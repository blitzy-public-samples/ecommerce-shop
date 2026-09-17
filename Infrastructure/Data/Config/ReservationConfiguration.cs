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

            // Composite index accelerating the reconciliation expired-reservation scan
            // (WHERE "Status" = 'Active' AND "ExpiresAt" < now). Without it PostgreSQL performs
            // a sequential scan of the Reservations table on every reconciliation pass (F4).
            // Provider-agnostic: SQLite EnsureCreated builds it from this configuration,
            // PostgreSQL builds it via the migration Up(), and the in-memory provider ignores it.
            builder.HasIndex(r => new { r.Status, r.ExpiresAt });
        }
    }
}
