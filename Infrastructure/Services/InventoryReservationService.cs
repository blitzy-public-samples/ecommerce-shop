using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Services
{
    // Flash-Sale feature (AAP R3 — ZERO-OVERSELL CORE).
    //
    // This service is the zero-oversell heart of the Real-Time Inventory & Flash Sale feature. It
    // reserves flash-sale stock under high concurrency using an optimistic-concurrency
    // read-modify-write guarded by the FlashSale.Version token, releases reservations, and consumes a
    // session's reservations after checkout.
    //
    // Design constraints (AAP §0.4.2, §0.5.2):
    //   * All real-time broadcasting flows ONLY through Core.Interfaces.IInventoryBroadcaster; this
    //     service carries NO direct SignalR / API.Hubs dependency, keeping it unit-testable and free of
    //     the presentation layer.
    //   * The concurrency guard is single-instance and in-memory (a database concurrency token) — there
    //     is NO distributed lock and NO SignalR backplane.
    //   * The "10 req/min/session -> HTTP 429" rate limit is enforced by the API-layer
    //     SessionRateLimitFilter, NOT here.
    //
    // Injection note: the CONCRETE StoreContext is injected (not IUnitOfWork) because the concurrency
    // algorithm needs low-level EF Core control that the generic repository abstraction does not
    // expose — namely _context.Entry(sale).ReloadAsync(), EntityState.Detached, and catching
    // DbUpdateConcurrencyException. StoreContext and this service both live in the Infrastructure
    // project, so there is no cross-project reference.
    public class InventoryReservationService : IInventoryReservationService
    {
        private readonly StoreContext _context;
        private readonly IInventoryBroadcaster _broadcaster;
        private readonly IConfiguration _config;

        public InventoryReservationService(
            StoreContext context,
            IInventoryBroadcaster broadcaster,
            IConfiguration config)
        {
            _context = context;
            _broadcaster = broadcaster;
            _config = config;
        }

        // Reservation time-to-live in seconds. Mirrors how TokenService/PaymentService read string
        // configuration values via _config["Key"]; defaults to 300 seconds when the key is absent,
        // non-numeric, or non-positive so a mis-set environment can never yield a zero/negative TTL.
        private int ReservationTtlSeconds =>
            int.TryParse(_config["RESERVATION_TTL_SECONDS"], out var s) && s > 0 ? s : 300;

        // Reserves <paramref name="quantity"/> units of the active flash sale for <paramref name="productId"/>
        // against the client basket session <paramref name="sessionId"/>.
        //
        // Zero-oversell guarantee: the staged reservation INSERT and the guarded FlashSale.Version bump
        // are committed in a SINGLE SaveChangesAsync (one transaction). Concurrent reservers all read the
        // same original Version, but only one "SET Version = original+1 WHERE Version = original" wins;
        // the losers see 0 rows updated, EF throws DbUpdateConcurrencyException, and the ENTIRE
        // transaction (including their reservation INSERT) rolls back — so no partial reservation ever
        // persists. The read-modify-write is retried EXACTLY once (binding user example, AAP §0.1.2); a
        // second concurrency failure yields Outcome.Conflict and the caller never retries server-side.
        public async Task<ReservationResult> ReserveAsync(int productId, int quantity, string sessionId)
        {
            var now = DateTimeOffset.UtcNow;

            // Load the active flash sale for this product; its Version column is the concurrency guard.
            var sale = await _context.FlashSales
                .FirstOrDefaultAsync(fs => fs.ProductId == productId && fs.StartAt <= now && fs.EndAt >= now);

            // No active sale means there is no allocation to reserve against. Make NO reservation and
            // report zero availability so the controller returns 409 INSUFFICIENT_STOCK {available:0}.
            if (sale == null)
            {
                return new ReservationResult
                {
                    Outcome = ReservationOutcome.InsufficientStock,
                    Available = 0
                };
            }

            const int maxAttempts = 2; // initial attempt + EXACTLY ONE retry (AAP §0.1.2) — never a third.
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                now = DateTimeOffset.UtcNow;

                // available = StockAllocation - SUM(stock-holding reservations for THIS sale).
                // Reconciled to the hardened sale-scoped, Status-based model (Core/Entities/InventoryReservation.cs,
                // ReservationStatus.cs): a reservation still holds stock when it is Consumed (sold) OR Active and
                // not yet expired. Released/Expired holds have returned their stock and are excluded. Aggregating
                // by FlashSaleId keeps one sale's holds from contaminating another sale's availability.
                // SumAsync over an int column returns 0 for an empty set, so no null handling is needed.
                var reserved = await _context.InventoryReservations
                    .Where(r => r.FlashSaleId == sale.Id
                        && (r.Status == ReservationStatus.Consumed
                            || (r.Status == ReservationStatus.Active && r.ExpiresAt > now)))
                    .SumAsync(r => r.Quantity);

                var available = sale.StockAllocation - reserved;
                if (available < 0) available = 0; // clamp so availability is never reported as negative.

                // Insufficient stock -> 409 INSUFFICIENT_STOCK {available:N}; persist NOTHING (no partial
                // reservation). On a retry this fresh recompute sees the winner's committed reservation,
                // so genuinely-exhausted stock correctly resolves to InsufficientStock (NOT Conflict).
                if (quantity > available)
                {
                    return new ReservationResult
                    {
                        Outcome = ReservationOutcome.InsufficientStock,
                        Available = available
                    };
                }

                // Stage the reservation AND bump the guarded token so both commit in one transaction.
                // Reconciled to the hardened model: the hold is sale-scoped (FlashSaleId) and starts Active.
                var reservation = new InventoryReservation
                {
                    FlashSaleId = sale.Id,
                    ProductId = productId,
                    Quantity = quantity,
                    SessionId = sessionId,
                    ExpiresAt = now.AddSeconds(ReservationTtlSeconds),
                    Status = ReservationStatus.Active
                };
                _context.InventoryReservations.Add(reservation);

                // Marking FlashSale.Version as changed makes EF emit
                // "UPDATE FlashSales SET Version = @new WHERE Id = @id AND Version = @original".
                // This is what forces concurrent reservers to collide on the token.
                sale.Version += 1;

                try
                {
                    await _context.SaveChangesAsync();

                    var remaining = available - quantity;

                    // Broadcast the new live availability so connected browsers update without refreshing.
                    await _broadcaster.BroadcastInventoryUpdatedAsync(productId, remaining);

                    return new ReservationResult
                    {
                        Outcome = ReservationOutcome.Success,
                        Reservation = reservation,
                        Available = remaining
                    };
                }
                catch (DbUpdateConcurrencyException)
                {
                    // A concurrent reserver committed first: the guarded WHERE Version = @original matched
                    // 0 rows, so the whole transaction rolled back and our reservation INSERT did NOT
                    // persist. Reset state for the single permitted retry.
                    //
                    //   * Detach the un-persisted (Added) reservation so it is not re-inserted next loop.
                    //   * Reload the sale to pick up the winner's committed Version and reset its tracked
                    //     state to Unchanged.
                    //
                    // The loop then recomputes availability from fresh data. If the second SaveChanges also
                    // throws, we fall through to the Conflict result below — never attempting a third write.
                    _context.Entry(reservation).State = EntityState.Detached;
                    await _context.Entry(sale).ReloadAsync();
                }
            }

            // Both permitted attempts collided on the concurrency token -> 409 RESERVATION_CONFLICT.
            return new ReservationResult { Outcome = ReservationOutcome.Conflict };
        }

        // Explicit release backing DELETE /api/inventory/reserve/{id} (hardened contract, review finding F02).
        // Ownership-checked: the reservation is matched by BOTH Id and SessionId so a caller can only release a
        // hold it owns. Returns:
        //   - ReleaseOutcome.NotFound  when no reservation with that Id exists;
        //   - ReleaseOutcome.Forbidden when the row exists but belongs to a different session;
        //   - ReleaseOutcome.Released  after the hold is transitioned to Status = Released and availability
        //     is rebroadcast. Releasing sets Status = Released (returns stock to the pool) and NEVER deletes the
        //     row, matching the durable-lifecycle model. Freeing stock can never oversell, so no Version bump.
        public async Task<ReleaseOutcome> ReleaseAsync(int reservationId, string sessionId)
        {
            var reservation = await _context.InventoryReservations.FindAsync(reservationId);
            if (reservation == null) return ReleaseOutcome.NotFound;
            if (reservation.SessionId != sessionId) return ReleaseOutcome.Forbidden;

            reservation.Status = ReservationStatus.Released; // returns stock to the available pool (no delete)
            await _context.SaveChangesAsync();

            // Recompute and broadcast the freed-up availability for connected clients.
            await BroadcastAvailabilityAsync(reservation.ProductId);
            return ReleaseOutcome.Released;
        }

        // Checkout hook (hardened contract, review finding F03): consumes ONLY this session's ACTIVE
        // reservations that match the supplied ordered lines (by ProductId), transitioning each matched hold to
        // Status = Consumed. Consumed rows are NEVER deleted — they REMAIN subtracted from availability (the
        // units are sold), which is what preserves the zero-oversell invariant (AAP R3). Invoked by OrderService
        // AFTER a successful commit (and wrapped there in its own try/catch as a second safety layer), so it MUST
        // be fully defensive: a null/empty/unmatched set is a no-op and never throws, ensuring the existing order
        // flow is never broken. The predicate keys on (SessionId, ProductId), served by the (SessionId, ProductId)
        // index.
        public async Task ConsumeReservationsAsync(string sessionId, IEnumerable<ReservationConsumeLine> orderedLines)
        {
            if (orderedLines == null) return; // no-op; a null set must never throw.
            var productIds = orderedLines.Select(l => l.ProductId).Distinct().ToList();
            if (productIds.Count == 0) return; // no-op; an empty set must never throw.

            var reservations = await _context.InventoryReservations
                .Where(r => r.SessionId == sessionId
                    && r.Status == ReservationStatus.Active
                    && productIds.Contains(r.ProductId))
                .ToListAsync();

            if (reservations.Count == 0) return; // no matching active hold; nothing to consume.

            // Transition each matched hold to Consumed (sold). Never delete: the units stay subtracted so the
            // sale can never oversell.
            foreach (var reservation in reservations)
            {
                reservation.Status = ReservationStatus.Consumed;
            }
            await _context.SaveChangesAsync();

            // Rebroadcast the affected products' availability (unchanged in magnitude, but keeps clients in sync).
            var affectedProductIds = reservations.Select(r => r.ProductId).Distinct().ToList();
            foreach (var productId in affectedProductIds)
            {
                await BroadcastAvailabilityAsync(productId);
            }
        }

        // Shared helper: recomputes the current live availability for a product and broadcasts it.
        // available = StockAllocation - SUM(active, non-expired reservations), clamped to >= 0; when no
        // active sale exists availability is 0. Neither Release nor Consume modifies FlashSale, so this
        // helper never triggers a concurrency exception.
        private async Task BroadcastAvailabilityAsync(int productId)
        {
            var now = DateTimeOffset.UtcNow;

            var sale = await _context.FlashSales
                .FirstOrDefaultAsync(fs => fs.ProductId == productId && fs.StartAt <= now && fs.EndAt >= now);

            // Reconciled to the hardened sale-scoped, Status-based model: count only stock-holding reservations
            // for THIS sale (Consumed, or Active and not expired); Released/Expired holds are excluded.
            var available = 0;
            if (sale != null)
            {
                var reserved = await _context.InventoryReservations
                    .Where(r => r.FlashSaleId == sale.Id
                        && (r.Status == ReservationStatus.Consumed
                            || (r.Status == ReservationStatus.Active && r.ExpiresAt > now)))
                    .SumAsync(r => r.Quantity);
                available = sale.StockAllocation - reserved;
                if (available < 0) available = 0;
            }

            await _broadcaster.BroadcastInventoryUpdatedAsync(productId, available);
        }
    }
}
