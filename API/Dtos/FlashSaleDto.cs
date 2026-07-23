using System;

namespace API.Dtos
{
    public class FlashSaleDto
    {
        public int Id { get; set; }
        public int ProductId { get; set; }
        public decimal SalePrice { get; set; }
        public DateTimeOffset StartAt { get; set; }
        public DateTimeOffset EndAt { get; set; }
        public int StockAllocation { get; set; }

        // Computed live, SALE-SCOPED availability (review finding m09). This is NOT simply "allocation minus
        // active holds": it is the sale's StockAllocation minus BOTH the durable sold units AND the live holds
        // for THIS EXACT flash sale, i.e.
        //   QuantityAvailable = StockAllocation
        //     - SUM(Quantity WHERE FlashSaleId = <this sale>
        //           AND (Status = Consumed                               -- durable sold units, never returned
        //                OR (Status = Active AND ExpiresAt > now)))       -- live, non-expired holds
        // clamped at zero. Consumed (sold) stock is intentionally included so purchased units are never handed
        // back to the available pool, and aggregation is keyed by FlashSaleId (never ProductId) so a different
        // sale's history cannot contaminate this value.
        public int QuantityAvailable { get; set; }
    }
}
