using System;

namespace Core.Entities
{
    public class InventoryReservation:BaseEntity
    {
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        // Reuses the client basket UUID (localStorage['basket_id']) as the session key — no new identity concept.
        public string SessionId { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
