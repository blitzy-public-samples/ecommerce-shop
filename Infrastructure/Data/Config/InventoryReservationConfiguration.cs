using Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Config
{
    public class InventoryReservationConfiguration:IEntityTypeConfiguration<InventoryReservation>
    {
        public void Configure(EntityTypeBuilder<InventoryReservation> builder)
        {
            // Flash-Sale feature (review finding F05): SessionId is the client basket UUID
            // (localStorage['basket_id'], a canonical 36-char UUID v4) reused as the session key — always
            // present, and bounded to 36 chars to match the API-layer UUID validation (ReserveInventoryDto)
            // and to prevent unbounded text storage / session-cardinality abuse.
            builder.Property(x => x.SessionId).IsRequired().HasMaxLength(36);
            // Flash-Sale feature (review finding F01/F06): per-sale availability aggregation. Availability is
            // computed per FlashSaleId, filtered by Status (Active/Consumed rows hold stock), so lead the
            // index with (FlashSaleId, Status).
            builder.HasIndex(x => new { x.FlashSaleId, x.Status });
            // Flash-Sale feature (review finding F06): checkout-consume and explicit release match reservations
            // by (SessionId, ProductId). A SessionId-LEADING composite serves that predicate; the previous
            // ProductId-leading composite could not serve session-first lookups.
            builder.HasIndex(x => new { x.SessionId, x.ProductId });
            // Flash-Sale feature: product-scoped queries and per-product hub broadcasts.
            builder.HasIndex(x => x.ProductId);
            // Flash-Sale feature: efficient expiry sweep by the ReservationExpirySweepService (rows past ExpiresAt).
            builder.HasIndex(x => x.ExpiresAt);
        }
    }
}
