using System.Collections.Generic;
using System.Threading.Tasks;
using Core.Entities;

namespace Core.Interfaces
{
    public interface IInventoryReservationService
    {
        // Optimistic-concurrency reserve. Resolves the product's active flash sale internally and records the
        // resulting FlashSaleId on the reservation (sale-scoped hold, review finding F01). Performs a
        // read-modify-write guarded by FlashSale.Version (the single allocation authority, review finding
        // F04): read the sale (capturing Version), recompute availability, and atomically INCREMENT
        // FlashSale.Version on save. On a version conflict (DbUpdateConcurrencyException — an Infrastructure
        // concern, not referenced here) reload and retry the read-modify-write EXACTLY ONCE; a second
        // conflict => Outcome.Conflict. Do NOT retry more than once server-side (AAP §0.1.2 binding example).
        // If quantity > available => Outcome.InsufficientStock with Available = N and NO partial reservation.
        // On success, Reservation is populated (Status = Active) and InventoryUpdated is broadcast via
        // IInventoryBroadcaster. sessionId is the basket UUID reused as the session key (no new identity).
        Task<ReservationResult> ReserveAsync(int productId, int quantity, string sessionId);

        // Explicit release for DELETE /api/inventory/reserve/{id} (review finding F02). Requires the owning
        // sessionId so a caller can only release a hold it owns: the implementation matches the reservation by
        // BOTH Id and SessionId atomically (served by the (SessionId, ProductId) index) and returns:
        //   - ReleaseOutcome.Released  when the row exists, is owned by sessionId, and was released;
        //   - ReleaseOutcome.NotFound  when no reservation with that Id exists;
        //   - ReleaseOutcome.Forbidden when the row exists but is owned by a different sessionId.
        // Releasing sets Status = Released (returns stock to the available pool) and rebroadcasts InventoryUpdated.
        Task<ReleaseOutcome> ReleaseAsync(int reservationId, string sessionId);

        // Checkout hook (review finding F03): invoked by OrderService.CreateOrderAsync AFTER a successful
        // _unitOfWork.Complete(). Consumes ONLY the caller session's ACTIVE reservations that match the supplied
        // ordered lines (by ProductId), transitioning each matched reservation to Status = Consumed. Consumed
        // rows REMAIN subtracted from availability (the units are sold) — the hold is NEVER deleted — which is
        // what preserves the zero-oversell invariant (AAP R3). The predicate keys on (SessionId, ProductId),
        // matching the (SessionId, ProductId) index. Defensive: an unmatched line is skipped so a missing
        // reservation never breaks the existing order flow.
        Task ConsumeReservationsAsync(string sessionId, IEnumerable<ReservationConsumeLine> orderedLines);
    }

    public enum ReservationOutcome
    {
        Success,
        InsufficientStock,
        Conflict
    }

    // Result of an explicit release (review finding F02): distinguishes ownership-forbidden from not-found so
    // the controller can map to 204 (Released), 404 (NotFound), or 403 (Forbidden) respectively.
    public enum ReleaseOutcome
    {
        Released,
        NotFound,
        Forbidden
    }

    // Plain result the API translates into exact HTTP responses without coupling Core to ASP.NET.
    public class ReservationResult
    {
        public ReservationOutcome Outcome { get; set; }
        public InventoryReservation Reservation { get; set; } // populated on Success
        public int Available { get; set; }                    // INSUFFICIENT_STOCK => N; Success => remaining
    }

    // Immutable ordered line supplied by the checkout hook (review finding F03). Carries only the product +
    // quantity from the placed order; the service resolves which ACTIVE reservation(s) for the session/product
    // to consume. Immutable (get-only, set once via the constructor) so the checkout caller cannot mutate the
    // consumed lines mid-operation.
    public class ReservationConsumeLine
    {
        public ReservationConsumeLine(int productId, int quantity)
        {
            ProductId = productId;
            Quantity = quantity;
        }

        public int ProductId { get; }
        public int Quantity { get; }
    }
}
