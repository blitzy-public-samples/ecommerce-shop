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
        }
    }
}
