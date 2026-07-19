using System;
using System.Net.Http;
using System.Threading.Tasks;
using Core.Entities.Identity;
using Infrastructure.Data;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
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
    /// Shared xUnit fixture that provisions <b>real, isolated, disposable</b> PostgreSQL and Redis
    /// instances via <b>Testcontainers</b>, wires a <see cref="CustomWebApplicationFactory"/> to those
    /// dynamic endpoints, applies the production EF Core migrations, and seeds data — replicating the
    /// <c>API/Program.cs</c> <c>Main</c> bootstrap — so every integration-test class in the shared
    /// collection runs against a ready, genuine environment (AAP §0.4.4, §0.5.2, §0.7.2).
    ///
    /// <para>
    /// <b>Lifecycle.</b> Implementing <see cref="IAsyncLifetime"/> lets xUnit start the containers
    /// <b>once per collection</b> (<see cref="InitializeAsync"/>) and tear them down at the end
    /// (<see cref="DisposeAsync"/>). Bound to sibling classes through an <c>ICollectionFixture</c>, the
    /// (expensive) container startup and migrate/seed cost is amortised across the whole collection.
    /// </para>
    ///
    /// <para>
    /// <b>Real infrastructure only (AAP §0.10.1).</b> Nothing here is mocked — genuine PostgreSQL and
    /// Redis are used. The containers are provisioned with <b>dynamically-assigned host ports</b> (never
    /// the shared <c>docker-compose.yml</c> ports <c>5432</c>/<c>6379</c>, and never the compose stack
    /// itself). Only the engine images and credentials mirror <c>docker-compose.yml</c> for parity: the
    /// <c>postgres</c> image with user <c>appuser</c> / password <c>secret</c>, and <c>redis:latest</c>.
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
    /// <b>FailClosedTests nuance.</b> Because this fixture is <b>shared</b> across the <c>"Integration"</c>
    /// collection, a test that deliberately <i>stops</i> a container mid-test (for example
    /// <c>Resilience/FailClosedTests</c>, which stops PostgreSQL or Redis to assert a fail-closed,
    /// structured <c>ApiException</c> 500) would disrupt every sibling class sharing these containers.
    /// Such a test must therefore construct its <b>own</b> non-shared <see cref="ContainerFixture"/>
    /// instance (via the public parameterless constructor and its own <see cref="IAsyncLifetime"/>),
    /// <b>outside</b> the shared collection, and drive <see cref="PostgresContainer"/> /
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

        /// <summary>PostgreSQL image, matching <c>docker-compose.yml</c> (<c>image: postgres</c>).</summary>
        private const string PostgresImage = "postgres:latest";

        /// <summary>Redis image, matching <c>docker-compose.yml</c> (<c>redis:latest</c>).</summary>
        private const string RedisImage = "redis:latest";

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
        /// Public parameterless constructor. Required by xUnit's <c>ICollectionFixture&lt;T&gt;</c> (xUnit
        /// instantiates the fixture reflectively) and also lets specialised classes such as
        /// <c>Resilience/FailClosedTests</c> <c>new</c> a private, non-shared instance so they can stop a
        /// container without disturbing the shared collection. The containers are only <i>defined</i> here;
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
        /// </summary>
        public HttpClient CreateClient() => Factory.CreateClient();

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
        /// Called once by xUnit before any test in the collection runs. Starts both containers (relying on
        /// their built-in readiness wait strategies — no <c>Thread.Sleep</c>), provisions the second
        /// (Identity) database on the same PostgreSQL server, computes the three connection strings, wires
        /// the <see cref="CustomWebApplicationFactory"/>, and finally applies migrations and seeds both
        /// databases — replicating the <c>API/Program.cs</c> bootstrap so tests see the documented seed
        /// data (6 brands / 4 types / 18 products / 4 delivery methods and the <c>bob@test.com</c> user).
        /// </summary>
        public async Task InitializeAsync()
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
            //    Unlike Program.Main — which logs and CONTINUES on failure so the site still starts — a
            //    test fixture must fail LOUDLY: a broken migration/seed means the environment is not ready,
            //    so the caught exception is logged and then rethrown to abort collection initialisation.
            using var scope = Factory.Services.CreateScope();
            var services = scope.ServiceProvider;
            var loggerFactory = services.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger<ContainerFixture>();

            try
            {
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
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "ContainerFixture failed to migrate/seed the Testcontainers databases during " +
                    "InitializeAsync; aborting collection initialisation so the failure surfaces loudly.");
                throw;
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

            using var command = new NpgsqlCommand($"CREATE DATABASE {IdentityDatabaseName};", connection);
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Called once by xUnit after all tests in the collection have run. Disposes the factory first so
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
