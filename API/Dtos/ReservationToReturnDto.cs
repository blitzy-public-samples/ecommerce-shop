using System;

namespace API.Dtos
{
    public class ReservationToReturnDto
    {
        public int Id { get; set; }
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        public string SessionId { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
