using System.IO;
using System.Net.NetworkInformation;
using Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Config
{
    public class ProductConfiguration:IEntityTypeConfiguration<Product>
    {
        public void Configure(EntityTypeBuilder<Product> builder)
        {
            builder.Property(p => p.Id).IsRequired();
            builder.Property(p => p.Name).IsRequired();
            builder.Property(p => p.Description).IsRequired();
            builder.Property(p => p.Price).HasColumnType("decimal(18,2)");
            builder.Property(p => p.PictureUrl).IsRequired();
            // Flash-Sale feature (review finding F04): general product-row optimistic-concurrency token
            // for concurrent Product mutations. This is deliberately NOT the flash-sale allocation
            // authority — the single oversell authority is FlashSale.Version (which owns StockAllocation).
            // The reservation/sweep flows never read or increment this token. (Not projected to any DTO.)
            builder.Property(p => p.Version).IsConcurrencyToken();
            builder.HasOne(b => b.ProductBrand).WithMany().HasForeignKey(p => p.ProductBrandId);
            builder.HasOne(t => t.ProductType).WithMany().HasForeignKey(p => p.ProductTypeId);
        }
    }
}