using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Core.Entities;

namespace Core.Interfaces
{
    // Flash-Sale feature (review findings M12, M13, M14): the SINGLE shared publication mechanism used by
    // EVERY writer that changes flash-sale availability (InventoryReservationService reserve/release/consume,
    // FlashSaleService schedule, ReservationExpirySweepService expiry). Centralising publication here is what
    // makes the ordering + persistence-authoritative guarantees uniform across all writers (M13 explicitly
    // requires "the same ordered publication mechanism across every writer").
    //
    // Guarantees (implemented in Infrastructure/Services/InventoryBroadcastCoordinator.cs):
    //   * Ordered server-side delivery per product (M13): publications for a given productId are serialised
    //     through an in-process per-product lock. Because the platform runs a SINGLE in-memory SignalR hub
    //     instance with NO backplane (AAP §0.5.2), an in-memory lock fully orders delivery for that product.
    //   * Always-authoritative value (M13): the availability is RE-READ from the committed database state
    //     INSIDE the per-product lock, immediately before the send, so the last publication a client receives
    //     always reflects the latest committed state — a stale concurrent value can never win.
    //   * Persistence-authoritative, never-throwing broadcast (M12): the send is wrapped so a transient
    //     SignalR failure is logged and swallowed rather than propagated. Callers broadcast only AFTER a
    //     successful commit, so a failed broadcast never turns a durably-persisted operation into a failure;
    //     the periodic sweep re-broadcast (ReservationExpirySweepService) and the client's automatic
    //     reconnect provide self-healing reconciliation without a durable outbox (which the single-instance,
    //     minimal-change AAP does not call for).
    public interface IInventoryBroadcastCoordinator
    {
        // Serialises, per product, the sequence "compute authoritative availability -> broadcast it".
        // The caller supplies computeAuthoritativeAvailabilityAsync, a delegate that re-reads the current
        // sale-scoped availability from its own (already-committed) database context; the coordinator invokes
        // it INSIDE the per-product lock so the broadcast reflects the newest committed value. Any exception
        // from the compute delegate or the broadcast is logged and swallowed — this method never throws, so a
        // post-commit caller's durable success is never undone by a broadcast/read failure.
        Task PublishAvailabilityAsync(int productId, Func<Task<int>> computeAuthoritativeAvailabilityAsync);

        // Flash-Sale feature (review finding M11, M13): persistence-authoritative "flash sale started"
        // publication used by FlashSaleService after a sale is COMMITTED (and by the sweep when a scheduled
        // sale crosses its StartAt). Shares the SAME per-product lock as PublishAvailabilityAsync, so the
        // started event and the availability stream for a product are ordered relative to one another, and
        // re-reads the authoritative availability inside the lock. Never throws — a post-commit broadcast
        // failure must not turn a durably-scheduled sale into a caller-visible failure (M11).
        //
        // QA Issue 1 fix (event cardinality — "FlashSaleStarted once per sale per active window"): this method
        // is IDEMPOTENT per FlashSale.Id. There are two callers for a sale that becomes active — FlashSaleService
        // .ScheduleAsync (immediately, when a sale is created already inside its window) and the expiry sweep
        // (on the first tick that observes the window open). The FIRST call for a given sale id performs the
        // broadcast; any subsequent call for the SAME sale id is a no-op. Centralising the dedup here (rather
        // than in one caller's private state) is what makes the "exactly once" guarantee hold across BOTH
        // publishers, matching this type's role as the single shared publication mechanism for every writer.
        // The dedup set is kept bounded by ForgetStartedAnnouncements (below).
        Task PublishFlashSaleStartedAsync(FlashSale sale, Func<Task<int>> computeAuthoritativeAvailabilityAsync);

        // QA Issue 1 fix: bounded-memory companion to the idempotent PublishFlashSaleStartedAsync. The expiry
        // sweep calls this each tick with the ids of sales whose window has just CLOSED so the coordinator can
        // forget their "already announced started" marker. Pruning by ENDED ids (rather than intersecting with a
        // just-queried active snapshot) is deliberately race-free: an id is only ever removed once its sale has
        // ended, so a sale created active concurrently with a sweep tick can never be evicted-then-re-announced.
        // The started-dedup set is therefore bounded by live catalog concurrency (active + recently-ended sales),
        // preserving the sweep's original M15 "bounded dedup" property. Passing null or an empty sequence is a
        // safe no-op. Single-instance, in-memory, no backplane (AAP §0.5.2).
        void ForgetStartedAnnouncements(IEnumerable<int> endedSaleIds);

        // Flash-Sale feature (review finding M14, M13): persistence-authoritative "flash sale ended"
        // publication used by the expiry sweep when a sale crosses its EndAt. Shares the per-product lock for
        // ordering and never throws.
        Task PublishFlashSaleEndedAsync(int productId);
    }
}
