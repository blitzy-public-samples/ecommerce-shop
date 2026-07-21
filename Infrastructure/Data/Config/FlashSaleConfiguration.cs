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
            builder.Property(x => x.Version).IsConcurrencyToken();
            // Flash-Sale feature: join/lookup by product
            builder.HasIndex(x => x.ProductId);
            // Flash-Sale feature: active-window (StartAt <= now <= EndAt) queries
            builder.HasIndex(x => new { x.StartAt, x.EndAt });
        }
    }
}
