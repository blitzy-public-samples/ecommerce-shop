using System;

namespace Core.Entities
{
    public class Reservation : BaseEntity
    {
        public int ProductId { get; set; }
        public string BasketId { get; set; }
        public int Quantity { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public ReservationStatus Status { get; set; }
        public int? FlashSaleId { get; set; }
    }
}
