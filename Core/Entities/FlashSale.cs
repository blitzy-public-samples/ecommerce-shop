using System;

namespace Core.Entities
{
    public class FlashSale : BaseEntity
    {
        public int ProductId { get; set; }
        public int SaleStockQuantity { get; set; }
        public DateTimeOffset StartsAt { get; set; }
        public DateTimeOffset EndsAt { get; set; }
        public FlashSaleStatus Status { get; set; }
    }
}
