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

        // Optimistic-concurrency token (row-version style); configured via
        // FlashSaleConfiguration.IsConcurrencyToken() in Infrastructure to guard against oversell.
        public uint Version { get; set; }
    }
}
