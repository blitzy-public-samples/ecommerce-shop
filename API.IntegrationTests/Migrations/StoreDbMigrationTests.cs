using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using API.IntegrationTests.Infrastructure;
using FluentAssertions;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace API.IntegrationTests.Migrations
{
    /// <summary>
    /// Integration tests that verify the Store (catalog + ordering) EF Core migration applies cleanly to a
    /// REAL PostgreSQL database provisioned by Testcontainers, and that <c>StoreContextSeed.SeedAsync</c>
    /// populates the documented reference data (6 brands / 4 types / 18 products / 4 delivery methods).
    /// <para>
    /// This class consumes <see cref="ContainerFixture"/> as an <c>IClassFixture&lt;ContainerFixture&gt;</c>
    /// (per-class isolation, CR-01) — consistent with every sibling integration test class — so it owns its
    /// OWN isolated, disposable PostgreSQL + Redis pair. The fixture's <c>InitializeAsync</c> has already run
    /// <c>MigrateAsync</c> + seeding for both databases (Store then Identity) before the first test in this
    /// class executes, so these tests simply query the ready database through fresh service scopes.
    /// </para>
    /// Binding constraints (AAP 0.10.1): real infrastructure only (genuine Npgsql, no in-memory provider),
    /// no docker-compose reuse, no Thread.Sleep.
    /// </summary>
    public class StoreDbMigrationTests : IClassFixture<ContainerFixture>
    {
        private readonly ContainerFixture _fixture;

        public StoreDbMigrationTests(ContainerFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task StoreContext_AfterMigration_HasNoPendingMigrations()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            // Act
            var pending = await context.Database.GetPendingMigrationsAsync();

            // Assert
            pending.Should().BeEmpty("all Store migrations should have been applied by the fixture");
        }

        [Fact]
        public async Task StoreContext_AfterMigration_HasAppliedInitialMigration()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            // Act
            var applied = await context.Database.GetAppliedMigrationsAsync();

            // Assert
            applied.Should().Contain(m => m.EndsWith("PostGres initial"),
                "the initial Store schema migration must be recorded as applied");
        }

        [Fact]
        public async Task StoreDatabase_AfterMigration_ContainsAllExpectedTables()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            // Act
            var tables = await GetPublicTableNamesAsync(context);

            // Assert
            tables.Should().Contain(new[]
            {
                "Products", "ProductBrands", "ProductTypes",
                "Orders", "OrderItems", "DeliveryMethods"
            });
        }

        [Fact]
        public async Task StoreDbSets_AfterMigration_AreAllQueryable()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            // Act
            Func<Task> act = async () =>
            {
                await context.Products.CountAsync();
                await context.ProductBrands.CountAsync();
                await context.ProductTypes.CountAsync();
                await context.Orders.CountAsync();
                await context.OrderItems.CountAsync();
                await context.DeliveryMethods.CountAsync();
            };

            // Assert
            await act.Should().NotThrowAsync(
                "every mapped table must physically exist in the migrated PostgreSQL schema");
        }

        [Fact]
        public async Task StoreContextSeed_AfterSeeding_CreatesSixProductBrands()
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            var count = await context.ProductBrands.CountAsync();

            count.Should().Be(6);
        }

        [Fact]
        public async Task StoreContextSeed_AfterSeeding_CreatesFourProductTypes()
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            var count = await context.ProductTypes.CountAsync();

            count.Should().Be(4);
        }

        [Fact]
        public async Task StoreContextSeed_AfterSeeding_CreatesEighteenProducts()
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            var count = await context.Products.CountAsync();

            count.Should().Be(18);
        }

        [Fact]
        public async Task StoreContextSeed_AfterSeeding_CreatesFourDeliveryMethods()
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            var count = await context.DeliveryMethods.CountAsync();

            count.Should().Be(4);
        }

        [Fact]
        public async Task StoreContextSeed_AfterSeeding_SeedsExpectedProductBrandNames()
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            var names = await context.ProductBrands.Select(b => b.Name).ToListAsync();

            names.Should().BeEquivalentTo(new[]
            {
                "Angular", "NetCore", "VS Code", "React", "Typescript", "Redis"
            });
        }

        [Fact]
        public async Task StoreContextSeed_AfterSeeding_SeedsExpectedDeliveryMethodShortNames()
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            var shortNames = await context.DeliveryMethods.Select(d => d.ShortName).ToListAsync();

            shortNames.Should().BeEquivalentTo(new[] { "UPS1", "UPS2", "UPS3", "FREE" });
        }

        /// <summary>
        /// Reads the physically-created table names from the REAL PostgreSQL catalog (schema correctness).
        /// Uses the DbContext's own configured connection, so it depends only on DI (not on connection-string
        /// property names). information_schema also lists __EFMigrationsHistory, so callers assert Contain(...)
        /// rather than exact set equality.
        /// </summary>
        private static async Task<List<string>> GetPublicTableNamesAsync(StoreContext context)
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
