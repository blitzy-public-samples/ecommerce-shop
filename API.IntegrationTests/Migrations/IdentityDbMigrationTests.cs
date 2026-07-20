using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using API.IntegrationTests.Infrastructure;
using Core.Entities.Identity;
using FluentAssertions;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace API.IntegrationTests.Migrations
{
    /// <summary>
    /// Integration tests verifying the Identity EF Core migration applies cleanly to a REAL PostgreSQL
    /// database (Testcontainers) and that <c>AppIdentityDbContextSeed.SeedUserAsync</c> seeds the
    /// documented user (bob@test.com / "Bob" / Pa$$w0rd) together with the pre-seeded <c>Address</c>.
    /// The <see cref="ContainerFixture"/> has already migrated + seeded the Identity DB in its
    /// <c>InitializeAsync</c>, so the seeded user exists by the time these tests run. Binding constraints
    /// (AAP 0.10.1): real infrastructure only (genuine Npgsql, no in-memory provider), no docker-compose
    /// reuse, no Thread.Sleep.
    ///
    /// <para>
    /// This class consumes the fixture as a per-class <c>IClassFixture&lt;ContainerFixture&gt;</c>, matching
    /// the post-CR-01 convention shared by every sibling integration-test class (see
    /// <c>Infrastructure/AssemblyInfo.cs</c>): each class owns its OWN isolated, disposable PostgreSQL +
    /// Redis pair, and assembly-wide parallelization is disabled so the per-class container pairs start
    /// sequentially. xUnit injects the fixture through the constructor.
    /// </para>
    /// </summary>
    public class IdentityDbMigrationTests : IClassFixture<ContainerFixture>
    {
        private const string SeededEmail = "bob@test.com";
        private const string SeededPassword = "Pa$$w0rd";

        private readonly ContainerFixture _fixture;

        public IdentityDbMigrationTests(ContainerFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task AppIdentityDbContext_AfterMigration_HasNoPendingMigrations()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();

            // Act
            var pending = await context.Database.GetPendingMigrationsAsync();

            // Assert
            pending.Should().BeEmpty("all Identity migrations should have been applied by the fixture");
        }

        [Fact]
        public async Task AppIdentityDbContext_AfterMigration_HasAppliedInitialMigration()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();

            // Act
            var applied = await context.Database.GetAppliedMigrationsAsync();

            // Assert
            applied.Should().Contain(m => m.EndsWith("PostGres identity initial"),
                "the initial Identity schema migration must be recorded as applied");
        }

        [Fact]
        public async Task IdentityDatabase_AfterMigration_ContainsAllExpectedTables()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();

            // Act
            var tables = await GetPublicTableNamesAsync(context);

            // Assert
            tables.Should().Contain(new[]
            {
                "AspNetUsers", "AspNetRoles", "AspNetRoleClaims", "AspNetUserClaims",
                "AspNetUserLogins", "AspNetUserRoles", "AspNetUserTokens", "Address"
            });
        }

        [Fact]
        public async Task AppIdentityDbContextSeed_AfterSeeding_CreatesSeededUser()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

            // Act
            var user = await userManager.FindByEmailAsync(SeededEmail);

            // Assert
            user.Should().NotBeNull();
            user.UserName.Should().Be(SeededEmail);
            user.DisplayName.Should().Be("Bob");
        }

        [Fact]
        public async Task AppIdentityDbContextSeed_AfterSeeding_SeededUserHasExpectedAddress()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();

            // Act
            var user = await context.Users
                .Include(u => u.Address)
                .SingleOrDefaultAsync(u => u.Email == SeededEmail);

            // Assert
            user.Should().NotBeNull();
            user.Address.Should().NotBeNull();
            user.Address.FirstName.Should().Be("Bob");
            user.Address.LastName.Should().Be("Bobbity");
            user.Address.Street.Should().Be("10 The Street");
            user.Address.City.Should().Be("New York");
            user.Address.State.Should().Be("NY");
            user.Address.ZipCode.Should().Be("90210");
        }

        [Fact]
        public async Task SeededUser_WithSeedPassword_PassesPasswordCheck()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = await userManager.FindByEmailAsync(SeededEmail);
            user.Should().NotBeNull("the seeded user must exist before checking its password");

            // Act
            var isValid = await userManager.CheckPasswordAsync(user, SeededPassword);

            // Assert
            isValid.Should().BeTrue();
        }

        [Fact]
        public async Task SeededUser_WithIncorrectPassword_FailsPasswordCheck()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = await userManager.FindByEmailAsync(SeededEmail);
            user.Should().NotBeNull("the seeded user must exist before checking its password");

            // Act
            var isValid = await userManager.CheckPasswordAsync(user, "not-the-seed-password");

            // Assert
            isValid.Should().BeFalse();
        }

        /// <summary>
        /// Reads the physically-created table names from the REAL PostgreSQL catalog (schema correctness).
        /// Uses the DbContext's own configured connection, so it depends only on DI (not on connection-string
        /// property names). information_schema also lists __EFMigrationsHistory, so callers assert Contain(...)
        /// rather than exact set equality.
        /// </summary>
        private static async Task<List<string>> GetPublicTableNamesAsync(AppIdentityDbContext context)
        {
            var connection = context.Database.GetDbConnection();
            var wasClosed = connection.State == ConnectionState.Closed;
            if (wasClosed)
            {
                await connection.OpenAsync();
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT table_name FROM information_schema.tables " +
                    "WHERE table_schema = 'public' AND table_type = 'BASE TABLE';";

                var tables = new List<string>();
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    tables.Add(reader.GetString(0));
                }

                return tables;
            }
            finally
            {
                if (wasClosed)
                {
                    await connection.CloseAsync();
                }
            }
        }
    }
}
