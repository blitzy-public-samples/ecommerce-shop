using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using API;                                       // Program, Startup
using Core.Entities.Identity;                    // AppUser
using FluentAssertions;
using Infrastructure.Data;                       // StoreContext, StoreContextSeed
using Infrastructure.Identity;                   // AppIdentityDbContext, AppIdentityDbContextSeed
using Microsoft.AspNetCore.Hosting.Server;       // IServer
using Microsoft.AspNetCore.TestHost;             // TestServer (transitive via Mvc.Testing 5.0.17)
using Microsoft.AspNetCore.Identity;             // UserManager
using Microsoft.EntityFrameworkCore;             // MigrateAsync extension (on DatabaseFacade)
using Microsoft.Extensions.Configuration;        // AddInMemoryCollection
using Microsoft.Extensions.DependencyInjection;  // CreateScope, GetRequiredService, AddSingleton
using Microsoft.Extensions.Hosting;              // IHost, Host, StartAsync/StopAsync, IHostBuilder
using Microsoft.Extensions.Logging;              // ILoggerFactory, CreateLogger, LogError
using Xunit;

namespace API.IntegrationTests.Resilience
{
    /// <summary>
    /// CRITICAL-PATH resilience tests for <b>startup migration-failure isolation</b>.
    ///
    /// <para>
    /// <c>API/Program.cs</c> <c>Main</c> wraps the EF Core <c>MigrateAsync</c> + seed for BOTH the
    /// <see cref="StoreContext"/> and the <see cref="AppIdentityDbContext"/> inside a single
    /// <c>try/catch (Exception)</c> that logs <c>"An error occured during migration"</c> and then
    /// <b>CONTINUES</b> to <c>host.Run()</c>. The contract under test is therefore: a migration
    /// failure must be <b>isolated</b> — the web host must still start rather than crash the process.
    /// </para>
    ///
    /// <para>
    /// These tests deliberately point migration at an <b>unreachable database endpoint</b> so
    /// <c>MigrateAsync</c> throws. They require <b>NO Docker / NO Testcontainers</b> — an unreachable
    /// endpoint needs no container. Accordingly this class is intentionally <b>NOT</b> part of the
    /// shared <c>"Integration"</c> collection and does <b>NOT</b> reference the shared
    /// <c>ContainerFixture</c>; adding a <c>[Collection]</c> attribute here would needlessly serialise
    /// it against the Testcontainers-backed suite.
    /// </para>
    ///
    /// <para>
    /// The migrate/seed block is <b>replicated</b> (not invoked) from <c>Program.Main</c> because
    /// (a) <c>Program.Main(args)</c> ends with <c>host.Run()</c>, which blocks until shutdown and is
    /// unusable from a test, and (b) <c>WebApplicationFactory&lt;Startup&gt;</c> builds the host from
    /// <c>Program.CreateHostBuilder</c> but <b>never executes <c>Main</c>'s migrate/seed block</b>, so
    /// it cannot exercise the isolation behaviour. Faithfully replicating the block is the only way to
    /// drive the exact code path under test. No production file is modified.
    /// </para>
    ///
    /// <para>
    /// Naming follows the repository convention <c>MethodName_StateUnderTest_ExpectedBehavior</c>, and
    /// each test is structured Arrange-Act-Assert with FluentAssertions.
    /// </para>
    /// </summary>
    public class StartupMigrationIsolationTests
    {
        /// <summary>
        /// Well-formed Npgsql connection string that fails <b>fast</b> at connect time.
        /// Port 1 on the loopback (<c>127.0.0.1:1</c>) has nothing listening, so a connection attempt
        /// is refused immediately; <c>Timeout=1</c> / <c>Command Timeout=1</c> cap any wait at ~1 second
        /// as belt-and-suspenders. This keeps the tests fully deterministic with no hang and no
        /// <c>Thread.Sleep</c>, while still guaranteeing that <c>MigrateAsync</c> genuinely throws.
        /// </summary>
        private const string UnreachableConnectionString =
            "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1;Command Timeout=1";

        /// <summary>
        /// Ensures the static-file directories that <c>Startup.Configure</c> requires exist under the
        /// current working directory before the host is started.
        /// </summary>
        /// <remarks>
        /// <c>Startup.Configure</c> constructs a <c>PhysicalFileProvider</c> over
        /// <c>"{cwd}/Content"</c>, which throws <c>DirectoryNotFoundException</c> if the directory is
        /// missing, and its <c>UseStaticFiles()</c> call plus the <c>MapFallbackToController</c> route
        /// resolve against the web root <c>"{cwd}/wwwroot"</c>. Creating both directories lets the host
        /// start. <c>Directory.CreateDirectory</c> is idempotent — it is a no-op when the directory
        /// already exists.
        /// </remarks>
        private static void EnsureContentAndWwwrootDirectoriesExist()
        {
            Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), "Content"));
            Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"));
        }

        /// <summary>
        /// Builds a real application host from <see cref="Program.CreateHostBuilder"/> whose database
        /// connection strings are unreachable and whose network server is the in-memory
        /// <see cref="TestServer"/> rather than Kestrel.
        /// </summary>
        /// <remarks>
        /// <list type="bullet">
        /// <item>
        /// Registering <c>AddSingleton&lt;IServer, TestServer&gt;()</c> <b>after</b>
        /// <c>ConfigureWebHostDefaults</c> makes <see cref="TestServer"/> the resolved
        /// <see cref="IServer"/> (last registration wins), so Kestrel never starts — no real port is
        /// bound and no HTTPS development certificate is required.
        /// </item>
        /// <item>
        /// All configuration is injected in memory because only <c>appsettings.Development.json</c>
        /// exists in the API project and it is not copied to the test output directory. The
        /// <c>UseNpgsql</c> option lambdas and the Redis <c>IConnectionMultiplexer</c> factory are
        /// deferred, so <c>Build()</c> succeeds with bad connection strings; the failure only surfaces
        /// when <c>MigrateAsync</c> actually opens a connection. Only <c>Token:Key</c> /
        /// <c>Token:Issuer</c> are read eagerly (by <c>AddIdentityServices</c> during <c>Build()</c>),
        /// so both are supplied — <c>Token:Key</c> must be non-null or <c>Build()</c> throws.
        /// </item>
        /// </list>
        /// </remarks>
        private static IHost BuildHostWithUnreachableDatabase()
        {
            return Program.CreateHostBuilder(Array.Empty<string>())
                // Replace Kestrel with the in-memory TestServer so StartAsync binds no real port and
                // needs no HTTPS dev-certificate. Registered AFTER ConfigureWebHostDefaults so it wins
                // as the resolved IServer.
                .ConfigureServices(services => services.AddSingleton<IServer, TestServer>())
                .ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string>
                    {
                        // Invalid/unreachable connection strings => MigrateAsync throws at connect time.
                        ["ConnectionStrings:DefaultConnection"] = UnreachableConnectionString,
                        ["ConnectionStrings:IdentityConnection"] = UnreachableConnectionString,
                        ["ConnectionStrings:Redis"] = "127.0.0.1:1",
                        // Required by Startup.AddIdentityServices at BUILD time (Token:Key must be non-null).
                        ["Token:Key"] = "super secret key which is long enough",
                        ["Token:Issuer"] = "https://localhost:5001",
                        ["ApiUrl"] = "https://localhost:5001/content/",
                        // Fallback in case Kestrel is ever used: bind an ephemeral HTTP-only port (no cert).
                        ["urls"] = "http://127.0.0.1:0"
                    });
                })
                .Build();
        }

        /// <summary>
        /// Behavioural replica of the scope + <c>try/catch</c> migrate/seed block in
        /// <c>API/Program.cs</c> <c>Main</c>. It migrates and seeds the store context, then the identity
        /// context, and swallows any exception (logging it) exactly as the production code does.
        /// </summary>
        /// <remarks>
        /// This replication is required — and does <b>not</b> modify production code — because
        /// <c>WebApplicationFactory</c> never runs <c>Main</c>'s block and <c>Main</c> itself blocks on
        /// <c>host.Run()</c>. The order and the <c>catch (Exception)</c> semantics mirror
        /// <c>Program.Main</c> precisely: Store <c>MigrateAsync</c> → <c>StoreContextSeed.SeedAsync</c>,
        /// then Identity <c>MigrateAsync</c> → <c>AppIdentityDbContextSeed.SeedUserAsync</c>, with a
        /// single catch that logs <c>"An error occured during migration"</c> and continues.
        /// </remarks>
        private static async Task RunProgramMainMigrateSeedBlockAsync(IHost host)
        {
            using var scope = host.Services.CreateScope();
            var services = scope.ServiceProvider;
            var loggerFactory = services.GetRequiredService<ILoggerFactory>();
            try
            {
                var context = services.GetRequiredService<StoreContext>();
                await context.Database.MigrateAsync();
                await StoreContextSeed.SeedAsync(context, loggerFactory);

                var userManager = services.GetRequiredService<UserManager<AppUser>>();
                var identityContext = services.GetRequiredService<AppIdentityDbContext>();
                await identityContext.Database.MigrateAsync();
                await AppIdentityDbContextSeed.SeedUserAsync(userManager);
            }
            catch (Exception ex)
            {
                var logger = loggerFactory.CreateLogger<Program>();
                logger.LogError(ex, "An error occured during migration");
            }
        }

        /// <summary>
        /// Primary assertion: when startup migration fails against an unreachable database, the failure
        /// is isolated by <c>Program.Main</c>'s <c>try/catch</c> (it does not propagate) and the host
        /// still starts successfully instead of crashing the process.
        /// </summary>
        [Fact]
        public async Task Main_WhenMigrationFailsWithUnreachableDatabase_MigrationErrorIsIsolatedAndHostStarts()
        {
            // Arrange
            EnsureContentAndWwwrootDirectoriesExist();
            using var host = BuildHostWithUnreachableDatabase();

            // Act — replicate Main's migrate/seed try/catch (it logs & CONTINUES; it must NOT rethrow).
            Func<Task> migrateAndSeed = () => RunProgramMainMigrateSeedBlockAsync(host);

            // Assert — the try/catch swallows the migration failure (isolation).
            await migrateAndSeed.Should().NotThrowAsync();

            // Assert — the host still starts despite the migration failure (does not crash).
            Func<Task> startThenStop = async () =>
            {
                await host.StartAsync();
                await host.StopAsync();
            };
            await startThenStop.Should().NotThrowAsync();
        }

        /// <summary>
        /// Guard assertion proving the failure is genuine: migrating against the unreachable database
        /// really does throw. This is precisely the exception that <c>Program.Main</c> isolates, so it
        /// makes the "does not throw" assertion in
        /// <see cref="Main_WhenMigrationFailsWithUnreachableDatabase_MigrationErrorIsIsolatedAndHostStarts"/>
        /// meaningful rather than vacuous.
        /// </summary>
        [Fact]
        public async Task MigrateAsync_WithUnreachableDatabase_Throws()
        {
            // Arrange
            EnsureContentAndWwwrootDirectoriesExist();
            using var host = BuildHostWithUnreachableDatabase();
            using var scope = host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            // Act
            Func<Task> migrate = () => context.Database.MigrateAsync();

            // Assert — the unreachable DB genuinely throws (this is precisely what Program.Main isolates).
            await migrate.Should().ThrowAsync<Exception>();
        }
    }
}
