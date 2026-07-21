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
        public int QuantityAvailable { get; set; }   // computed live: StockAllocation - SUM(active reservations)
    }
}
