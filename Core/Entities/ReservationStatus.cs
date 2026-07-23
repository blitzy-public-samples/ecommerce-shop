namespace Core.Entities
{
    // Flash-Sale feature: durable lifecycle state of an InventoryReservation (review finding F01).
    //
    // Availability for a flash sale is derived, NOT stored:
    //   quantityAvailable = FlashSale.StockAllocation
    //                       - SUM(Quantity) of reservations for that sale that still hold stock.
    //
    // A reservation "still holds stock" while it is either:
    //   * Active   — a live, non-expired hold (ExpiresAt > now); OR
    //   * Consumed — permanently sold at checkout (converted to an order).
    //
    // Released and Expired reservations DO NOT hold stock and are excluded from the sum. This
    // distinction is what preserves the zero-oversell invariant (AAP R3): consuming a reservation
    // at checkout MUST transition it to Consumed (never delete it), otherwise the sold units would
    // be returned to the available pool and the sale could oversell.
    public enum ReservationStatus
    {
        // A live hold that counts against availability until it expires, is released, or is consumed.
        Active,
        // The hold was converted to a completed sale at checkout; permanently subtracted from availability.
        Consumed,
        // The hold was explicitly released (DELETE) before checkout; returns stock to the available pool.
        Released,
        // The hold lapsed past ExpiresAt and was swept; returns stock to the available pool.
        Expired
    }
}
