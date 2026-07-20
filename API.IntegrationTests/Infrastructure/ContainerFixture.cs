using System;
using System.Net.Http;
using System.Threading.Tasks;
using Core.Entities.Identity;
using Infrastructure.Data;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Xunit;

namespace API.IntegrationTests.Infrastructure
{
    /// <summary>
    /// Per-class xUnit fixture that provisions <b>real, isolated, disposable</b> PostgreSQL and Redis
    /// instances via <b>Testcontainers</b>, wires a <see cref="CustomWebApplicationFactory"/> to those
    /// dynamic endpoints, applies the production EF Core migrations, and seeds data — replicating the
    /// <c>API/Program.cs</c> <c>Main</c> bootstrap — so each integration-test class runs against its OWN
    /// ready, genuine environment (AAP §0.10.1 per-class isolation; §0.4.4, §0.5.2, §0.7.2).
    ///
    /// <para>
    /// <b>Lifecycle.</b> Implementing <see cref="IAsyncLifetime"/> lets xUnit start the containers
    /// <b>once per consuming class</b> (<see cref="InitializeAsync"/>, before that class's first test) and
    /// tear them down after its last test (<see cref="DisposeAsync"/>). It is consumed as a per-class
    /// <c>IClassFixture&lt;ContainerFixture&gt;</c> (CR-01), so each class owns its own container pair; the
    /// (expensive) startup and migrate/seed cost is amortised across that class's tests. Assembly-wide test
    /// parallelization is disabled (see <c>AssemblyInfo.cs</c>) so these per-class container pairs start
    /// sequentially rather than all at once.
    /// </para>
    ///
    /// <para>
    /// <b>Real infrastructure only (AAP §0.10.1).</b> Nothing here is mocked — genuine PostgreSQL and
    /// Redis are used. The containers are provisioned with <b>dynamically-assigned host ports</b> (never
    /// the shared <c>docker-compose.yml</c> ports <c>5432</c>/<c>6379</c>, and never the compose stack
    /// itself). Only the engine images and credentials mirror <c>docker-compose.yml</c> for parity: the
    /// <c>postgres</c> image (pinned by immutable digest — see <see cref="PostgresImage"/>) with user
    /// <c>appuser</c> / password <c>secret</c>, and the digest-pinned Redis image (<see cref="RedisImage"/>).
    /// </para>
    ///
    /// <para>
    /// <b>Readiness via wait strategies, never sleeps.</b> The Testcontainers module builders attach a
    /// built-in readiness <c>WaitStrategy</c> that <i>polls</i> until the service accepts connections, so
    /// <see cref="InitializeAsync"/> only returns once both services are genuinely ready. There is no
    /// fixed <c>Thread.Sleep</c> anywhere, which keeps the suite deterministic (100% pass, zero flakiness
    /// across repeated runs).
    /// </para>
    ///
    /// <para>
    /// <b>Two databases on one server.</b> The application binds <c>DefaultConnection</c> to the Store
    /// database (<see cref="StoreContext"/>) and <c>IdentityConnection</c> to the Identity database
    /// (<see cref="AppIdentityDbContext"/>). To stay resource-efficient, a <b>single</b>
    /// <see cref="PostgreSqlContainer"/> hosts both: it is created with the default database
    /// <c>e-commerce</c> (Store), and a second database <c>identity</c> is created on the same server
    /// immediately after startup. The Identity connection string is derived from the Store one by
    /// switching only the database name, so both point at the same dynamic host port.
    /// </para>
    ///
    /// <para>
    /// <b>FailClosedTests nuance.</b> A test that deliberately <i>stops</i> a container mid-test (for
    /// example <c>Resilience/FailClosedTests</c>, which stops PostgreSQL or Redis to assert a fail-closed,
    /// structured <c>ApiException</c> 500) must never share a container with any other test. That class
    /// therefore constructs its <b>own</b> <see cref="ContainerFixture"/> instance <b>per test method</b>
    /// (via the public parameterless constructor and its own <see cref="IAsyncLifetime"/>) rather than
    /// consuming the per-class fixture, and drives <see cref="PostgresContainer"/> /
    /// <see cref="RedisContainer"/> <c>StopAsync()</c> without affecting others. The
    /// <see cref="PostgresContainer"/> and <see cref="RedisContainer"/> handles are exposed precisely to
    /// enable that pattern.
    /// </para>
    ///
    /// <para>
    /// <b>Prerequisite.</b> A running Docker daemon is required; the fixture assumes Docker is available
    /// and lets Testcontainers manage the container lifecycle.
    /// </para>
    /// </summary>
    public class ContainerFixture : IAsyncLifetime
    {
        // --- Engine/credential parity with docker-compose.yml (REFERENCE ONLY; the compose stack is
        //     never reused). These are non-secret, test-only values. -------------------------------------

        // Container images are pinned by IMMUTABLE DIGEST (@sha256), never by a mutable rolling tag such
        // as ":latest". This makes `docker pull` deterministic across environments and over time: a clean
        // host resolves the exact same image bytes forever, so migrations/seeding/behaviour cannot silently
        // drift when the upstream ":latest" (or even a floating major) is republished. This mirrors the
        // suite's version-pinning philosophy for every other tool (AAP §0.2.2 / §0.7.2 — e.g. Testcontainers
        // 3.9.0, Moq 4.18.4, FluentAssertions 6.12.0 are all pinned exactly) and resolves QA finding F-1.
        //
        // The chosen digests resolve to engine majors aligned with the net5-era production drivers the
        // application actually targets — PostgreSQL 13.x (Npgsql.EntityFrameworkCore.PostgreSQL 5.0.7, the
        // PG 9.6–13 era) and Redis 6.x (StackExchange.Redis 2.2.62) — which also matches the engine family
        // used by docker-compose.yml (REFERENCE ONLY; the compose stack itself is never reused — these
        // tests always run against isolated, disposable containers on dynamically-assigned host ports).
        // To re-pin (e.g. for a security patch) run `docker pull postgres:13` / `docker pull redis:6` and
        // copy the resulting `RepoDigests` value here, keeping the human-readable major in this comment.

        /// <summary>
        /// PostgreSQL image, pinned by immutable digest for reproducibility (resolves to <b>PostgreSQL
        /// 13.x</b>, the net5-era Npgsql 5.0.7 driver family; engine parity with <c>docker-compose.yml</c>).
        /// </summary>
        private const string PostgresImage =
            "postgres@sha256:4689940c683801b4ab839ab3b0a0a3555a5fe425371422310944e89eca7d8068";

        /// <summary>
        /// Redis image, pinned by immutable digest for reproducibility (resolves to <b>Redis 6.x</b>, the
        /// net5-era StackExchange.Redis 2.2.62 driver family; engine parity with <c>docker-compose.yml</c>).
        /// </summary>
        private const string RedisImage =
            "redis@sha256:e608fb94319e1aa53cecadbb051faa36d5c495f1142b99ca6673b41c2b428c0a";

        /// <summary>PostgreSQL superuser name (parity with <c>POSTGRES_USER</c> in <c>docker-compose.yml</c>).</summary>
        private const string PostgresUsername = "appuser";

        /// <summary>PostgreSQL password (parity with <c>POSTGRES_PASSWORD</c> in <c>docker-compose.yml</c>).</summary>
        private const string PostgresPassword = "secret";

        /// <summary>Default (Store) database name bound to <c>ConnectionStrings:DefaultConnection</c>.</summary>
        private const string StoreDatabaseName = "e-commerce";

        /// <summary>Second (Identity) database name bound to <c>ConnectionStrings:IdentityConnection</c>.</summary>
        private const string IdentityDatabaseName = "identity";

        private readonly PostgreSqlContainer _pg;
        private readonly RedisContainer _redis;

        /// <summary>
        /// Public parameterless constructor. Required by xUnit's <c>IClassFixture&lt;T&gt;</c> (xUnit
        /// instantiates the fixture reflectively, once per consuming class) and also lets specialised classes
        /// such as <c>Resilience/FailClosedTests</c> <c>new</c> a private instance per test so they can stop
        /// a container without disturbing any other test. The containers are only <i>defined</i> here;
        /// they are not started until <see cref="InitializeAsync"/> so construction stays cheap and
        /// side-effect free.
        /// </summary>
        public ContainerFixture()
        {
            // Store database container: dynamic host port (assigned by Docker at StartAsync); the
            // PostgreSqlBuilder attaches a built-in readiness wait strategy — no Thread.Sleep required.
            _pg = new PostgreSqlBuilder()
                .WithImage(PostgresImage)
                .WithUsername(PostgresUsername)
                .WithPassword(PostgresPassword)
                .WithDatabase(StoreDatabaseName)
                .Build();

            // Redis cache container: dynamic host port; the RedisBuilder likewise attaches a built-in
            // readiness wait strategy.
            _redis = new RedisBuilder()
                .WithImage(RedisImage)
                .Build();
        }

        // --- Public surface (CONTRACT consumed by sibling integration-test classes — keep names stable) --

        /// <summary>
        /// The wired in-process host harness. Assigned during <see cref="InitializeAsync"/> once the
        /// container endpoints are known and pointed at the Testcontainers PostgreSQL/Redis instances.
        /// Sibling classes obtain HTTP clients from this factory (directly or via the pass-throughs below).
        /// </summary>
        public CustomWebApplicationFactory Factory { get; private set; }

        /// <summary>
        /// Store database connection string (<c>ConnectionStrings:DefaultConnection</c>), pointing at the
        /// dynamic Testcontainers PostgreSQL endpoint with database <c>e-commerce</c>.
        /// </summary>
        public string StoreConnectionString { get; private set; }

        /// <summary>
        /// Identity database connection string (<c>ConnectionStrings:IdentityConnection</c>), pointing at
        /// the same PostgreSQL server as <see cref="StoreConnectionString"/> but database <c>identity</c>.
        /// </summary>
        public string IdentityConnectionString { get; private set; }

        /// <summary>
        /// Redis connection string (<c>ConnectionStrings:Redis</c>) in <c>host:port</c> form, pointing at
        /// the dynamic Testcontainers Redis endpoint.
        /// </summary>
        public string RedisConnectionString { get; private set; }

        /// <summary>
        /// The underlying PostgreSQL container handle. Exposed so a non-shared consumer (for example
        /// <c>Resilience/FailClosedTests</c>) can <c>StopAsync()</c>/<c>StartAsync()</c> it mid-test to
        /// simulate a database outage.
        /// </summary>
        public PostgreSqlContainer PostgresContainer => _pg;

        /// <summary>
        /// The underlying Redis container handle. Exposed so a non-shared consumer can
        /// <c>StopAsync()</c>/<c>StartAsync()</c> it mid-test to simulate a cache outage.
        /// </summary>
        public RedisContainer RedisContainer => _redis;

        // --- Convenience pass-throughs to the wired factory ------------------------------------------

        /// <summary>
        /// Creates an anonymous <see cref="HttpClient"/> against the running application. Convenience
        /// pass-through to <see cref="CustomWebApplicationFactory"/>.
        ///
        /// <para>
        /// <b>Explicit, tested redirect policy (MD-03).</b> The client is created with
        /// <c>AllowAutoRedirect = false</c> so that any <c>UseHttpsRedirection</c> 307 surfaces as the real
        /// status code instead of being silently followed. Under the in-process test host no HTTPS port is
        /// configured, so redirection is inoperative and endpoints answer over HTTP directly; disabling
        /// auto-redirect makes that contract explicit and guarantees the suite asserts the genuine response
        /// (never a transparently-followed redirect). This matches the authenticated-client behaviour in
        /// <see cref="CustomWebApplicationFactory.CreateAuthenticatedClientAsync"/>.
        /// </para>
        /// </summary>
        public HttpClient CreateClient() =>
            Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        /// <summary>
        /// Creates an <see cref="HttpClient"/> pre-authenticated with a <c>Bearer</c> token for the given
        /// seeded user, ready to call <c>[Authorize]</c> endpoints. Convenience pass-through to
        /// <see cref="CustomWebApplicationFactory.CreateAuthenticatedClientAsync"/>.
        /// </summary>
        /// <param name="email">User e-mail; defaults to the seeded integration user (<c>bob@test.com</c>).</param>
        /// <param name="password">User password; defaults to the seeded integration user's password.</param>
        public Task<HttpClient> CreateAuthenticatedClientAsync(
            string email = CustomWebApplicationFactory.DefaultTestUserEmail,
            string password = CustomWebApplicationFactory.DefaultTestUserPassword)
            => Factory.CreateAuthenticatedClientAsync(email, password);

        /// <summary>
        /// Authenticates against <c>POST api/account/login</c> and returns the issued JWT. Convenience
        /// pass-through to <see cref="CustomWebApplicationFactory.GetAuthTokenAsync"/>.
        /// </summary>
        /// <param name="email">User e-mail; defaults to the seeded integration user (<c>bob@test.com</c>).</param>
        /// <param name="password">User password; defaults to the seeded integration user's password.</param>
        public Task<string> GetAuthTokenAsync(
            string email = CustomWebApplicationFactory.DefaultTestUserEmail,
            string password = CustomWebApplicationFactory.DefaultTestUserPassword)
            => Factory.GetAuthTokenAsync(email, password);

        // --- IAsyncLifetime -----------------------------------------------------------------------------

        /// <summary>
        /// Called once by xUnit before the first test in each consuming test class runs (this fixture is
        /// consumed as a per-class <c>IClassFixture&lt;ContainerFixture&gt;</c>, so every class receives its
        /// own isolated PostgreSQL+Redis pair — AAP §0.10.1). Starts both containers (relying on their
        /// built-in readiness wait strategies — no <c>Thread.Sleep</c>), provisions the second (Identity)
        /// database on the same PostgreSQL server, computes the three connection strings, wires the
        /// <see cref="CustomWebApplicationFactory"/>, applies migrations and seeds both databases —
        /// replicating the <c>API/Program.cs</c> bootstrap so tests see the documented seed data (6 brands /
        /// 4 types / 18 products / 4 delivery methods and the <c>bob@test.com</c> user) — and finally
        /// verifies those seed postconditions fail-loud before declaring readiness.
        ///
        /// <para>
        /// The entire sequence is wrapped so a failure at any step reverse-disposes whatever was already
        /// started before rethrowing (MJ-02): xUnit does not invoke <see cref="DisposeAsync"/> when
        /// <see cref="InitializeAsync"/> throws, so cleanup must happen here to avoid leaking containers.
        /// </para>
        /// </summary>
        public async Task InitializeAsync()
        {
            // The COMPLETE initialisation sequence is wrapped so a failure at ANY step (container start,
            // Identity-database creation, host build, migration, seed, or seed verification) reverse-disposes
            // whatever has already been acquired before rethrowing (MJ-02). xUnit does NOT call DisposeAsync
            // when InitializeAsync throws, so without this the already-started PostgreSQL/Redis containers and
            // the wired factory would leak for the remainder of the test run.
            try
            {
                // 1) Start the PostgreSQL container. StartAsync only returns once the module's built-in wait
                //    strategy reports the server is accepting connections (a polling wait strategy, not a sleep).
                await _pg.StartAsync();

                // 2) Start the Redis container (same built-in readiness guarantee).
                await _redis.StartAsync();

                // 3) Compute the connection strings and provision the second database on the SAME server.
                //    GetConnectionString() carries the dynamic host port assigned by Docker (never 5432/6379).
                StoreConnectionString = _pg.GetConnectionString();

                // The Store container ships a single database (e-commerce). The application also needs a
                // separate Identity database, so create it now on the same server. CREATE DATABASE cannot run
                // inside a transaction, so it is issued over a direct autocommit Npgsql connection.
                await CreateIdentityDatabaseAsync(StoreConnectionString);

                // Derive the Identity connection string from the Store one by switching only the database name
                // (same host/port/credentials, different catalogue).
                IdentityConnectionString =
                    new NpgsqlConnectionStringBuilder(StoreConnectionString) { Database = IdentityDatabaseName }
                        .ConnectionString;

                // Redis returns a host:port connection string parseable by StackExchange.Redis' ConfigurationOptions.
                RedisConnectionString = _redis.GetConnectionString();

                // 4) Wire the in-process host harness to the dynamic Testcontainers endpoints BEFORE its host is
                //    built. The factory validates these three values on first host build and never falls back to
                //    the docker-compose defaults.
                Factory = new CustomWebApplicationFactory
                {
                    StoreConnectionString = StoreConnectionString,
                    IdentityConnectionString = IdentityConnectionString,
                    RedisConnectionString = RedisConnectionString
                };

                // 5) Migrate + seed BOTH contexts, replicating API/Program.cs Main. Accessing Factory.Services
                //    lazily builds the real host (running Startup.ConfigureServices) against the real endpoints.
                using var scope = Factory.Services.CreateScope();
                var services = scope.ServiceProvider;
                var loggerFactory = services.GetRequiredService<ILoggerFactory>();

                // Store database: apply the Npgsql-specific migrations, then seed reference data. The seed
                // JSON files (Data/SeedData/*.json) are link-copied into this test project's output by
                // API.IntegrationTests.csproj so StoreContextSeed can locate them beside Infrastructure.dll.
                var storeContext = services.GetRequiredService<StoreContext>();
                await storeContext.Database.MigrateAsync();
                await StoreContextSeed.SeedAsync(storeContext, loggerFactory);

                // Identity database: apply the Identity migrations, then seed the default integration user
                // (bob@test.com / Pa$$w0rd) with its pre-seeded address.
                var identityContext = services.GetRequiredService<AppIdentityDbContext>();
                await identityContext.Database.MigrateAsync();
                var userManager = services.GetRequiredService<UserManager<AppUser>>();
                await AppIdentityDbContextSeed.SeedUserAsync(userManager);

                // 6) Fail-LOUD seed verification (MJ-03). StoreContextSeed.SeedAsync catches and logs its own
                //    exceptions internally and never rethrows, so a partial/skipped seed would otherwise leave
                //    the fixture "ready" with missing reference data and produce confusing downstream failures.
                //    Assert the documented postconditions before declaring readiness.
                await VerifySeedPostconditionsAsync(storeContext, userManager);
            }
            catch
            {
                // MJ-02: reverse-dispose everything already acquired (factory -> Redis -> PostgreSQL) so a
                //    partial initialisation leaks nothing, then rethrow so the failure aborts this class's
                //    initialisation loudly (the original exception is preserved and surfaced by xUnit).
                await DisposePartialInitializationAsync();
                throw;
            }
        }

        /// <summary>
        /// Fail-loud verification (MJ-03) that both databases were seeded with the documented reference data
        /// before the fixture declares itself ready. <see cref="StoreContextSeed"/> swallows its own
        /// exceptions (it logs and returns rather than rethrowing), so an under-seeded database would not
        /// otherwise surface until confusing downstream assertions failed. Throws
        /// <see cref="InvalidOperationException"/> if any documented postcondition is unmet.
        /// </summary>
        private static async Task VerifySeedPostconditionsAsync(
            StoreContext storeContext,
            UserManager<AppUser> userManager)
        {
            // Documented seed set (AAP §0.4.4; tech-spec §6.2.2): 6 brands, 4 types, 18 products, 4 methods.
            const int expectedBrands = 6;
            const int expectedTypes = 4;
            const int expectedProducts = 18;
            const int expectedDeliveryMethods = 4;

            var brands = await storeContext.ProductBrands.CountAsync();
            var types = await storeContext.ProductTypes.CountAsync();
            var products = await storeContext.Products.CountAsync();
            var deliveryMethods = await storeContext.DeliveryMethods.CountAsync();

            if (brands != expectedBrands || types != expectedTypes ||
                products != expectedProducts || deliveryMethods != expectedDeliveryMethods)
            {
                throw new InvalidOperationException(
                    "ContainerFixture seed verification FAILED: the Store database was not seeded with the " +
                    $"documented reference data. Expected {expectedBrands} brands / {expectedTypes} types / " +
                    $"{expectedProducts} products / {expectedDeliveryMethods} delivery methods but found " +
                    $"{brands} / {types} / {products} / {deliveryMethods}. StoreContextSeed.SeedAsync " +
                    "swallows its own exceptions, so this check prevents tests running against an " +
                    "under-seeded database.");
            }

            // The Identity user (bob@test.com) and its pre-seeded Address underpin the address/checkout and
            // authenticated-order contract tests; verify both are present (the Address is a related entity,
            // so it must be eagerly loaded to confirm it exists).
            var seededUser = await userManager.Users
                .Include(u => u.Address)
                .SingleOrDefaultAsync(u => u.Email == CustomWebApplicationFactory.DefaultTestUserEmail);

            if (seededUser is null)
            {
                throw new InvalidOperationException(
                    "ContainerFixture seed verification FAILED: the integration user " +
                    $"'{CustomWebApplicationFactory.DefaultTestUserEmail}' was not created by " +
                    "AppIdentityDbContextSeed.SeedUserAsync.");
            }

            if (seededUser.Address is null)
            {
                throw new InvalidOperationException(
                    "ContainerFixture seed verification FAILED: the integration user " +
                    $"'{CustomWebApplicationFactory.DefaultTestUserEmail}' was created without the " +
                    "pre-seeded Address required by the address/checkout contract tests.");
            }
        }

        /// <summary>
        /// Reverse-disposes any resources already acquired by a PARTIALLY-completed
        /// <see cref="InitializeAsync"/> (MJ-02): the factory first (releasing host connections, DbContexts
        /// and the Redis multiplexer), then the Redis container, then the PostgreSQL container. Secondary
        /// failures during cleanup are written to stderr and swallowed so they cannot mask the original
        /// initialisation exception, which the caller rethrows.
        /// </summary>
        private async Task DisposePartialInitializationAsync()
        {
            if (Factory != null)
            {
                try
                {
                    Factory.Dispose();
                }
                catch (Exception disposeEx)
                {
                    Console.Error.WriteLine(
                        "[ContainerFixture] Ignoring factory-dispose error during failed " +
                        $"initialisation: {disposeEx.Message}");
                }

                Factory = null;
            }

            try
            {
                await _redis.DisposeAsync();
            }
            catch (Exception disposeEx)
            {
                Console.Error.WriteLine(
                    "[ContainerFixture] Ignoring Redis-container-dispose error during failed " +
                    $"initialisation: {disposeEx.Message}");
            }

            try
            {
                await _pg.DisposeAsync();
            }
            catch (Exception disposeEx)
            {
                Console.Error.WriteLine(
                    "[ContainerFixture] Ignoring PostgreSQL-container-dispose error during failed " +
                    $"initialisation: {disposeEx.Message}");
            }
        }

        /// <summary>
        /// Creates the second (Identity) database on the running PostgreSQL server. Uses a direct Npgsql
        /// connection because <c>CREATE DATABASE</c> cannot execute inside a transaction; issuing it over a
        /// plain command runs it in autocommit mode. The connecting user (<c>appuser</c>) is a superuser in
        /// the <c>postgres</c> image and therefore has the privilege to create databases.
        /// </summary>
        /// <param name="storeConnectionString">
        /// The Store connection string (database <c>e-commerce</c>) used to reach the server; the new
        /// database is created on the same server, not within the connected catalogue.
        /// </param>
        private static async Task CreateIdentityDatabaseAsync(string storeConnectionString)
        {
            await using var connection = new NpgsqlConnection(storeConnectionString);
            await connection.OpenAsync();

            // Defensive/idempotent guard (resolves QA finding I-1). PostgreSQL has no
            // `CREATE DATABASE IF NOT EXISTS`, and a bare CREATE against an already-existing catalogue
            // throws SQLSTATE 42P04. In the normal lifecycle this path is reached exactly once — each
            // ContainerFixture owns a fresh, empty container and xUnit invokes InitializeAsync a single
            // time — so `identity` is guaranteed absent here. This existence check simply makes a repeat
            // invocation safe (a no-op) so the method is robust regardless of how it is called. The
            // database name is compared as a parameter value (safe from injection); it cannot be
            // parameterised in the CREATE statement below because it is an identifier, but it is a
            // compile-time constant (not user input), so interpolation there is safe.
            await using (var existsCommand = new NpgsqlCommand(
                "SELECT 1 FROM pg_database WHERE datname = @databaseName;", connection))
            {
                existsCommand.Parameters.AddWithValue("databaseName", IdentityDatabaseName);
                var alreadyExists = await existsCommand.ExecuteScalarAsync();
                if (alreadyExists != null)
                {
                    return;
                }
            }

            // CREATE DATABASE cannot execute inside a transaction; issuing it over a plain command on this
            // open connection runs it in autocommit mode.
            using var createCommand =
                new NpgsqlCommand($"CREATE DATABASE {IdentityDatabaseName};", connection);
            await createCommand.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Called once by xUnit after the consuming class's last test has run. Disposes the factory first so
        /// the in-process host, its Redis multiplexer and its <c>DbContext</c>s release their connections
        /// before the containers are torn down, then disposes both containers (which removes them entirely,
        /// honouring the isolated-and-disposable constraint).
        /// </summary>
        public async Task DisposeAsync()
        {
            // WebApplicationFactory : IDisposable — dispose it before the containers so no lingering
            // connection outlives the server it targets.
            Factory?.Dispose();

            // Dispose the containers (in the reverse order they were started). DisposeAsync stops and
            // removes each container.
            await _redis.DisposeAsync();
            await _pg.DisposeAsync();
        }
    }
}
