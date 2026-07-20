using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using API;
using Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace API.IntegrationTests.Infrastructure
{
    /// <summary>
    /// In-process host harness for the integration-test suite. It boots the <b>real</b> ASP.NET Core
    /// application (the <c>API</c> project's <see cref="Startup"/>) via
    /// <see cref="WebApplicationFactory{TEntryPoint}"/> and drives it through a genuine
    /// <see cref="HttpClient"/>, exercising the full middleware/routing/authentication pipeline.
    ///
    /// <para>
    /// <b>Real infrastructure only (AAP §0.10.1).</b> This factory changes exactly two things about the
    /// application under test and nothing else:
    /// <list type="number">
    ///   <item>it injects the three connection strings (<c>DefaultConnection</c>,
    ///         <c>IdentityConnection</c>, <c>Redis</c>) so <see cref="Startup"/> binds to the
    ///         <b>Testcontainers</b> PostgreSQL and Redis endpoints supplied by
    ///         <c>ContainerFixture</c>; and</item>
    ///   <item>it swaps the production <see cref="IPaymentService"/> for the offline
    ///         <see cref="StripePaymentServiceStub"/> so no live Stripe call is ever made.</item>
    /// </list>
    /// The <c>StoreContext</c>, <c>AppIdentityDbContext</c> and Redis <c>IConnectionMultiplexer</c>
    /// registrations are deliberately left untouched — PostgreSQL, Redis and the HTTP transport all remain
    /// genuine. No production ports (<c>5432</c>/<c>6379</c>) are hardcoded; the endpoints are always the
    /// dynamic container endpoints assigned before the first client is created.
    /// </para>
    ///
    /// <para>
    /// <b>Migration/seed is NOT performed here.</b> <c>Program.Main</c> — which normally migrates and
    /// seeds both databases — is never executed by <see cref="WebApplicationFactory{TEntryPoint}"/> (the
    /// factory builds its own host from <c>Program.CreateHostBuilder</c> and skips <c>Main</c>). Applying
    /// migrations and seeding (<c>StoreContextSeed</c>/<c>AppIdentityDbContextSeed</c>) is the
    /// responsibility of <c>ContainerFixture</c>, which owns the container lifecycle.
    /// </para>
    ///
    /// <para>
    /// <b>Usage.</b> <c>ContainerFixture</c> constructs the factory, assigns
    /// <see cref="StoreConnectionString"/>, <see cref="IdentityConnectionString"/> and
    /// <see cref="RedisConnectionString"/> (and optionally <see cref="EnvironmentName"/>) BEFORE creating
    /// any client, then uses <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/> for anonymous
    /// requests and <see cref="CreateAuthenticatedClientAsync"/> for <c>[Authorize]</c> endpoints.
    /// </para>
    /// </summary>
    public class CustomWebApplicationFactory : WebApplicationFactory<Startup>
    {
        // --- Test configuration constants -------------------------------------------------------------
        // These are non-secret, test-only values. They are exposed as public constants so sibling test
        // classes and the container fixture can reference the exact values the host is configured with
        // (for example, the webhook test can sign an offline event with the same WhSecret).

        /// <summary>Default seeded integration-test user e-mail (created by <c>AppIdentityDbContextSeed</c>).</summary>
        public const string DefaultTestUserEmail = "bob@test.com";

        /// <summary>Default seeded integration-test user password.</summary>
        public const string DefaultTestUserPassword = "Pa$$w0rd";

        /// <summary>
        /// JWT signing key injected as <c>Token:Key</c>. It is long enough to satisfy the HMAC-SHA512
        /// signing that <c>TokenService</c> performs, and mirrors the source <c>appsettings</c> value so
        /// tokens minted in-process validate against the same key the app configures.
        /// </summary>
        public const string TestTokenKey = "super secret key";

        /// <summary>JWT issuer injected as <c>Token:Issuer</c> (used for both issuance and validation).</summary>
        public const string TestTokenIssuer = "https://localhost:5001";

        /// <summary>
        /// Value injected as <c>ApiUrl</c>. AutoMapper's <c>MappingProfiles</c> URL resolvers
        /// (<c>ProductUrlResolver</c>/<c>OrderItemUrlResolver</c>) read <c>IConfiguration["ApiUrl"]</c>;
        /// any non-null value keeps mapping configuration valid and URL composition working.
        /// </summary>
        public const string TestApiUrl = "https://localhost:5001/content/";

        /// <summary>
        /// TEST placeholder for <c>StripeSettings:SecretKey</c> — never a live key. Because
        /// <see cref="IPaymentService"/> is replaced by the offline stub, this key is never used against
        /// Stripe; it exists only so configuration binding succeeds.
        /// </summary>
        public const string TestStripeSecretKey = "sk_test_stub";

        /// <summary>
        /// TEST placeholder for <c>StripeSettings:WhSecret</c> — the webhook signing secret read by
        /// <c>PaymentsController</c>. A placeholder keeps the controller happy; the dedicated webhook test
        /// signs its offline event with this exact (public) value.
        /// </summary>
        public const string TestStripeWebhookSecret = "whsec_test_stub";

        /// <summary>
        /// Cached deserialization options: the login endpoint serialises <c>UserDto</c> in camelCase
        /// (<c>token</c>), so case-insensitive matching maps it onto the PascalCase response DTO.
        /// </summary>
        private static readonly JsonSerializerOptions JsonReadOptions =
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        /// <summary>
        /// Static initializer that guarantees the <c>Content</c> and <c>wwwroot</c> directories exist
        /// exactly once, before any host is built for the first time.
        /// </summary>
        static CustomWebApplicationFactory()
        {
            EnsureContentAndWwwrootDirectoriesExist();
        }

        // --- Public surface (contract for ContainerFixture + sibling test classes) --------------------

        /// <summary>
        /// Connection string for the Store database (<c>StoreContext</c>), injected as
        /// <c>ConnectionStrings:DefaultConnection</c>. Assigned by <c>ContainerFixture</c> to the
        /// Testcontainers PostgreSQL endpoint before the first client is created.
        /// </summary>
        public string StoreConnectionString { get; set; }

        /// <summary>
        /// Connection string for the Identity database (<c>AppIdentityDbContext</c>), injected as
        /// <c>ConnectionStrings:IdentityConnection</c>. Assigned by <c>ContainerFixture</c> to the
        /// Testcontainers PostgreSQL endpoint before the first client is created.
        /// </summary>
        public string IdentityConnectionString { get; set; }

        /// <summary>
        /// Redis connection string, injected as <c>ConnectionStrings:Redis</c>. Assigned by
        /// <c>ContainerFixture</c> to the Testcontainers Redis endpoint before the first client is created.
        /// </summary>
        public string RedisConnectionString { get; set; }

        /// <summary>
        /// Hosting environment name applied to the host. Defaults to <c>"Development"</c> so
        /// <c>ExceptionMiddleware</c> returns a structured <c>ApiException</c> 500 that INCLUDES the
        /// exception <c>message</c> and <c>stackTrace</c> (the middleware branches on
        /// <c>_env.IsDevelopment()</c>), which the <c>Resilience/FailClosedTests</c> assert on. A consumer
        /// that needs Production suppression semantics can set this to <c>"Production"</c> before creating
        /// a client.
        /// </summary>
        public string EnvironmentName { get; set; } = "Development";

        /// <summary>
        /// Configures the test host: sets the environment, overrides configuration to point the real
        /// application at the Testcontainers endpoints, and replaces only the Stripe payment service with
        /// the offline stub. Called by <see cref="WebApplicationFactory{TEntryPoint}"/> while the host is
        /// being built (lazily, on first client creation).
        /// </summary>
        /// <param name="builder">The web host builder supplied by the base factory.</param>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Defensive re-assert (in addition to the static ctor): the directories must exist in the
            // process working directory that Startup.Configure reads via Directory.GetCurrentDirectory().
            EnsureContentAndWwwrootDirectoriesExist();

            // Fail fast on misuse: the container endpoints must be supplied before a client is created so
            // the factory never silently falls back to production/docker-compose defaults.
            ValidateConnectionStrings();

            // Development => ExceptionMiddleware surfaces message + stackTrace in its 500 ApiException.
            builder.UseEnvironment(EnvironmentName);

            builder.ConfigureAppConfiguration((context, configBuilder) =>
            {
                // Appended LAST so these values win over the app's own appsettings*.json (later
                // configuration sources override earlier ones). This is how the genuine Startup is pointed
                // at the Testcontainers PostgreSQL/Redis endpoints without mocking any of them.
                var testConfiguration = new Dictionary<string, string>
                {
                    // Real infrastructure endpoints (from ContainerFixture; never the docker-compose ports).
                    ["ConnectionStrings:DefaultConnection"] = StoreConnectionString,
                    ["ConnectionStrings:IdentityConnection"] = IdentityConnectionString,
                    ["ConnectionStrings:Redis"] = RedisConnectionString,

                    // JWT signing/validation material (Startup.AddIdentityServices + TokenService).
                    ["Token:Key"] = TestTokenKey,
                    ["Token:Issuer"] = TestTokenIssuer,

                    // Required by AutoMapper MappingProfiles' URL resolvers.
                    ["ApiUrl"] = TestApiUrl,

                    // Stripe TEST placeholders only — never live keys. The payment service is stubbed, so
                    // the secret key is never used against Stripe; the webhook secret only needs to exist.
                    ["StripeSettings:SecretKey"] = TestStripeSecretKey,
                    ["StripeSettings:WhSecret"] = TestStripeWebhookSecret,
                };

                configBuilder.AddInMemoryCollection(testConfiguration);
            });

            builder.ConfigureTestServices(services =>
            {
                // Replace ONLY the Stripe-backed payment service with the offline stub. ConfigureTestServices
                // runs AFTER Startup.ConfigureServices, so the production
                // AddScoped<IPaymentService, PaymentService>() descriptor is already registered and can be
                // located and removed deterministically. Every other registration — StoreContext,
                // AppIdentityDbContext and the Redis IConnectionMultiplexer — is left untouched so
                // PostgreSQL, Redis and the HTTP transport remain genuine (AAP §0.10.1).
                var paymentServiceDescriptor =
                    services.SingleOrDefault(d => d.ServiceType == typeof(IPaymentService));
                if (paymentServiceDescriptor != null)
                {
                    services.Remove(paymentServiceDescriptor);
                }

                // MJ-09: register the stub as a SINGLETON (not Scoped). This makes delegation observable —
                // the instance the controller resolves within each request scope is the SAME instance a
                // webhook test resolves from Factory.Services, so the stub's recorded Calls reflect exactly
                // the controller's delegations. The stub holds only in-memory, thread-safe call metadata and
                // returns deterministic values (and has no injected dependencies), so a singleton lifetime is
                // safe: a scoped/transient consumer depending on a singleton is always a valid DI lifetime.
                services.AddSingleton<IPaymentService, StripePaymentServiceStub>();
            });

            // NOTE: Program.Main is intentionally NOT invoked and no migration/seed is run here — that is
            // ContainerFixture's responsibility.
        }

        /// <summary>
        /// Authenticates against the running application via <c>POST api/account/login</c> and returns the
        /// issued JWT. Assumes <c>ContainerFixture</c> has already migrated and seeded the identity
        /// database so the requested user exists.
        /// </summary>
        /// <param name="email">User e-mail; defaults to the seeded integration user.</param>
        /// <param name="password">User password; defaults to the seeded integration user's password.</param>
        /// <returns>The raw bearer token string.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown if the login request does not succeed or the response carries no token, so misuse (for
        /// example, requesting a token before the identity database is seeded) surfaces immediately.
        /// </exception>
        /// <remarks>
        /// Consumers that prefer to MINT a JWT directly (bypassing HTTP) can resolve
        /// <see cref="ITokenService"/> from <c>Services.CreateScope().ServiceProvider</c> and call
        /// <c>CreateToken(appUser)</c>; this login-based helper is the primary path.
        /// </remarks>
        public async Task<string> GetAuthTokenAsync(
            string email = DefaultTestUserEmail,
            string password = DefaultTestUserPassword)
        {
            // AllowAutoRedirect=false so any 307 from Startup's UseHttpsRedirection surfaces as the real
            // status code rather than being silently followed. This is a throwaway client used only to
            // obtain the token, so it is disposed once the token is read.
            using var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            // Case-insensitive server-side JSON binding maps { email, password } onto LoginDto{Email,Password}.
            var payload = JsonSerializer.Serialize(new { email, password });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await client.PostAsync("api/account/login", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                // MD-04: log only the response schema (top-level field names), never the raw body, which
                // could carry sensitive material on some failure envelopes.
                throw new InvalidOperationException(
                    $"Login failed for '{email}': HTTP {(int)response.StatusCode} ({response.StatusCode}). " +
                    "Ensure ContainerFixture has migrated and seeded the identity database (so the user " +
                    $"exists) before requesting a token. Response schema: {DescribeJsonSchema(errorBody)}");
            }

            var json = await response.Content.ReadAsStringAsync();
            var user = JsonSerializer.Deserialize<LoginTokenResponse>(json, JsonReadOptions);

            if (string.IsNullOrWhiteSpace(user?.Token))
            {
                // MD-04: a SUCCESSFUL login response body contains a valid JWT, so it must never be logged
                // verbatim. Emit only the response schema (field names present) so a genuinely missing/
                // mis-cased token field is still diagnosable without leaking a credential.
                throw new InvalidOperationException(
                    $"Login for '{email}' returned HTTP {(int)response.StatusCode} but no JWT was present " +
                    $"in the response. Response schema: {DescribeJsonSchema(json)} (body redacted — MD-04).");
            }

            return user.Token;
        }

        /// <summary>
        /// Creates an <see cref="HttpClient"/> pre-authenticated with a <c>Bearer</c> token for the given
        /// user, ready to call <c>[Authorize]</c> endpoints. The returned client is owned by the caller.
        /// </summary>
        /// <param name="email">User e-mail; defaults to the seeded integration user.</param>
        /// <param name="password">User password; defaults to the seeded integration user's password.</param>
        /// <returns>An authenticated <see cref="HttpClient"/>.</returns>
        public async Task<HttpClient> CreateAuthenticatedClientAsync(
            string email = DefaultTestUserEmail,
            string password = DefaultTestUserPassword)
        {
            var token = await GetAuthTokenAsync(email, password);

            // AllowAutoRedirect=false so tests observe the real status codes from [Authorize]/content routes.
            var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        /// <summary>
        /// Ensures the <c>Content</c> and <c>wwwroot</c> directories exist under the process working
        /// directory. <see cref="Startup"/><c>.Configure</c> constructs a
        /// <c>PhysicalFileProvider(Path.Combine(Directory.GetCurrentDirectory(), "Content"))</c> whose
        /// constructor throws <see cref="DirectoryNotFoundException"/> when the directory is absent — that
        /// would abort host startup before any request is served. <see cref="Startup"/> also calls the
        /// parameterless <c>app.UseStaticFiles()</c> (serving <c>wwwroot</c>) and maps a fallback
        /// controller whose <c>Index</c> action reads <c>wwwroot/index.html</c>. The integration host runs
        /// from the test project's <c>bin</c> directory, where neither directory exists, so both are
        /// created here. <see cref="Directory.CreateDirectory(string)"/> is idempotent (a no-op when the
        /// directory already exists).
        /// </summary>
        private static void EnsureContentAndWwwrootDirectoriesExist()
        {
            var currentDirectory = Directory.GetCurrentDirectory();
            Directory.CreateDirectory(Path.Combine(currentDirectory, "Content"));
            Directory.CreateDirectory(Path.Combine(currentDirectory, "wwwroot"));
        }

        /// <summary>
        /// Verifies that all three container-backed connection strings have been assigned before the host
        /// is built, guaranteeing the factory never silently falls back to production/docker-compose
        /// defaults and honoring the "no hardcoded 5432/6379" constraint.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if any connection string is null or blank.</exception>
        private void ValidateConnectionStrings()
        {
            if (string.IsNullOrWhiteSpace(StoreConnectionString) ||
                string.IsNullOrWhiteSpace(IdentityConnectionString) ||
                string.IsNullOrWhiteSpace(RedisConnectionString))
            {
                throw new InvalidOperationException(
                    "CustomWebApplicationFactory requires StoreConnectionString, IdentityConnectionString " +
                    "and RedisConnectionString to be set to the Testcontainers endpoints BEFORE the first " +
                    "client is created. ContainerFixture owns these dynamic connection strings; the factory " +
                    "must never fall back to the shared docker-compose ports (5432/6379).");
            }
        }

        /// <summary>
        /// Produces a redacted, non-sensitive description of a JSON response for diagnostics (MD-04): only
        /// the top-level property NAMES (the response schema) are reported — never their values — so a
        /// message can convey response shape without ever leaking a JWT or other sensitive field. Returns a
        /// safe placeholder for empty, non-object, or unparseable payloads.
        /// </summary>
        /// <param name="json">The raw response body to describe (its values are never emitted).</param>
        /// <returns>A schema description such as <c>fields: [token, email, displayName]</c>.</returns>
        private static string DescribeJsonSchema(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return "<empty response body>";
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return $"<non-object {document.RootElement.ValueKind} payload>";
                }

                var fieldNames = document.RootElement.EnumerateObject().Select(property => property.Name);
                return $"fields: [{string.Join(", ", fieldNames)}]";
            }
            catch (JsonException)
            {
                return "<unparseable JSON body>";
            }
        }

        /// <summary>
        /// Minimal projection of the login response used solely to extract the JWT. Kept private so the
        /// factory stays decoupled from the production <c>UserDto</c> shape while still reading the
        /// camelCase <c>token</c> field (via <see cref="JsonReadOptions"/>).
        /// </summary>
        private sealed class LoginTokenResponse
        {
            public string Token { get; set; }
        }
    }
}
