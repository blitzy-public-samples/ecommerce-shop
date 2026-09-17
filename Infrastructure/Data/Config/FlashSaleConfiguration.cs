using System;
using Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Config
{
    public class FlashSaleConfiguration : IEntityTypeConfiguration<FlashSale>
    {
        public void Configure(EntityTypeBuilder<FlashSale> builder)
        {
            builder.Property(f => f.Status)
                .HasConversion(
                    s => s.ToString(),
                    s => (FlashSaleStatus) Enum.Parse(typeof(FlashSaleStatus), s));

            builder.HasOne<Product>()
                .WithMany()
                .HasForeignKey(f => f.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
