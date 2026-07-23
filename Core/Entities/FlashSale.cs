using System;

namespace Core.Entities
{
    public class FlashSale:BaseEntity
    {
        public int ProductId { get; set; }
        public DateTimeOffset StartAt { get; set; }
        public DateTimeOffset EndAt { get; set; }
        public decimal SalePrice { get; set; }
        public int StockAllocation { get; set; }

        // Flash-Sale feature (review finding F04): THIS is the single, authoritative optimistic-concurrency
        // token for the zero-oversell guarantee (AAP R3). The flash sale row owns StockAllocation, so the
        // allocation guard lives here — NOT on Product.Version (which is a general product-row token with a
        // distinct role and never participates in the allocation check).
        //
        // Contract for every stock-allocating write (reserve/consume/release/expire) in
        // InventoryReservationService / ReservationExpirySweepService:
        //   1. read the FlashSale (capturing Version),
        //   2. recompute availability,
        //   3. increment this Version and SaveChanges within the same unit of work.
        // EF Core's IsConcurrencyToken() only puts Version in the UPDATE WHERE clause — it does NOT
        // auto-generate a new value — so the write MUST explicitly increment it. A stale token throws
        // DbUpdateConcurrencyException, which the service reloads-and-retries exactly ONCE, then returns
        // RESERVATION_CONFLICT (AAP §0.1.2). Configured via FlashSaleConfiguration.IsConcurrencyToken().
        public uint Version { get; set; }
    }
}
