using System.Collections.Generic;
using System.Threading.Tasks;
using Core.Entities;

namespace Core.Interfaces
{
    public interface IInventoryReservationService
    {
        // Optimistic-concurrency reserve. FIRST validates the request at the service boundary (review finding
        // C05): quantity MUST be > 0 and sessionId MUST be a canonical UUID; otherwise it returns
        // Outcome.Invalid WITHOUT touching the database (the service does not trust the controller as the sole
        // validation layer, and a negative quantity can never persist a stock-inflating hold). It then resolves
        // the product's active flash sale DETERMINISTICALLY (review finding C06): a stable ordering selects a
        // single authority even under transient overlap, and the active window [StartAt, EndAt] is re-validated
        // on EVERY attempt (including after a conflict reload) immediately before commit, closing the TOCTOU
        // window. The hold records the resolved FlashSaleId (sale-scoped, review finding C04). The core is a
        // read-modify-write guarded by FlashSale.Version (the single allocation authority): read the sale
        // (capturing Version), recompute sale-scoped availability, and atomically INCREMENT FlashSale.Version on
        // save. On a version conflict (DbUpdateConcurrencyException) reload and retry EXACTLY ONCE; a second
        // conflict => Outcome.Conflict (never retried more than once server-side, AAP §0.1.2 binding example).
        // If quantity > available => Outcome.InsufficientStock with Available = N and NO partial reservation. On
        // success, Reservation is populated (Status = Active) and live availability is broadcast (after commit,
        // via the ordered IInventoryBroadcastCoordinator). sessionId is the basket UUID reused as the session
        // key (no new identity).
        Task<ReservationResult> ReserveAsync(int productId, int quantity, string sessionId);

        // Explicit release for DELETE /api/inventory/reserve/{id} (review findings C07, C09). Requires the owning
        // sessionId so a caller can only release a hold it owns, and performs a CONDITIONAL, atomic transition
        // from Active -> Released ONLY (guarded by the reservation's Status concurrency token so a concurrent
        // consume/expiry can never be overwritten). Returns:
        //   - ReleaseOutcome.NotFound  when no reservation with that Id exists;
        //   - ReleaseOutcome.Forbidden when the row exists but is owned by a different sessionId;
        //   - ReleaseOutcome.Conflict  when the row is owned but NOT Active (already Consumed/Released/Expired),
        //                              or a concurrent transition won the race — releasing sold or already-freed
        //                              stock is refused, so stock is never double-returned;
        //   - ReleaseOutcome.Released  after a successful Active -> Released transition; freed availability is
        //                              then re-broadcast via the ordered coordinator. Freeing stock can never
        //                              oversell, so the sale's allocation authority (Version) is not contended.
        Task<ReleaseOutcome> ReleaseAsync(int reservationId, string sessionId);

        // Checkout hook (review findings C08, C09, C10): invoked by OrderService.CreateOrderAsync AFTER a
        // successful _unitOfWork.Complete(). For each ordered line it consumes the caller session's ACTIVE,
        // non-expired hold for that product, honouring the EXACT ordered quantity and the hold's own FlashSaleId
        // (sale-scoped): a hold is consumed only up to the ordered quantity, never across sales, and never an
        // already-expired hold. Each transition is a CONDITIONAL, atomic Active -> Consumed guarded by the Status
        // concurrency token; Consumed rows REMAIN subtracted from availability (the units are sold) and are NEVER
        // deleted, which preserves the zero-oversell invariant (AAP R3). Fully defensive (C10): a null/empty set,
        // an unmatched line, or a lost concurrency race is skipped so a missing/contended hold never breaks the
        // existing order flow. Affected products' availability is re-broadcast via the ordered coordinator.
        Task ConsumeReservationsAsync(string sessionId, IEnumerable<ReservationConsumeLine> orderedLines);
    }

    public enum ReservationOutcome
    {
        Success,
        InsufficientStock,
        Conflict,
        // Flash-Sale feature (review finding C05): the request failed service-boundary validation (non-positive
        // quantity, or a missing/non-canonical session id). The service — not just the controller — is a trust
        // boundary, so it rejects such input BEFORE any database work and never persists a (e.g. negative) hold.
        // A future controller maps this to HTTP 400; it is distinct from the 409 stock/conflict outcomes.
        Invalid
    }

    // Result of an explicit release (review finding F02): distinguishes ownership-forbidden from not-found so
    // the controller can map to 204 (Released), 404 (NotFound), or 403 (Forbidden) respectively.
    public enum ReleaseOutcome
    {
        Released,
        NotFound,
        Forbidden,
        // Flash-Sale feature (review finding C07/C09): the reservation exists and is owned by the caller, but it
        // is NOT in a releasable (Active) state — it was already Consumed (sold), Released, or Expired. Release
        // performs a conditional Active -> Released transition ONLY, so it returns Conflict here instead of
        // restoring already-sold or already-freed stock. A future controller maps this to HTTP 409.
        Conflict
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
