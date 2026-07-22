using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using API.IntegrationTests.Infrastructure;
// Flash-Sale feature (review finding M9): entity namespaces are required by the upgrade/rollback
// migration tests below, which seed representative data (Product/ProductBrand/ProductType/DeliveryMethod)
// against a PRIOR-schema database and then create FlashSale/InventoryReservation rows after migrating
// forward to prove the new tables and their foreign keys are genuinely usable end-to-end.
using Core.Entities;
using Core.Entities.OrderAggregate;
using FluentAssertions;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
// Flash-Sale feature (review finding M9): IMigrator + GetService power the deterministic, provider-real
// upgrade (migrate to an earlier target, populate, migrate forward) and rollback (Down) coverage.
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
// Flash-Sale feature (review finding M9): a direct Npgsql connection provisions and drops an ISOLATED,
// disposable database on the SAME Testcontainers server so the upgrade/rollback tests never mutate the
// shared, already-migrated-and-seeded e-commerce database the other tests in this class rely on.
using Npgsql;
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
            var columns = await GetColumnMetadataAsync(context, "Products");

            // Assert — the optimistic-concurrency token column exists on the existing Products table ...
            columns.Keys.Should().Contain("Version");

            // ... and carries the EXACT provider-native store type. The Product/FlashSale optimistic-
            // concurrency token is CLR `uint`, which the Npgsql provider maps to the PostgreSQL `oid` type
            // (AddColumn<uint>(type: "oid") in 20260721124953_AddProductVersionConcurrencyToken, and
            // ProductConfiguration.IsConcurrencyToken().HasColumnType("oid")). Empirically confirmed against
            // the pinned Testcontainers PostgreSQL 13.x image: information_schema.columns.data_type reports
            // exactly "oid" (NOT "bigint"). Review finding M9: assert the EXACT catalog type and NOT-NULL
            // nullability rather than a tolerant non-empty presence check.
            columns["Version"].DataType.Should().Be("oid");
            columns["Version"].IsNullable.Should().BeFalse(
                "the concurrency-token column is created NOT NULL (with default 0)");
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

        // ---------------------------------------------------------------------------------------------
        // Review finding M9: EXACT PostgreSQL catalog proof. The tests above prove the feature tables and
        // key indexes EXIST (Contain-based, additive). The tests below tighten that into exact catalog
        // assertions — every column's precise store type / nullability / length / precision, the EXACT
        // index set with column ORDER, and the exact foreign-key (incl. ON DELETE behaviour), CHECK, and
        // primary-key definitions — plus a populated-prior-schema upgrade and a Down rollback. All expected
        // values were empirically confirmed against the pinned Testcontainers PostgreSQL 13.x image.
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task FlashSalesTable_AfterMigration_HasExactColumnCatalog()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            // Act
            var columns = await GetColumnMetadataAsync(context, "FlashSales");

            // Assert — EXACTLY these seven columns, no more and no fewer (exact catalog, not Contain).
            columns.Keys.Should().BeEquivalentTo(new[]
            {
                "Id", "ProductId", "StartAt", "EndAt", "SalePrice", "StockAllocation", "Version"
            });

            // ... and each column's exact PostgreSQL store type + nullability.
            columns["Id"].DataType.Should().Be("integer");
            columns["Id"].IsNullable.Should().BeFalse();
            columns["ProductId"].DataType.Should().Be("integer");
            columns["ProductId"].IsNullable.Should().BeFalse();
            columns["StartAt"].DataType.Should().Be("timestamp with time zone");
            columns["StartAt"].IsNullable.Should().BeFalse();
            columns["EndAt"].DataType.Should().Be("timestamp with time zone");
            columns["EndAt"].IsNullable.Should().BeFalse();
            // SalePrice mirrors Product.Price money precision: decimal(18,2) -> numeric(18,2).
            columns["SalePrice"].DataType.Should().Be("numeric");
            columns["SalePrice"].IsNullable.Should().BeFalse();
            columns["SalePrice"].NumericPrecision.Should().Be(18);
            columns["SalePrice"].NumericScale.Should().Be(2);
            columns["StockAllocation"].DataType.Should().Be("integer");
            columns["StockAllocation"].IsNullable.Should().BeFalse();
            // Optimistic-concurrency token: CLR uint -> PostgreSQL oid (NOT bigint).
            columns["Version"].DataType.Should().Be("oid");
            columns["Version"].IsNullable.Should().BeFalse();
        }

        [Fact]
        public async Task InventoryReservationsTable_AfterMigration_HasExactColumnCatalog()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            // Act
            var columns = await GetColumnMetadataAsync(context, "InventoryReservations");

            // Assert — EXACTLY these seven columns, no more and no fewer.
            columns.Keys.Should().BeEquivalentTo(new[]
            {
                "Id", "FlashSaleId", "ProductId", "Quantity", "SessionId", "ExpiresAt", "Status"
            });

            columns["Id"].DataType.Should().Be("integer");
            columns["Id"].IsNullable.Should().BeFalse();
            columns["FlashSaleId"].DataType.Should().Be("integer");
            columns["FlashSaleId"].IsNullable.Should().BeFalse();
            columns["ProductId"].DataType.Should().Be("integer");
            columns["ProductId"].IsNullable.Should().BeFalse();
            columns["Quantity"].DataType.Should().Be("integer");
            columns["Quantity"].IsNullable.Should().BeFalse();
            // SessionId is the client basket UUID reused as the session key, bounded to the canonical
            // 36-character UUID v4 length: character varying(36).
            columns["SessionId"].DataType.Should().Be("character varying");
            columns["SessionId"].IsNullable.Should().BeFalse();
            columns["SessionId"].MaxLength.Should().Be(36);
            columns["ExpiresAt"].DataType.Should().Be("timestamp with time zone");
            columns["ExpiresAt"].IsNullable.Should().BeFalse();
            // ReservationStatus enum persisted as int.
            columns["Status"].DataType.Should().Be("integer");
            columns["Status"].IsNullable.Should().BeFalse();
        }

        [Fact]
        public async Task FlashSalesTable_AfterMigration_HasExactIndexesForeignKeyAndPrimaryKey()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            // Act + Assert — EXACT index set: the PK index plus EXACTLY the two feature indexes.
            var indexes = await GetIndexDefinitionsAsync(context, "FlashSales");
            indexes.Keys.Should().BeEquivalentTo(new[]
            {
                "PK_FlashSales", "IX_FlashSales_ProductId", "IX_FlashSales_StartAt_EndAt"
            });
            // ... with the exact indexed columns AND their order.
            indexes["IX_FlashSales_ProductId"].Should().Contain("(\"ProductId\")");
            indexes["IX_FlashSales_StartAt_EndAt"].Should().Contain("(\"StartAt\", \"EndAt\")");

            // The single FK references Products(Id) with the intended ON DELETE RESTRICT behaviour.
            var foreignKeys = await GetConstraintDefinitionsAsync(context, "FlashSales", 'f');
            foreignKeys.Should().HaveCount(1);
            foreignKeys["FK_FlashSales_Products_ProductId"].Should().Be(
                "FOREIGN KEY (\"ProductId\") REFERENCES \"Products\"(\"Id\") ON DELETE RESTRICT");

            // Review finding M2: the three database-integrity CHECK constraints exist with their exact,
            // PostgreSQL-normalized predicate text — a positive sale price, a positive stock allocation, and a
            // correctly-ordered non-empty [StartAt, EndAt] window. (numeric literal 0 is deparsed as
            // "(0)::numeric" for the decimal SalePrice column.)
            var checks = await GetConstraintDefinitionsAsync(context, "FlashSales", 'c');
            checks.Should().ContainKey("CK_FlashSales_SalePrice_Positive");
            checks["CK_FlashSales_SalePrice_Positive"].Should().Be("CHECK ((\"SalePrice\" > (0)::numeric))");
            checks.Should().ContainKey("CK_FlashSales_StockAllocation_Positive");
            checks["CK_FlashSales_StockAllocation_Positive"].Should().Be("CHECK ((\"StockAllocation\" > 0))");
            checks.Should().ContainKey("CK_FlashSales_EndAt_After_StartAt");
            checks["CK_FlashSales_EndAt_After_StartAt"].Should().Be("CHECK ((\"EndAt\" > \"StartAt\"))");

            // Exact primary key.
            var primaryKeys = await GetConstraintDefinitionsAsync(context, "FlashSales", 'p');
            primaryKeys.Should().ContainKey("PK_FlashSales");
            primaryKeys["PK_FlashSales"].Should().Be("PRIMARY KEY (\"Id\")");
        }

        [Fact]
        public async Task InventoryReservationsTable_AfterMigration_HasExactIndexesForeignKeysCheckAndPrimaryKey()
        {
            // Arrange
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            // Act + Assert — EXACT index set: the PK index plus EXACTLY the three feature indexes.
            var indexes = await GetIndexDefinitionsAsync(context, "InventoryReservations");
            indexes.Keys.Should().BeEquivalentTo(new[]
            {
                "PK_InventoryReservations",
                "IX_InventoryReservations_FlashSaleId_Status",
                "IX_InventoryReservations_ProductId_SessionId",
                "IX_InventoryReservations_Status_ExpiresAt"
            });
            // ... with the exact indexed columns AND their order (composite lead columns matter for the
            // availability, checkout-consume, and expiry-sweep query paths these indexes back).
            indexes["IX_InventoryReservations_FlashSaleId_Status"].Should().Contain("(\"FlashSaleId\", \"Status\")");
            indexes["IX_InventoryReservations_ProductId_SessionId"].Should().Contain("(\"ProductId\", \"SessionId\")");
            indexes["IX_InventoryReservations_Status_ExpiresAt"].Should().Contain("(\"Status\", \"ExpiresAt\")");

            // Both FKs reference their parent with ON DELETE RESTRICT (preserving durable sold-stock history).
            var foreignKeys = await GetConstraintDefinitionsAsync(context, "InventoryReservations", 'f');
            foreignKeys.Should().HaveCount(2);
            foreignKeys["FK_InventoryReservations_FlashSales_FlashSaleId"].Should().Be(
                "FOREIGN KEY (\"FlashSaleId\") REFERENCES \"FlashSales\"(\"Id\") ON DELETE RESTRICT");
            foreignKeys["FK_InventoryReservations_Products_ProductId"].Should().Be(
                "FOREIGN KEY (\"ProductId\") REFERENCES \"Products\"(\"Id\") ON DELETE RESTRICT");

            // The positive-quantity CHECK constraint (deepest zero-oversell backstop) exists with its exact text.
            var checks = await GetConstraintDefinitionsAsync(context, "InventoryReservations", 'c');
            checks.Should().ContainKey("CK_InventoryReservations_Quantity_Positive");
            checks["CK_InventoryReservations_Quantity_Positive"].Should().Be("CHECK ((\"Quantity\" > 0))");

            // Review finding M2: the Status-domain CHECK constraint keeps Status within the ReservationStatus
            // enum range (Active=0 .. Expired=3), with its exact PostgreSQL-normalized text.
            checks.Should().ContainKey("CK_InventoryReservations_Status_Valid");
            checks["CK_InventoryReservations_Status_Valid"].Should().Be(
                "CHECK (((\"Status\" >= 0) AND (\"Status\" <= 3)))");

            // Exact primary key.
            var primaryKeys = await GetConstraintDefinitionsAsync(context, "InventoryReservations", 'p');
            primaryKeys.Should().ContainKey("PK_InventoryReservations");
            primaryKeys["PK_InventoryReservations"].Should().Be("PRIMARY KEY (\"Id\")");
        }

        [Fact]
        public async Task StoreDatabase_UpgradingFromPopulatedPriorSchema_PreservesDataAndAddsFeatureTables()
        {
            // Review finding M9: prove the additive Flash-Sale migration applies onto a database that already
            // holds representative catalog data (the realistic production upgrade), preserving every existing
            // row and making the two new tables genuinely usable. Runs on an ISOLATED, disposable database on
            // the SAME Testcontainers server so it never mutates the shared, seeded e-commerce database.
            var databaseName = "blitzy_adhoc_upgrade_" + Guid.NewGuid().ToString("N");
            var connectionString =
                await CreateIsolatedDatabaseAsync(_fixture.StoreConnectionString, databaseName);

            try
            {
                int seededProductId;

                // 1) Migrate ONLY as far as the pre-Flash-Sale migration, then populate representative data.
                await using (var context = CreateStoreContext(connectionString))
                {
                    var priorMigrationId = ResolveMigrationId(context, "AddProductVersionConcurrencyToken");
                    await MigrateToAsync(context, priorMigrationId);

                    // The feature tables must NOT exist yet at the prior schema ...
                    var priorTables = await GetPublicTableNamesAsync(context);
                    priorTables.Should().NotContain("FlashSales");
                    priorTables.Should().NotContain("InventoryReservations");
                    // ... but Products.Version already exists (it was added by this prior migration).
                    (await GetColumnMetadataAsync(context, "Products")).Should().ContainKey("Version");

                    seededProductId = await SeedRepresentativePriorDataAsync(context);
                }

                // 2) Apply the additive Flash-Sale migration on top of the POPULATED prior database.
                await using (var context = CreateStoreContext(connectionString))
                {
                    await context.Database.MigrateAsync();

                    // The additive migration is now recorded as applied ...
                    (await context.Database.GetAppliedMigrationsAsync())
                        .Should().Contain(id => id.EndsWith("AddFlashSaleAndInventoryReservation"));

                    // ... every pre-existing row is preserved and still queryable ...
                    (await context.ProductBrands.CountAsync()).Should().Be(1);
                    (await context.ProductTypes.CountAsync()).Should().Be(1);
                    (await context.DeliveryMethods.CountAsync()).Should().Be(1);
                    var preservedProduct = await context.Products.SingleAsync();
                    preservedProduct.Id.Should().Be(seededProductId);
                    preservedProduct.Name.Should().Be("M9 Upgrade Product");
                    preservedProduct.Price.Should().Be(123.45m);

                    // ... and the new tables are genuinely usable end-to-end, including their FKs to the
                    // preserved product (a real FlashSale + a real InventoryReservation against it).
                    var sale = new FlashSale
                    {
                        ProductId = seededProductId,
                        StartAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                        EndAt = DateTimeOffset.UtcNow.AddHours(1),
                        SalePrice = 99.99m,
                        StockAllocation = 100
                    };
                    context.FlashSales.Add(sale);
                    await context.SaveChangesAsync();

                    context.InventoryReservations.Add(new InventoryReservation
                    {
                        FlashSaleId = sale.Id,
                        ProductId = seededProductId,
                        Quantity = 3,
                        SessionId = Guid.NewGuid().ToString(),
                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                        Status = ReservationStatus.Active
                    });
                    await context.SaveChangesAsync();

                    (await context.FlashSales.CountAsync()).Should().Be(1);
                    (await context.InventoryReservations.CountAsync()).Should().Be(1);
                }
            }
            finally
            {
                // Always drop the throwaway database so nothing leaks on the shared server.
                await DropIsolatedDatabaseAsync(_fixture.StoreConnectionString, databaseName);
            }
        }

        [Fact]
        public async Task StoreDatabase_AddingVersionTokenToPopulated202112Schema_BackfillsExistingRowsAndAddsFeatureTables()
        {
            // Review finding M4: the realistic production upgrade path starts at the ORIGINAL, already-populated
            // 20211212023144 ("PostGres initial") schema — which has NO Products.Version column — and then
            // migrates forward through BOTH feature migrations: first AddProductVersionConcurrencyToken (which
            // must add the NON-NULL `oid` token to the already-populated Products table, backfilling every
            // pre-existing row with the default 0u), then AddFlashSaleAndInventoryReservation (the two new
            // tables). The sibling PreservesDataAndAddsFeatureTables test seeds AFTER Version already exists, so
            // it cannot prove that adding the non-null token to the original populated schema succeeds and
            // backfills existing rows; this test closes exactly that gap.
            //
            // Seeding is intentionally done via RAW SQL, not the EF model. The current StoreContext model
            // already includes Product.Version, so any EF insert (context.Products.Add + SaveChanges) at the
            // pre-Version schema would emit SQL referencing a "Version" column that does not yet exist and fail.
            // Raw INSERTs that name only the original 202112 columns are the faithful way to reproduce rows that
            // pre-date the token. Runs on an ISOLATED, disposable database on the SAME Testcontainers server so
            // it never mutates the shared, seeded e-commerce database the other tests rely on.
            var databaseName = "blitzy_adhoc_initupgrade_" + Guid.NewGuid().ToString("N");
            var connectionString =
                await CreateIsolatedDatabaseAsync(_fixture.StoreConnectionString, databaseName);

            try
            {
                // 1) Migrate ONLY to the original 202112 initial schema (before Version and the feature tables).
                await using (var context = CreateStoreContext(connectionString))
                {
                    var initialMigrationId = ResolveMigrationId(context, "PostGres initial");
                    await MigrateToAsync(context, initialMigrationId);

                    // The concurrency token does NOT exist yet ...
                    (await GetColumnMetadataAsync(context, "Products"))
                        .Should().NotContainKey("Version",
                            "the original 202112 schema predates the optimistic-concurrency token");
                    // ... and neither do the feature tables.
                    var initialTables = await GetPublicTableNamesAsync(context);
                    initialTables.Should().NotContain("FlashSales");
                    initialTables.Should().NotContain("InventoryReservations");
                }

                // 2) Populate representative catalog rows via RAW SQL at the pre-Version schema. Two products
                //    (referencing one brand + one type) are inserted so the backfill is proven across multiple
                //    pre-existing rows. Column lists name only original 202112 columns; identity Ids are
                //    generated by the database and the products resolve their FKs via scalar subqueries.
                await using (var context = CreateStoreContext(connectionString))
                {
                    await context.Database.ExecuteSqlRawAsync(
                        "INSERT INTO \"ProductBrands\" (\"Name\") VALUES ('M4InitBrand');");
                    await context.Database.ExecuteSqlRawAsync(
                        "INSERT INTO \"ProductTypes\" (\"Name\") VALUES ('M4InitType');");
                    await context.Database.ExecuteSqlRawAsync(
                        "INSERT INTO \"Products\" " +
                        "(\"Name\",\"Description\",\"Price\",\"PictureUrl\",\"ProductTypeId\",\"ProductBrandId\") " +
                        "VALUES ('M4 Init Product A','Seeded on the pre-Version 202112 schema',111.11," +
                        "'images/products/m4a.png'," +
                        "(SELECT \"Id\" FROM \"ProductTypes\" WHERE \"Name\"='M4InitType')," +
                        "(SELECT \"Id\" FROM \"ProductBrands\" WHERE \"Name\"='M4InitBrand'));");
                    await context.Database.ExecuteSqlRawAsync(
                        "INSERT INTO \"Products\" " +
                        "(\"Name\",\"Description\",\"Price\",\"PictureUrl\",\"ProductTypeId\",\"ProductBrandId\") " +
                        "VALUES ('M4 Init Product B','Seeded on the pre-Version 202112 schema',222.22," +
                        "'images/products/m4b.png'," +
                        "(SELECT \"Id\" FROM \"ProductTypes\" WHERE \"Name\"='M4InitType')," +
                        "(SELECT \"Id\" FROM \"ProductBrands\" WHERE \"Name\"='M4InitBrand'));");
                }

                // 3) Migrate forward through BOTH feature migrations (MigrateAsync applies all pending: the
                //    Version-token migration, THEN the feature-tables migration) onto the POPULATED schema.
                await using (var context = CreateStoreContext(connectionString))
                {
                    await context.Database.MigrateAsync();

                    // Both feature migrations are now recorded as applied.
                    var applied = await context.Database.GetAppliedMigrationsAsync();
                    applied.Should().Contain(id => id.EndsWith("AddProductVersionConcurrencyToken"),
                        "the non-null concurrency token migration must run against the populated schema");
                    applied.Should().Contain(id => id.EndsWith("AddFlashSaleAndInventoryReservation"),
                        "the additive feature-tables migration must run after the token migration");

                    // The non-null Version token now exists on Products (the core M4 proof: adding a NON-NULL
                    // column to an already-populated table succeeded).
                    var upgradedColumns = await GetColumnMetadataAsync(context, "Products");
                    upgradedColumns.Should().ContainKey("Version");
                    upgradedColumns["Version"].IsNullable.Should().BeFalse(
                        "the concurrency token is added as NOT NULL");

                    // Every PRE-EXISTING row survived AND was backfilled with the migration's default (0u); no
                    // data was lost when the non-null token was introduced.
                    var products = await context.Products.AsNoTracking().OrderBy(p => p.Price).ToListAsync();
                    products.Should().HaveCount(2, "both pre-Version rows survive the token addition");
                    products.Select(p => p.Name).Should().Equal("M4 Init Product A", "M4 Init Product B");
                    products.Should().OnlyContain(p => p.Version == 0u,
                        "adding the non-null token to a populated table backfills existing rows with the 0u default");

                    // The two feature tables are genuinely usable end-to-end, including their FKs to a
                    // backfilled, pre-existing product (a real FlashSale + a real InventoryReservation).
                    var target = products.First();
                    var sale = new FlashSale
                    {
                        ProductId = target.Id,
                        StartAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                        EndAt = DateTimeOffset.UtcNow.AddHours(1),
                        SalePrice = 55.55m,
                        StockAllocation = 40
                    };
                    context.FlashSales.Add(sale);
                    await context.SaveChangesAsync();

                    context.InventoryReservations.Add(new InventoryReservation
                    {
                        FlashSaleId = sale.Id,
                        ProductId = target.Id,
                        Quantity = 4,
                        SessionId = Guid.NewGuid().ToString(),
                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                        Status = ReservationStatus.Active
                    });
                    await context.SaveChangesAsync();

                    (await context.FlashSales.CountAsync()).Should().Be(1);
                    (await context.InventoryReservations.CountAsync()).Should().Be(1);
                }
            }
            finally
            {
                // Always drop the throwaway database so nothing leaks on the shared server.
                await DropIsolatedDatabaseAsync(_fixture.StoreConnectionString, databaseName);
            }
        }

        [Fact]
        public async Task StoreDatabase_RollingBackFlashSaleMigration_DropsFeatureTablesAndPreservesPriorData()
        {
            // Review finding M9: prove the additive migration's Down is safe even against a populated feature
            // schema — it drops the two tables in correct reverse-dependency order (InventoryReservations
            // before FlashSales), leaves all pre-existing catalog data intact, and keeps Products.Version
            // (owned by the earlier migration). Runs on its own ISOLATED, disposable database.
            var databaseName = "blitzy_adhoc_rollback_" + Guid.NewGuid().ToString("N");
            var connectionString =
                await CreateIsolatedDatabaseAsync(_fixture.StoreConnectionString, databaseName);

            try
            {
                int seededProductId;

                // 1) Migrate fully forward and populate BOTH prior data and feature-table rows.
                await using (var context = CreateStoreContext(connectionString))
                {
                    await context.Database.MigrateAsync();
                    seededProductId = await SeedRepresentativePriorDataAsync(context);

                    var sale = new FlashSale
                    {
                        ProductId = seededProductId,
                        StartAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                        EndAt = DateTimeOffset.UtcNow.AddHours(1),
                        SalePrice = 49.99m,
                        StockAllocation = 50
                    };
                    context.FlashSales.Add(sale);
                    await context.SaveChangesAsync();

                    context.InventoryReservations.Add(new InventoryReservation
                    {
                        FlashSaleId = sale.Id,
                        ProductId = seededProductId,
                        Quantity = 2,
                        SessionId = Guid.NewGuid().ToString(),
                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                        Status = ReservationStatus.Active
                    });
                    await context.SaveChangesAsync();
                }

                // 2) Roll the additive migration BACK (runs its Down). This must not throw even with rows present.
                await using (var context = CreateStoreContext(connectionString))
                {
                    var priorMigrationId = ResolveMigrationId(context, "AddProductVersionConcurrencyToken");
                    Func<Task> rollback = async () => await MigrateToAsync(context, priorMigrationId);
                    await rollback.Should().NotThrowAsync(
                        "the additive migration's Down must drop the feature tables in safe reverse-dependency order");
                }

                // 3) Verify the feature tables are gone, pre-existing data survives, and Products.Version stays.
                await using (var context = CreateStoreContext(connectionString))
                {
                    var tables = await GetPublicTableNamesAsync(context);
                    tables.Should().NotContain("FlashSales");
                    tables.Should().NotContain("InventoryReservations");
                    tables.Should().Contain(new[]
                    {
                        "Products", "ProductBrands", "ProductTypes", "DeliveryMethods"
                    });

                    (await context.Products.CountAsync()).Should().Be(1);
                    (await context.Products.SingleAsync()).Id.Should().Be(seededProductId);
                    // Products.Version belongs to the earlier migration, so rolling back only the Flash-Sale
                    // migration must NOT remove it.
                    (await GetColumnMetadataAsync(context, "Products")).Should().ContainKey("Version");

                    // The additive migration is no longer recorded as applied.
                    (await context.Database.GetAppliedMigrationsAsync())
                        .Should().NotContain(id => id.EndsWith("AddFlashSaleAndInventoryReservation"));
                }
            }
            finally
            {
                await DropIsolatedDatabaseAsync(_fixture.StoreConnectionString, databaseName);
            }
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
        /// Reads the EXACT column catalog (column-name -> <see cref="ColumnMetadata"/>) for a public table
        /// from the REAL PostgreSQL catalog (information_schema.columns): the mapped store type, nullability,
        /// character length (for character types) and numeric precision/scale (for numeric types). Review
        /// finding M9: this supersedes the former type-only reader so callers can assert the exact type,
        /// nullability, length and precision of every column rather than a tolerant non-empty presence check.
        /// Mirrors <see cref="GetPublicTableNamesAsync"/>'s connection-ownership-safe pattern: it opens the
        /// DbContext's own borrowed connection only if it was closed, and closes it again only if it opened
        /// it (never disposing a connection it did not own).
        /// </summary>
        private static async Task<Dictionary<string, ColumnMetadata>> GetColumnMetadataAsync(
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
                    "SELECT column_name, data_type, is_nullable, character_maximum_length, " +
                    "numeric_precision, numeric_scale FROM information_schema.columns " +
                    "WHERE table_schema = 'public' AND table_name = @tableName;";

                var parameter = command.CreateParameter();
                parameter.ParameterName = "tableName";
                parameter.Value = tableName;
                command.Parameters.Add(parameter);

                var columns = new Dictionary<string, ColumnMetadata>();
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    // character_maximum_length / numeric_precision / numeric_scale are NULL for columns to
                    // which they do not apply; read them defensively as nullable ints (Convert.ToInt32 keeps
                    // this robust to whichever integer domain type the catalog surfaces them as).
                    columns[reader.GetString(0)] = new ColumnMetadata
                    {
                        DataType = reader.GetString(1),
                        IsNullable = string.Equals(reader.GetString(2), "YES", StringComparison.OrdinalIgnoreCase),
                        MaxLength = reader.IsDBNull(3) ? (int?)null : Convert.ToInt32(reader.GetValue(3)),
                        NumericPrecision = reader.IsDBNull(4) ? (int?)null : Convert.ToInt32(reader.GetValue(4)),
                        NumericScale = reader.IsDBNull(5) ? (int?)null : Convert.ToInt32(reader.GetValue(5))
                    };
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
        /// Reads constraint definitions (constraint-name -> canonical <c>pg_get_constraintdef</c> text) for a
        /// public table from the REAL PostgreSQL catalog (pg_constraint), filtered to a single constraint kind:
        /// <c>'f'</c> foreign key, <c>'c'</c> check, or <c>'p'</c> primary key. <c>pg_get_constraintdef</c>
        /// renders the exact constraint text — the referenced table/column and <c>ON DELETE</c> behaviour for
        /// FKs, the boolean predicate for CHECKs, and the key columns for PKs — enabling review finding M9's
        /// exact FK / delete-behaviour / quantity-check / primary-key assertions. The <paramref name="constraintType"/>
        /// is a compile-time-constant char supplied by this test class (never user input), so it is embedded
        /// directly as a SQL character literal (safe from injection). Same connection-ownership-safe pattern as
        /// the sibling readers.
        /// </summary>
        private static async Task<Dictionary<string, string>> GetConstraintDefinitionsAsync(
            StoreContext context, string tableName, char constraintType)
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
                    "SELECT con.conname, pg_get_constraintdef(con.oid) " +
                    "FROM pg_constraint con " +
                    "JOIN pg_class rel ON rel.oid = con.conrelid " +
                    "JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace " +
                    "WHERE nsp.nspname = 'public' AND rel.relname = @tableName " +
                    "AND con.contype = '" + constraintType + "';";

                var parameter = command.CreateParameter();
                parameter.ParameterName = "tableName";
                parameter.Value = tableName;
                command.Parameters.Add(parameter);

                var constraints = new Dictionary<string, string>();
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    constraints[reader.GetString(0)] = reader.GetString(1);
                }

                return constraints;
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
        /// Reads index definitions (index-name -> the full <c>CREATE INDEX</c> text from
        /// <c>pg_indexes.indexdef</c>) for a public table from the REAL PostgreSQL catalog. The indexdef text
        /// encodes the exact indexed columns AND their order, enabling review finding M9's exact
        /// index-composition assertions (not merely index-name presence). Same connection-ownership-safe
        /// pattern as the sibling readers.
        /// </summary>
        private static async Task<Dictionary<string, string>> GetIndexDefinitionsAsync(
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
                    "SELECT indexname, indexdef FROM pg_indexes " +
                    "WHERE schemaname = 'public' AND tablename = @tableName;";

                var parameter = command.CreateParameter();
                parameter.ParameterName = "tableName";
                parameter.Value = tableName;
                command.Parameters.Add(parameter);

                var indexes = new Dictionary<string, string>();
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    indexes[reader.GetString(0)] = reader.GetString(1);
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

        /// <summary>
        /// Provisions a fresh, uniquely-named, EMPTY database on the SAME Testcontainers PostgreSQL server as
        /// the fixture's shared Store database, and returns a connection string targeting it. Review finding
        /// M9: the upgrade/rollback tests run their destructive migrate-up/-down sequences here so they never
        /// disturb the shared, already-migrated-and-seeded e-commerce database the rest of this class depends
        /// on. CREATE DATABASE cannot run inside a transaction, so it is issued over a plain autocommit Npgsql
        /// command (the fixture provisions its Identity database the same way). The database name is a
        /// GUID-suffixed compile-time-safe identifier (never user input), so quoting it inline is injection-safe.
        /// </summary>
        private static async Task<string> CreateIsolatedDatabaseAsync(
            string serverConnectionString, string databaseName)
        {
            await using var connection = new NpgsqlConnection(serverConnectionString);
            await connection.OpenAsync();

            await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\";", connection))
            {
                await command.ExecuteNonQueryAsync();
            }

            return new NpgsqlConnectionStringBuilder(serverConnectionString)
            {
                Database = databaseName
            }.ConnectionString;
        }

        /// <summary>
        /// Drops the isolated database created by <see cref="CreateIsolatedDatabaseAsync"/> (invoked from a
        /// finally block so no throwaway catalogue leaks on the shared server). EF Core pools connections, so
        /// the pool is cleared and any lingering backend sessions on the target database are terminated before
        /// DROP DATABASE runs — PostgreSQL refuses to drop a database that still has active sessions. The DROP
        /// is issued against the shared server database (never the one being dropped).
        /// </summary>
        private static async Task DropIsolatedDatabaseAsync(
            string serverConnectionString, string databaseName)
        {
            // Release any pooled connections EF opened against the isolated database.
            NpgsqlConnection.ClearAllPools();

            await using var connection = new NpgsqlConnection(serverConnectionString);
            await connection.OpenAsync();

            await using (var terminate = new NpgsqlCommand(
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
                "WHERE datname = @databaseName AND pid <> pg_backend_pid();", connection))
            {
                terminate.Parameters.AddWithValue("databaseName", databaseName);
                await terminate.ExecuteNonQueryAsync();
            }

            await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\";", connection))
            {
                await drop.ExecuteNonQueryAsync();
            }
        }

        /// <summary>
        /// Builds a standalone <see cref="StoreContext"/> bound to an explicit connection string (an isolated
        /// database) using the same Npgsql provider the application uses. The StoreContext's own assembly owns
        /// the migrations, so no MigrationsAssembly override is needed; callers drive migrations through
        /// <see cref="IMigrator"/> or <c>Database.MigrateAsync()</c>. The caller owns the returned context's
        /// lifetime and must dispose it.
        /// </summary>
        private static StoreContext CreateStoreContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<StoreContext>()
                .UseNpgsql(connectionString)
                .Options;
            return new StoreContext(options);
        }

        /// <summary>
        /// Resolves the full migration id (<c>timestamp_name</c>) whose name ends with
        /// <paramref name="suffix"/> from the migrations defined in the StoreContext assembly, so the
        /// upgrade/rollback tests target migrations by stable suffix rather than a hard-coded timestamp.
        /// </summary>
        private static string ResolveMigrationId(StoreContext context, string suffix) =>
            context.Database.GetMigrations().Single(id => id.EndsWith(suffix, StringComparison.Ordinal));

        /// <summary>
        /// Migrates the given context UP or DOWN to an explicit target migration state using the EF Core
        /// <see cref="IMigrator"/> (the mechanism EF itself uses). Passing a target earlier than the current
        /// state runs the intervening migrations' Down methods — exactly how the rollback test exercises the
        /// additive migration's Down safety.
        /// </summary>
        private static Task MigrateToAsync(StoreContext context, string targetMigrationId) =>
            context.Database.GetService<IMigrator>().MigrateAsync(targetMigrationId);

        /// <summary>
        /// Seeds a minimal but representative set of PRIOR-schema rows — one brand, one type, one product that
        /// references them, and one delivery method — so the upgrade/rollback tests can prove pre-existing data
        /// survives the additive Flash-Sale migration and that the migrated product can back a real FlashSale
        /// and InventoryReservation. Returns the created product's id. Product.Version defaults to 0 (the oid
        /// concurrency token is not auto-generated).
        /// </summary>
        private static async Task<int> SeedRepresentativePriorDataAsync(StoreContext context)
        {
            var brand = new ProductBrand { Name = "M9UpgradeBrand" };
            var type = new ProductType { Name = "M9UpgradeType" };
            context.ProductBrands.Add(brand);
            context.ProductTypes.Add(type);
            await context.SaveChangesAsync();

            var product = new Product
            {
                Name = "M9 Upgrade Product",
                Description = "Representative product seeded before the additive Flash-Sale migration.",
                Price = 123.45m,
                PictureUrl = "images/products/m9-upgrade.png",
                ProductBrandId = brand.Id,
                ProductTypeId = type.Id
            };
            context.Products.Add(product);

            context.DeliveryMethods.Add(new DeliveryMethod
            {
                ShortName = "M9UP",
                DeliveryTime = "1-2 Days",
                Description = "Representative delivery method seeded before the additive migration.",
                Price = 7.50m
            });
            await context.SaveChangesAsync();

            return product.Id;
        }

        /// <summary>
        /// Exact column-catalog metadata for a single column read from information_schema.columns: the
        /// PostgreSQL data type, nullability, and (where applicable) character length / numeric precision and
        /// scale. Backs the review-finding-M9 exact-catalog assertions.
        /// </summary>
        private sealed class ColumnMetadata
        {
            public string DataType { get; set; }
            public bool IsNullable { get; set; }
            public int? MaxLength { get; set; }
            public int? NumericPrecision { get; set; }
            public int? NumericScale { get; set; }
        }

    }
}
