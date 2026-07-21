using Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Config
{
    public class InventoryReservationConfiguration:IEntityTypeConfiguration<InventoryReservation>
    {
        public void Configure(EntityTypeBuilder<InventoryReservation> builder)
        {
            // Flash-Sale feature: SessionId is the basket UUID (localStorage['basket_id']) reused as the session key — always present
            builder.Property(x => x.SessionId).IsRequired();
            // Flash-Sale feature: availability aggregation by product
            builder.HasIndex(x => x.ProductId);
            // Flash-Sale feature: efficient expiry sweep by the ReservationExpirySweepService
            builder.HasIndex(x => x.ExpiresAt);
            // Flash-Sale feature: per-session-per-product lookups
            builder.HasIndex(x => new { x.ProductId, x.SessionId });
        }
    }
}
