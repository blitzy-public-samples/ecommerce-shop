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

        /// <summary>
        /// Reads a single column's data type and nullability from the REAL PostgreSQL catalog, or null if the
        /// column does not exist. Reuses the DbContext connection (open-if-closed) exactly like
        /// <see cref="GetPublicTableNamesAsync"/>, using a parameterized command for the table/column names.
        /// </summary>
        private static async Task<(string DataType, string IsNullable)?> GetColumnAsync(
            StoreContext context, string table, string column)
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
                    "SELECT column_name, data_type, is_nullable FROM information_schema.columns " +
                    "WHERE table_schema = 'public' AND table_name = @table AND column_name = @column;";

                var tableParam = command.CreateParameter();
                tableParam.ParameterName = "@table";
                tableParam.Value = table;
                command.Parameters.Add(tableParam);

                var columnParam = command.CreateParameter();
                columnParam.ParameterName = "@column";
                columnParam.Value = column;
                command.Parameters.Add(columnParam);

                using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    return null;
                }

                return (reader.GetString(1), reader.GetString(2));
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
        /// Reads all column names of a public table from the REAL PostgreSQL catalog (ordered), using the same
        /// connection-ownership-safe pattern as <see cref="GetPublicTableNamesAsync"/> with a parameterized table name.
        /// </summary>
        private static async Task<List<string>> GetColumnNamesAsync(StoreContext context, string table)
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
                    "SELECT column_name FROM information_schema.columns " +
                    "WHERE table_schema = 'public' AND table_name = @table " +
                    "ORDER BY column_name;";

                var tableParam = command.CreateParameter();
                tableParam.ParameterName = "@table";
                tableParam.Value = table;
                command.Parameters.Add(tableParam);

                var columns = new List<string>();
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    columns.Add(reader.GetString(0));
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
        /// Reads the ON DELETE rule of every FOREIGN KEY constraint on the Reservations/FlashSales tables from the
        /// REAL PostgreSQL catalog, as a map of constraint name → delete_rule. Same connection-ownership pattern as
        /// <see cref="GetPublicTableNamesAsync"/>. NOTE: PostgreSQL/Npgsql may report a non-cascading FK as either
        /// 'NO ACTION' or 'RESTRICT' in referential_constraints.delete_rule; callers must accept both.
        /// </summary>
        private static async Task<Dictionary<string, string>> GetForeignKeyDeleteRulesAsync(StoreContext context)
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
                    "SELECT tc.constraint_name, rc.delete_rule " +
                    "FROM information_schema.table_constraints tc " +
                    "JOIN information_schema.referential_constraints rc " +
                    "  ON tc.constraint_name = rc.constraint_name " +
                    " AND tc.constraint_schema = rc.constraint_schema " +
                    "WHERE tc.constraint_type = 'FOREIGN KEY' " +
                    "  AND tc.table_schema = 'public' " +
                    "  AND tc.table_name IN ('Reservations', 'FlashSales');";

                var rules = new Dictionary<string, string>();
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rules[reader.GetString(0)] = reader.GetString(1);
                }

                return rules;
            }
            finally
            {
                if (wasClosed)
                {
                    await connection.CloseAsync();
                }
            }
        }

        [Fact]
        public async Task HasAppliedAddInventoryAndFlashSaleMigration_AfterStartup_MigrationIsApplied()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            // Act
            var applied = await context.Database.GetAppliedMigrationsAsync();
            var pending = await context.Database.GetPendingMigrationsAsync();

            // Assert
            applied.Should().Contain(m => m.EndsWith("AddInventoryAndFlashSale"),
                "the inventory & flash-sale migration must be recorded as applied by the fixture");
            pending.Should().BeEmpty("no Store migrations should remain pending after the inventory migration");
        }

        [Fact]
        public async Task ContainsInventoryAndFlashSaleTables_AfterMigration_TablesExist()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            // Act
            var tables = await GetPublicTableNamesAsync(context);

            // Assert
            tables.Should().Contain("Reservations");
            tables.Should().Contain("FlashSales");
            tables.Should().Contain(new[]
            {
                "Products", "ProductBrands", "ProductTypes",
                "Orders", "OrderItems", "DeliveryMethods"
            }, "the inventory migration is additive and must not drop the existing catalog/order tables");
        }

        [Fact]
        public async Task Products_AfterMigration_HasStockQuantityColumn()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            // Act
            var column = await GetColumnAsync(context, "Products", "StockQuantity");

            // Assert
            column.Should().NotBeNull("the additive StockQuantity column must exist on Products after the migration");
            column.Value.DataType.Should().Be("integer");
            column.Value.IsNullable.Should().Be("NO");
        }

        [Fact]
        public async Task ReservationsAndFlashSales_AfterMigration_HaveExpectedColumns()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            // Act
            var flashSaleColumns = await GetColumnNamesAsync(context, "FlashSales");
            var reservationColumns = await GetColumnNamesAsync(context, "Reservations");

            // Assert
            flashSaleColumns.Should().Contain(new[]
            {
                "Id", "ProductId", "SaleStockQuantity", "StartsAt", "EndsAt", "Status"
            });
            reservationColumns.Should().Contain(new[]
            {
                "Id", "ProductId", "BasketId", "Quantity", "CreatedAt", "ExpiresAt", "Status", "FlashSaleId"
            });
        }

        [Fact]
        public async Task StoreDbSets_IncludingInventory_AreAllQueryable()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            // Act
            Func<Task> act = async () =>
            {
                await context.Reservations.CountAsync();
                await context.FlashSales.CountAsync();
            };

            // Assert
            await act.Should().NotThrowAsync(
                "the new inventory DbSets must map to real tables in the migrated PostgreSQL schema");
            (await context.Reservations.CountAsync()).Should().Be(0, "a freshly migrated database has no reservations");
            (await context.FlashSales.CountAsync()).Should().Be(0, "a freshly migrated database has no flash sales");
        }

        [Fact]
        public async Task InventoryForeignKeys_AfterMigration_UseRestrictDelete()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();
            var acceptable = new[] { "NO ACTION", "RESTRICT" };
            var foreignKeys = new[]
            {
                "FK_Reservations_Products_ProductId",
                "FK_FlashSales_Products_ProductId",
                "FK_Reservations_FlashSales_FlashSaleId"
            };

            // Act
            var deleteRules = await GetForeignKeyDeleteRulesAsync(context);

            // Assert
            foreach (var fk in foreignKeys)
            {
                deleteRules.Should().ContainKey(fk, "the inventory foreign key {0} must exist", fk);
                deleteRules[fk].Should().BeOneOf(acceptable,
                    "DeleteBehavior.Restrict must map to a non-cascading delete rule for {0} " +
                    "(PostgreSQL/Npgsql reports it as NO ACTION or RESTRICT)", fk);
                deleteRules[fk].Should().NotBe("CASCADE",
                    "inventory foreign keys must never cascade-delete their principals");
            }
        }
    }
}
