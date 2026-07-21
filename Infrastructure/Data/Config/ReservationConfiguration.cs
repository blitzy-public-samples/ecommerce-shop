using System;
using Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Config
{
    public class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
    {
        public void Configure(EntityTypeBuilder<Reservation> builder)
        {
            builder.Property(r => r.Status)
                .HasConversion(
                    s => s.ToString(),
                    s => (ReservationStatus) Enum.Parse(typeof(ReservationStatus), s));

            builder.HasOne<Product>()
                .WithMany()
                .HasForeignKey(r => r.ProductId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne<FlashSale>()
                .WithMany()
                .HasForeignKey(r => r.FlashSaleId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
