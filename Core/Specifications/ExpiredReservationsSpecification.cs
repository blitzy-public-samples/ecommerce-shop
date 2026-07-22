using System;
using Core.Entities;

namespace API.Specifications
{
    public class ExpiredReservationsSpecification : BaseSpecification<Reservation>
    {
        public ExpiredReservationsSpecification(DateTimeOffset now)
            : base(r => r.Status == ReservationStatus.Active && r.ExpiresAt < now)
        {
        }
    }
}
