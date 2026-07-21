using System.Threading.Tasks;
using Core.Entities;

namespace Core.Interfaces
{
    public interface IInventoryReservationService
    {
        // Optimistic-concurrency reserve. Retries the read-modify-write once internally on a version
        // conflict; a second conflict => Outcome.Conflict. If requested > available => Outcome.InsufficientStock
        // with Available = N and NO partial reservation. On success, Reservation is populated.
        Task<ReservationResult> ReserveAsync(int productId, int quantity, string sessionId);

        // Explicit release for DELETE /api/inventory/reserve/{id}; false if not found.
        Task<bool> ReleaseAsync(int reservationId);

        // Checkout hook: consume the session's active reservations after a successful order write.
        Task ConsumeReservationsForSessionAsync(string sessionId);
    }

    public enum ReservationOutcome
    {
        Success,
        InsufficientStock,
        Conflict
    }

    // Plain result the API translates into exact HTTP responses without coupling Core to ASP.NET.
    public class ReservationResult
    {
        public ReservationOutcome Outcome { get; set; }
        public InventoryReservation Reservation { get; set; } // populated on Success
        public int Available { get; set; }                    // INSUFFICIENT_STOCK => N; Success => remaining
    }
}
