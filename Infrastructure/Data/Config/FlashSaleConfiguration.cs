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
            // Flash-Sale feature: optimistic-concurrency guard against oversell
            builder.Property(x => x.Version).IsConcurrencyToken();
            // Flash-Sale feature: join/lookup by product
            builder.HasIndex(x => x.ProductId);
            // Flash-Sale feature: active-window (StartAt <= now <= EndAt) queries
            builder.HasIndex(x => new { x.StartAt, x.EndAt });
        }
    }
}
