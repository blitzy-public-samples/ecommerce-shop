using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services
{
    // Flash-Sale feature (AAP R3 — ZERO-OVERSELL CORE).
    //
    // This service is the zero-oversell heart of the Real-Time Inventory & Flash Sale feature. It reserves
    // flash-sale stock under high concurrency using an optimistic-concurrency read-modify-write guarded by the
    // FlashSale.Version token, releases reservations, and consumes a session's reservations after checkout.
    //
    // Hardening applied for the code review (Infrastructure layer, single-instance, in-memory — no distributed
    // lock, no SignalR backplane, AAP §0.4.2/§0.5.2):
    //   * C05 — every public method validates its input at the SERVICE boundary (positive quantity, canonical
    //     session, positive product) before any database work; the controller is not the sole trust boundary,
    //     and a negative quantity can never persist a stock-inflating hold. A DB CHECK constraint
    //     (CK_InventoryReservations_Quantity_Positive) is the deepest backstop.
    //   * C06 — the active flash sale is selected DETERMINISTICALLY and its [StartAt, EndAt] window is
    //     re-validated on EVERY attempt (the sale is re-queried each loop, including after a conflict), closing
    //     the reserve TOCTOU.
    //   * C07/C09 — release and consume perform CONDITIONAL, atomic Active -> {Released|Consumed} transitions
    //     guarded by the reservation's Status concurrency token (configured
    //     IsConcurrencyToken() in InventoryReservationConfiguration). A stale/lost race yields zero affected
    //     rows -> DbUpdateConcurrencyException, handled deterministically, so a Consumed (sold) hold can never
    //     be flipped back to Released/Expired and stock is never double-returned.
    //   * C08 — consume honours the EXACT ordered quantity per product and the hold's own FlashSaleId, never
    //     consuming across sales or an already-expired hold, and splits a hold when the order needs fewer units
    //     than the hold carries.
    //   * M12/M13 — all live-availability broadcasts go through the shared, ordered, persistence-authoritative
    //     IInventoryBroadcastCoordinator AFTER commit, so a broadcast failure never fails a persisted operation
    //     and concurrent broadcasts for a product are delivered in order with the latest committed value.
    //
    // Injection note: the CONCRETE StoreContext is injected (not IUnitOfWork) because the concurrency algorithm
    // needs low-level EF Core control the generic repository abstraction does not expose — namely
    // _context.Entry(...).State and catching DbUpdateConcurrencyException. StoreContext and this service both
    // live in the Infrastructure project, so there is no cross-project reference.
    public class InventoryReservationService : IInventoryReservationService
    {
        private readonly StoreContext _context;
        private readonly IInventoryBroadcastCoordinator _coordinator;
        private readonly IConfiguration _config;
        private readonly ILogger<InventoryReservationService> _logger;

        public InventoryReservationService(
            StoreContext context,
            IInventoryBroadcastCoordinator coordinator,
            IConfiguration config,
            ILogger<InventoryReservationService> logger)
        {
            _context = context;
            _coordinator = coordinator;
            _config = config;
            _logger = logger;
        }

        // Reservation time-to-live in seconds. Mirrors how TokenService/PaymentService read string configuration
        // via _config["Key"]; defaults to 300 seconds when the key is absent, non-numeric, or non-positive so a
        // mis-set environment can never yield a zero/negative TTL.
        private int ReservationTtlSeconds =>
            int.TryParse(_config["RESERVATION_TTL_SECONDS"], out var s) && s > 0 ? s : 300;

        // Flash-Sale feature (review finding C05): validate AND canonicalise the session id at the service
        // boundary. Accepts only a parseable UUID and normalises it to the canonical lowercase 8-4-4-4-12 form
        // (Guid "D"), so a hold is always stored under one stable key regardless of the caller's casing and a
        // malformed/whitespace session is rejected before any database work. Keeping normalisation here means
        // release/consume match reservations by the SAME canonical key that reserve stored.
        private static bool TryNormalizeSession(string sessionId, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(sessionId)) return false;

            // Flash-Sale feature (review finding M15): enforce the SAME canonical RFC 4122 version-4 UUID
            // contract as the API boundary (CanonicalUuidV4Attribute / ReserveInventoryDto) at THIS service
            // boundary too, so every layer keys a basket off exactly ONE identity and the "any Guid version"
            // gap is closed. Guid.TryParseExact(.., "D") accepts ONLY the 36-char hyphenated form (rejecting
            // the brace/parenthesis/no-hyphen "B"/"P"/"N" shapes and any non-parseable text); the version
            // nibble (canonical index 14) must be '4' and the variant nibble (index 19) must be one of
            // 8/9/a/b (RFC 4122 10xx). A non-v4 Guid — which the client uuidv4() can never emit — is rejected
            // before any database work, byte-for-byte matching the API validator so reserve/release/consume
            // all key off the identical canonical identity.
            if (!Guid.TryParseExact(sessionId.Trim(), "D", out var g)) return false;
            var canonical = g.ToString("D");
            if (canonical[14] != '4') return false;
            var variant = canonical[19];
            if (variant != '8' && variant != '9' && variant != 'a' && variant != 'b') return false;

            normalized = canonical;
            return true;
        }

        // Reserves <paramref name="quantity"/> units of the active flash sale for <paramref name="productId"/>
        // against the client basket session <paramref name="sessionId"/>.
        //
        // Zero-oversell guarantee: the staged reservation INSERT and the guarded FlashSale.Version bump commit
        // in a SINGLE SaveChangesAsync (one transaction). Concurrent reservers all read the same original
        // Version, but only one "SET Version = original+1 WHERE Version = original" wins; the losers see 0 rows
        // updated, EF throws DbUpdateConcurrencyException, and the ENTIRE transaction (including their INSERT)
        // rolls back — so no partial reservation ever persists. The read-modify-write is retried EXACTLY once
        // (binding user example, AAP §0.1.2); a second failure yields Outcome.Conflict.
        public async Task<ReservationResult> ReserveAsync(int productId, int quantity, string sessionId)
        {
            // C05 — service-boundary validation. Reject non-positive quantity/product and non-canonical session
            // BEFORE touching the database; never persist a (e.g. negative) stock-inflating hold.
            if (quantity <= 0 || productId <= 0 || !TryNormalizeSession(sessionId, out var session))
            {
                return new ReservationResult { Outcome = ReservationOutcome.Invalid };
            }

            const int maxAttempts = 2; // initial attempt + EXACTLY ONE retry (AAP §0.1.2) — never a third.
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var now = DateTimeOffset.UtcNow;

                // C06 — DETERMINISTIC active-sale selection + active-window revalidation on EVERY attempt.
                // Re-querying inside the loop (rather than loading once before it) means an EndAt that lapses
                // between attempts, or a reload after a conflict, is naturally revalidated: a sale whose window
                // has closed simply drops out of this query. A stable ordering (most-recently-started, then
                // highest id) selects ONE authority even under transient overlap, so behaviour is never
                // arbitrary. The re-query also captures the winner's committed Version after a conflict.
                var sale = await _context.FlashSales
                    .Where(fs => fs.ProductId == productId && fs.StartAt <= now && fs.EndAt >= now)
                    .OrderByDescending(fs => fs.StartAt)
                    .ThenByDescending(fs => fs.Id)
                    .FirstOrDefaultAsync();

                // No active authority (never started, already ended, or window closed between attempts). Make
                // NO reservation and report zero availability -> controller returns 409 INSUFFICIENT_STOCK{0}.
                if (sale == null)
                {
                    return new ReservationResult
                    {
                        Outcome = ReservationOutcome.InsufficientStock,
                        Available = 0
                    };
                }

                // available = StockAllocation - SUM(stock-holding reservations for THIS sale). A reservation
                // still holds stock when it is Consumed (sold) OR Active and not yet expired; Released/Expired
                // holds have returned their stock. Aggregating by FlashSaleId keeps one sale's holds from
                // contaminating another sale's availability. SumAsync over int returns 0 for an empty set.
                var reserved = await _context.InventoryReservations
                    .Where(r => r.FlashSaleId == sale.Id
                        && (r.Status == ReservationStatus.Consumed
                            || (r.Status == ReservationStatus.Active && r.ExpiresAt > now)))
                    .SumAsync(r => r.Quantity);

                var available = sale.StockAllocation - reserved;
                if (available < 0) available = 0; // never report negative availability.

                // Insufficient stock -> 409 INSUFFICIENT_STOCK{available:N}; persist NOTHING. On a retry this
                // fresh recompute sees the winner's committed reservation, so genuinely-exhausted stock
                // correctly resolves to InsufficientStock (NOT Conflict).
                if (quantity > available)
                {
                    return new ReservationResult
                    {
                        Outcome = ReservationOutcome.InsufficientStock,
                        Available = available
                    };
                }

                // Stage the sale-scoped, Active hold AND bump the guarded token so both commit in one txn.
                var reservation = new InventoryReservation
                {
                    FlashSaleId = sale.Id,
                    ProductId = productId,
                    Quantity = quantity,
                    SessionId = session,
                    ExpiresAt = now.AddSeconds(ReservationTtlSeconds),
                    Status = ReservationStatus.Active
                };
                _context.InventoryReservations.Add(reservation);

                // Marking FlashSale.Version as changed makes EF emit
                // "UPDATE FlashSales SET Version = @new WHERE Id = @id AND Version = @original" — the collision
                // point that serialises concurrent reservers.
                sale.Version += 1;

                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    // A concurrent reserver committed first: the guarded WHERE Version = @original matched 0
                    // rows, the whole transaction rolled back, and our INSERT did NOT persist. Detach BOTH the
                    // un-persisted hold and the stale sale so the single permitted retry RE-QUERIES a fresh,
                    // window-valid sale with the winner's committed Version (C06). If the retry also collides we
                    // fall through to the Conflict result — never a third write.
                    _context.Entry(reservation).State = EntityState.Detached;
                    _context.Entry(sale).State = EntityState.Detached;
                    continue;
                }

                // Committed. M12/M13: publish authoritative availability AFTER commit through the ordered
                // coordinator; a broadcast failure can never fail this durably-persisted reservation.
                await _coordinator.PublishAvailabilityAsync(productId, () => ComputeAvailabilityAsync(productId));

                return new ReservationResult
                {
                    Outcome = ReservationOutcome.Success,
                    Reservation = reservation,
                    Available = available - quantity
                };
            }

            // Both permitted attempts collided on the concurrency token -> 409 RESERVATION_CONFLICT.
            return new ReservationResult { Outcome = ReservationOutcome.Conflict };
        }

        // Explicit release backing DELETE /api/inventory/reserve/{id} (review findings C07, C09). Ownership is
        // enforced by comparing the caller's canonical session to the stored one, and the transition is a
        // CONDITIONAL, atomic Active -> Released ONLY.
        public async Task<ReleaseOutcome> ReleaseAsync(int reservationId, string sessionId)
        {
            var reservation = await _context.InventoryReservations.FindAsync(reservationId);
            if (reservation == null) return ReleaseOutcome.NotFound;

            // Ownership check against the canonical session. A malformed/foreign session can never own a hold.
            if (!TryNormalizeSession(sessionId, out var session) || reservation.SessionId != session)
            {
                return ReleaseOutcome.Forbidden;
            }

            // M6 — a repeat DELETE by the OWNER of an already-Released hold is IDEMPOTENT success: the stock is
            // already back in the pool, so reporting the same Released outcome (rather than 409) lets a retried
            // or duplicated cart-cancellation converge without a spurious error. Ownership was already proven
            // above, so this branch can never re-release a foreign hold.
            if (reservation.Status == ReservationStatus.Released)
            {
                return ReleaseOutcome.Released; // idempotent: this owner already released this hold.
            }

            // C07/C09 — refuse to "release" a Consumed (sold) or Expired hold. Restoring their stock would
            // return units that are not free to return (sold, or already swept back), so it is a Conflict, not
            // a release. Only an Active hold performs the real Active -> Released transition below.
            if (reservation.Status != ReservationStatus.Active)
            {
                return ReleaseOutcome.Conflict;
            }

            var productId = reservation.ProductId;
            reservation.Status = ReservationStatus.Released; // returns stock to the pool (never a delete).

            try
            {
                // The Status concurrency token makes EF emit
                // "UPDATE ... SET Status = Released WHERE Id = @id AND Status = @original(Active)". A concurrent
                // consume/expiry that already moved the row off Active matches 0 rows -> the exception below.
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // A concurrent transition won the race; do NOT overwrite it (C09). Reset and report Conflict.
                _context.Entry(reservation).State = EntityState.Detached;
                return ReleaseOutcome.Conflict;
            }

            // Freeing stock can never oversell, so the sale's allocation authority (Version) is not contended.
            // Re-broadcast the freed availability after commit via the ordered coordinator.
            await _coordinator.PublishAvailabilityAsync(productId, () => ComputeAvailabilityAsync(productId));
            return ReleaseOutcome.Released;
        }

        // Checkout hook (review findings C08, C09, C10): consumes this session's ACTIVE, non-expired holds that
        // match the ordered lines, honouring the EXACT ordered quantity per product and the hold's own
        // FlashSaleId. Consumed rows are NEVER deleted — they REMAIN subtracted from availability (the units are
        // sold), preserving the zero-oversell invariant (AAP R3). Fully defensive: null/empty/unmatched input
        // and lost concurrency races are skipped so the existing order flow is never broken.
        public async Task ConsumeReservationsAsync(string sessionId, IEnumerable<ReservationConsumeLine> orderedLines)
        {
            if (orderedLines == null) return;                                   // no-op; must never throw.
            if (!TryNormalizeSession(sessionId, out var session)) return;       // invalid session -> no-op.

            // Aggregate the ordered quantity per product (an order may list a product on several lines) and
            // ignore any non-positive/garbage line defensively.
            var orderedByProduct = orderedLines
                .Where(l => l != null && l.ProductId > 0 && l.Quantity > 0)
                .GroupBy(l => l.ProductId)
                .ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
            if (orderedByProduct.Count == 0) return;

            var now = DateTimeOffset.UtcNow;
            var productIds = orderedByProduct.Keys.ToList();

            // Only this session's ACTIVE, NON-EXPIRED holds for the ordered products are eligible (C08: never
            // consume an expired hold, a cross-session hold, or a cross-sale hold — the hold carries its own
            // FlashSaleId). Deterministic ordering keeps split behaviour stable.
            var holds = await _context.InventoryReservations
                .Where(r => r.SessionId == session
                    && r.Status == ReservationStatus.Active
                    && r.ExpiresAt > now
                    && productIds.Contains(r.ProductId))
                .OrderBy(r => r.ExpiresAt)
                .ThenBy(r => r.Id)
                .ToListAsync();
            if (holds.Count == 0) return;

            var affected = new HashSet<int>();

            foreach (var group in holds.GroupBy(h => h.ProductId))
            {
                var productId = group.Key;
                var remaining = orderedByProduct[productId]; // exact ordered quantity still to consume.

                foreach (var hold in group)
                {
                    if (remaining <= 0) break; // this product's order is fully satisfied.

                    // Consume the whole hold when it does not exceed what is still ordered; otherwise SPLIT it.
                    var wholeHold = hold.Quantity <= remaining;
                    var consumeQty = wholeHold ? hold.Quantity : remaining;
                    InventoryReservation remainderPart = null;

                    try
                    {
                        if (wholeHold)
                        {
                            hold.Status = ReservationStatus.Consumed; // conditional via Status token.
                        }
                        else
                        {
                            // C3 FIX (concurrent split double-sale): a partial consume now transitions the
                            // ORIGINAL hold's STATUS (Active -> Consumed) — carrying exactly the consumed
                            // quantity — and represents the untouched leftover as a NEW Active row. Previously
                            // the original row STAYED Active with only a decremented Quantity, an UNGUARDED
                            // field write: because Status (the sole concurrency token) was unchanged, two
                            // concurrent consumes of the same 10-unit hold could BOTH pass "WHERE Status =
                            // Active", each inserting a 6-unit Consumed row and each writing Quantity = 4 —
                            // selling 12 while 4 stayed held (an oversell, violating AAP R3). By flipping the
                            // original row's Status here, the guarded UPDATE ("SET Status = Consumed, Quantity =
                            // @consumeQty WHERE Id = @id AND Status = Active") is won by EXACTLY ONE consumer;
                            // the loser matches zero rows -> DbUpdateConcurrencyException -> defensive skip. The
                            // total held quantity is preserved (Consumed @consumeQty + new Active leftover ==
                            // the original hold), so the split itself never changes availability.
                            var leftover = hold.Quantity - consumeQty; // > 0 here (hold.Quantity > remaining).
                            hold.Status = ReservationStatus.Consumed;   // guarded Active -> Consumed transition.
                            hold.Quantity = consumeQty;                 // original row records exactly what sold.

                            remainderPart = new InventoryReservation
                            {
                                FlashSaleId = hold.FlashSaleId,
                                ProductId = hold.ProductId,
                                Quantity = leftover,
                                SessionId = hold.SessionId,
                                ExpiresAt = hold.ExpiresAt, // leftover keeps the original hold's TTL.
                                Status = ReservationStatus.Active
                            };
                            _context.InventoryReservations.Add(remainderPart);
                        }

                        // The Status concurrency token guards the hold's UPDATE (WHERE ... AND Status = Active);
                        // the split's leftover INSERT commits atomically in the same SaveChanges.
                        await _context.SaveChangesAsync();
                        remaining -= consumeQty;
                        affected.Add(productId);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        // A concurrent consume/expiry/release won this hold's Active transition first (C3/C09).
                        // Detach the pending changes (the un-persisted leftover INSERT and the rolled-back hold)
                        // and SKIP this hold — the order flow must never break (C10). `remaining` is left intact
                        // so the next eligible hold, if any, can still satisfy this order line.
                        if (remainderPart != null) _context.Entry(remainderPart).State = EntityState.Detached;
                        _context.Entry(hold).State = EntityState.Detached;
                        _logger.LogDebug(
                            "Flash-Sale: skipped consuming contended reservation {ReservationId} for product {ProductId}; a concurrent consume/expiry/release won the transition.",
                            hold.Id, productId);
                    }
                }
            }

            // M12/M13: re-broadcast affected products' availability after commit via the ordered coordinator.
            foreach (var productId in affected)
            {
                await _coordinator.PublishAvailabilityAsync(productId, () => ComputeAvailabilityAsync(productId));
            }
        }

        // Shared authoritative availability computation for the active sale of a product. Used by the ordered
        // broadcast coordinator (invoked INSIDE its per-product lock, immediately before a send) and mirrors the
        // reserve-path formula exactly: available = StockAllocation - SUM(stock-holding reservations for the
        // deterministically-selected active sale), clamped to >= 0; when no active sale exists availability is 0.
        private async Task<int> ComputeAvailabilityAsync(int productId)
        {
            var now = DateTimeOffset.UtcNow;

            var sale = await _context.FlashSales
                .Where(fs => fs.ProductId == productId && fs.StartAt <= now && fs.EndAt >= now)
                .OrderByDescending(fs => fs.StartAt)
                .ThenByDescending(fs => fs.Id)
                .FirstOrDefaultAsync();
            if (sale == null) return 0;

            var reserved = await _context.InventoryReservations
                .Where(r => r.FlashSaleId == sale.Id
                    && (r.Status == ReservationStatus.Consumed
                        || (r.Status == ReservationStatus.Active && r.ExpiresAt > now)))
                .SumAsync(r => r.Quantity);

            var available = sale.StockAllocation - reserved;
            return available < 0 ? 0 : available;
        }
    }
}
