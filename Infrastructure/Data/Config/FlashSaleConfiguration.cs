using Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Config
{
    public class FlashSaleConfiguration:IEntityTypeConfiguration<FlashSale>
    {
        public void Configure(EntityTypeBuilder<FlashSale> builder)
        {
            // Flash-Sale feature: sale price mirrors Product.Price money precision
            builder.Property(x => x.SalePrice).HasColumnType("decimal(18,2)");
            // Flash-Sale feature (review finding F04): FlashSale.Version is the SINGLE authoritative
            // optimistic-concurrency token for the zero-oversell guarantee (AAP R3) — the flash sale row
            // owns StockAllocation. IsConcurrencyToken() places Version in the UPDATE WHERE clause but does
            // NOT auto-generate values; the reservation/consume/release/sweep flows MUST explicitly
            // increment FlashSale.Version on every stock-allocating write (see FlashSale.Version remarks).
            // Review finding M20: CLR `uint` maps to the PostgreSQL `oid` type under Npgsql; state it
            // explicitly so entity/config/migration/snapshot agree on the provider-native store type.
            builder.Property(x => x.Version).IsConcurrencyToken().HasColumnType("oid");

            // Flash-Sale feature (review finding M19): a flash sale MUST reference a real product. Configure the
            // required FK FlashSale.ProductId -> Products.Id (scalar FK; the entity has no navigation, matching the
            // repository convention). DeleteBehavior.Restrict is intentional: a product that still has scheduled/
            // historical sales cannot be silently deleted out from under them (no cascade data loss of sale rows).
            // EF Core creates the supporting index for this FK by convention; the explicit HasIndex below reuses it.
            builder.HasOne<Product>()
                .WithMany()
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Restrict);

            // Flash-Sale feature: join/lookup by product (also serves the FK above).
            builder.HasIndex(x => x.ProductId);
            // Flash-Sale feature: active-window (StartAt <= now <= EndAt) queries
            builder.HasIndex(x => new { x.StartAt, x.EndAt });

            // Flash-Sale feature (review finding M2 — database-integrity backstops): provider-compatible CHECK
            // constraints enforce the flash-sale invariants at the DEEPEST layer, so a direct/buggy writer can
            // never persist a row that would corrupt availability accounting or misroute events, even though the
            // service layer (FlashSaleService.ScheduleAsync) already validates these on the write path:
            //   * SalePrice > 0        — a sale must carry a positive discounted price (money, decimal(18,2)).
            //   * StockAllocation > 0  — a sale must allocate at least one unit of stock.
            //   * EndAt > StartAt      — the [StartAt, EndAt] window must be non-empty and correctly ordered.
            // These are pure single-row predicates (no cross-table reference), so they are emitted verbatim into
            // the additive migration and behave identically on PostgreSQL and on the relational test providers.
            // (ProductId <-> FlashSaleId consistency between a reservation and its sale is NOT expressible as a
            // single-row CHECK; it is guaranteed by construction — ReserveAsync stamps both the ProductId and the
            // FlashSaleId from the SAME deterministically-selected sale, whose ProductId equals that productId —
            // and both columns are independently FK-protected. A composite (FlashSaleId, ProductId) FK would
            // require a new alternate key / unique index on FlashSales(Id, ProductId), which would break the
            // frozen exact migration catalog contract and the AAP §0.5.2 minimal-change clause, so it is
            // deliberately not added.)
            builder.HasCheckConstraint("CK_FlashSales_SalePrice_Positive", "\"SalePrice\" > 0");
            builder.HasCheckConstraint("CK_FlashSales_StockAllocation_Positive", "\"StockAllocation\" > 0");
            builder.HasCheckConstraint("CK_FlashSales_EndAt_After_StartAt", "\"EndAt\" > \"StartAt\"");
        }
    }
}
