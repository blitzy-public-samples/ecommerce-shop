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

        // ---------------------------------------------------------------------------------------------
        // Flash-Sale feature (AAP §0.4.1 Group 5): additive coverage proving the
        // AddFlashSaleAndInventoryReservation migration (applied on startup AFTER
        // 20211212023144_PostGres initial) creates the two new tables and the Products.Version column,
        // WITHOUT disturbing any existing schema/seed expectation above. All assertions are Contain-/
        // EndsWith-based (additive); the new tables start empty, so these read-only checks create no rows.
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task StoreDatabase_AfterMigration_ContainsFlashSaleAndReservationTables()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            // Act
            var tables = await GetPublicTableNamesAsync(context);

            // Assert — the additive migration adds the two new feature tables ...
            tables.Should().Contain(new[] { "FlashSales", "InventoryReservations" });

            // ... while EVERY original table remains present (the guard reads as "old + new").
            tables.Should().Contain(new[]
            {
                "Products", "ProductBrands", "ProductTypes",
                "Orders", "OrderItems", "DeliveryMethods"
            });
        }

        [Fact]
        public async Task ProductsTable_AfterMigration_HasVersionConcurrencyColumn()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            // Act
            var columns = await GetPublicColumnTypesAsync(context, "Products");

            // Assert — the optimistic-concurrency token column exists on the existing Products table ...
            columns.Keys.Should().Contain("Version");

            // ... and carries a concrete SQL store type. NOTE (empirically confirmed against Testcontainers
            // PostgreSQL 13.x): the committed AddFlashSaleAndInventoryReservation migration maps
            // the .IsConcurrencyToken() column via AddColumn<long>(type: "bigint"), so information_schema
            // reports "bigint" (NOT "oid"). Per the task's tolerance clause, column PRESENCE (asserted above)
            // is the primary guarantee; the store type is asserted only as a tolerant, non-empty presence
            // check so this fact stays robust to whichever integer store type the migration chose.
            columns["Version"].Should().NotBeNullOrEmpty();
        }

        [Fact]
        public async Task StoreContext_AfterMigration_HasAppliedFlashSaleMigration()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            // Act
            var applied = await context.Database.GetAppliedMigrationsAsync();

            // Assert — mirror the existing EndsWith("PostGres initial") style; do NOT hardcode the full
            // timestamped id beyond the suffix.
            applied.Should().Contain(m => m.EndsWith("AddFlashSaleAndInventoryReservation"),
                "the additive Flash-Sale / Inventory-Reservation migration must be recorded as applied");
        }

        [Fact]
        public async Task StoreDbSets_AfterMigration_FlashSaleAndReservationSetsAreQueryable()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            // Act — mirrors the existing StoreDbSets_AfterMigration_AreAllQueryable style for the NEW sets.
            Func<Task> act = async () =>
            {
                await context.FlashSales.CountAsync();
                await context.InventoryReservations.CountAsync();
            };

            // Assert — both new mapped tables must physically exist (empty tables => count 0 is fine).
            await act.Should().NotThrowAsync(
                "the new FlashSales and InventoryReservations tables must physically exist in the migrated schema");
        }

        [Fact]
        public async Task FlashSaleAndReservationTables_AfterMigration_HaveExpectedIndexes()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            // Act — pg_indexes also lists the PK indexes, so collect and assert Contain(...) (additive).
            var indexes = await GetPublicIndexNamesAsync(context, "FlashSales");
            indexes.AddRange(await GetPublicIndexNamesAsync(context, "InventoryReservations"));

            // Assert — the feature indexes created by the migration are present. These are the SALE-SCOPED
            // composite indexes defined by the entity configurations and emitted by the additive migration,
            // each backing a hot query path:
            //   IX_FlashSales_ProductId, IX_FlashSales_StartAt_EndAt  -> active-sale lookup by product / window
            //   IX_InventoryReservations_FlashSaleId_Status           -> sale-scoped availability aggregation
            //   IX_InventoryReservations_Status_ExpiresAt             -> the expiry-sweep predicate (Status, then ExpiresAt)
            //   IX_InventoryReservations_ProductId_SessionId          -> checkout-consume / explicit-release lookup
            // Contain(...) stays additive: the migration also creates the two PK_* indexes, which this subset
            // check tolerates without tightening.
            indexes.Should().Contain(new[]
            {
                "IX_FlashSales_ProductId",
                "IX_FlashSales_StartAt_EndAt",
                "IX_InventoryReservations_FlashSaleId_Status",
                "IX_InventoryReservations_Status_ExpiresAt",
                "IX_InventoryReservations_ProductId_SessionId"
            });
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

        /// <summary>
        /// Reads the column names and their SQL data types for a given public table from the REAL
        /// PostgreSQL catalog (information_schema.columns), returned as column-name -> data_type so callers
        /// can assert both presence (Contain) and, tolerantly, the mapped store type. Mirrors
        /// <see cref="GetPublicTableNamesAsync"/>'s connection-ownership-safe pattern: it opens the
        /// DbContext's own borrowed connection only if it was closed, and closes it again only if it opened
        /// it (never disposing a connection it did not own).
        /// </summary>
        private static async Task<Dictionary<string, string>> GetPublicColumnTypesAsync(
            StoreContext context, string tableName)
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
                    "SELECT column_name, data_type FROM information_schema.columns " +
                    "WHERE table_schema = 'public' AND table_name = @tableName;";

                var parameter = command.CreateParameter();
                parameter.ParameterName = "tableName";
                parameter.Value = tableName;
                command.Parameters.Add(parameter);

                var columns = new Dictionary<string, string>();
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    columns[reader.GetString(0)] = reader.GetString(1);
                }

                return columns;
            }
            finally
            {
                if (wasClosed)
                {
                    await connection.CloseAsync();
                }
            }
        }

        /// <summary>
        /// Reads the index names for a given public table from the REAL PostgreSQL catalog (pg_indexes).
        /// Mirrors <see cref="GetPublicTableNamesAsync"/>'s connection-ownership-safe pattern. pg_indexes
        /// also lists the primary-key index, so callers assert Contain(...) rather than exact equality.
        /// </summary>
        private static async Task<List<string>> GetPublicIndexNamesAsync(
            StoreContext context, string tableName)
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
                    "SELECT indexname FROM pg_indexes " +
                    "WHERE schemaname = 'public' AND tablename = @tableName;";

                var parameter = command.CreateParameter();
                parameter.ParameterName = "tableName";
                parameter.Value = tableName;
                command.Parameters.Add(parameter);

                var indexes = new List<string>();
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    indexes.Add(reader.GetString(0));
                }

                return indexes;
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
