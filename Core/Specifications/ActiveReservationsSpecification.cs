using Core.Entities;

namespace API.Specifications
{
    public class ActiveReservationsSpecification : BaseSpecification<Reservation>
    {
        // All active reservations for a product (sum their Quantity to compute available stock).
        public ActiveReservationsSpecification(int productId)
            : base(r => r.Status == ReservationStatus.Active && r.ProductId == productId)
        {
        }

        // All active reservations for a basket (commit/extend a basket's holds).
        public ActiveReservationsSpecification(string basketId)
            : base(r => r.Status == ReservationStatus.Active && r.BasketId == basketId)
        {
        }

        // A specific basket's active hold on a specific product.
        public ActiveReservationsSpecification(int productId, string basketId)
            : base(r => r.Status == ReservationStatus.Active && r.ProductId == productId && r.BasketId == basketId)
        {
        }
    }
}
