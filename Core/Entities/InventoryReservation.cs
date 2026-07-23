using System;

namespace Core.Entities
{
    public class InventoryReservation:BaseEntity
    {
        // Flash-Sale feature (review finding F01): the flash sale this hold is allocated against.
        // Reservations MUST be sale-scoped, not merely product-scoped: a product can have sequential
        // or overlapping sales, and availability is computed per sale
        // (FlashSale.StockAllocation - SUM of stock-holding reservations for THIS FlashSaleId).
        // Keying only on ProductId would let one sale's holds contaminate another sale's availability.
        // Scalar FK id only (no navigation property) to match the repository's existing convention.
        public int FlashSaleId { get; set; }
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        // Reuses the client basket UUID (localStorage['basket_id']) as the session key — no new identity concept.
        public string SessionId { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }

        // Flash-Sale feature (review finding F01): durable reservation lifecycle state. Availability
        // subtracts BOTH active non-expired holds AND Consumed (sold) rows for the sale, so checkout
        // must transition the hold to Consumed (never delete it) to preserve zero-oversell (AAP R3):
        //   quantityAvailable = FlashSale.StockAllocation
        //                       - SUM(Quantity WHERE FlashSaleId = X
        //                             AND (Status = Consumed OR (Status = Active AND ExpiresAt > now())))
        // Defaults to Active (enum value 0) for a freshly created hold. Persisted as int by EF Core.
        public ReservationStatus Status { get; set; }
    }
}
