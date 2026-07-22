using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services
{
    // Flash-Sale feature (review findings M12, M13, M14): the shared, ordered, persistence-authoritative
    // publication mechanism behind IInventoryBroadcastCoordinator. Registered as a SINGLETON in
    // API/Extension/ApplicationServicesExtensions.cs so its per-product locks are process-wide — which is
    // exactly right for the AAP's single-instance, no-backplane SignalR hub (§0.5.2). It depends only on the
    // (singleton) IInventoryBroadcaster and ILogger, so there is no scoped/captive dependency; the caller's
    // scoped database context is reached only through the compute delegate, never held here.
    public class InventoryBroadcastCoordinator : IInventoryBroadcastCoordinator
    {
        private readonly IInventoryBroadcaster _broadcaster;
        private readonly ILogger<InventoryBroadcastCoordinator> _logger;

        // One lock per productId. Product ids are bounded by the catalog size (unlike unbounded session ids),
        // so this dictionary cannot grow without bound and needs no eviction. SemaphoreSlim(1,1) gives an
        // async-friendly mutex we can await without blocking a thread-pool thread.
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _productLocks = new();

        // QA Issue 1 fix (duplicate FlashSaleStarted): the set of FlashSale ids for which a FlashSaleStarted
        // event has ALREADY been broadcast during this process's lifetime. It exists here — in the single shared
        // publication mechanism — precisely because a sale entering its window has TWO independent publishers
        // (FlashSaleService.ScheduleAsync for a created-already-active sale, and the expiry sweep on the tick it
        // first observes the window open). A per-caller dedup set (as before, private to the sweep) cannot see
        // the other caller's publication, so the two raced and produced two identical FlashSaleStarted events.
        // Centralising the marker here makes PublishFlashSaleStartedAsync atomically "exactly once" across BOTH
        // publishers. Value type is byte (a set; the value is unused). ConcurrentDictionary.TryAdd is the atomic
        // test-and-set that avoids any check-then-act race. Bounded by ForgetStartedAnnouncements, which the
        // sweep calls with ended-sale ids each tick, so the set never grows without bound (preserves M15).
        private readonly ConcurrentDictionary<int, byte> _startedSaleIds = new();

        public InventoryBroadcastCoordinator(
            IInventoryBroadcaster broadcaster,
            ILogger<InventoryBroadcastCoordinator> logger)
        {
            _broadcaster = broadcaster;
            _logger = logger;
        }

        // See IInventoryBroadcastCoordinator. Serialises compute+broadcast per product and never throws.
        public async Task PublishAvailabilityAsync(int productId, Func<Task<int>> computeAuthoritativeAvailabilityAsync)
        {
            if (computeAuthoritativeAvailabilityAsync == null)
            {
                throw new ArgumentNullException(nameof(computeAuthoritativeAvailabilityAsync));
            }

            var gate = _productLocks.GetOrAdd(productId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                // Re-read the authoritative availability from committed state INSIDE the lock, then send it
                // before releasing the lock. Because sends for this product are serialised and each re-reads
                // the newest committed value, the final message a client receives is always current (M13).
                var quantityAvailable = await computeAuthoritativeAvailabilityAsync();
                await _broadcaster.BroadcastInventoryUpdatedAsync(productId, quantityAvailable);
            }
            catch (Exception ex)
            {
                // M12: persistence is authoritative. A broadcast (or re-read) failure must NOT propagate to a
                // caller that has already committed durably; log it and let the periodic sweep re-broadcast and
                // client reconnect reconcile the missed event.
                _logger.LogWarning(ex,
                    "Flash-Sale: failed to publish live availability for product {ProductId}; " +
                    "the persisted state is authoritative and will be reconciled by the next sweep broadcast.",
                    productId);
            }
            finally
            {
                gate.Release();
            }
        }

        // See IInventoryBroadcastCoordinator. Announces a started sale under the SAME per-product lock as the
        // availability stream (so the two are mutually ordered) and never throws (M11/M13).
        public async Task PublishFlashSaleStartedAsync(FlashSale sale, Func<Task<int>> computeAuthoritativeAvailabilityAsync)
        {
            if (sale == null) throw new ArgumentNullException(nameof(sale));
            if (computeAuthoritativeAvailabilityAsync == null)
            {
                throw new ArgumentNullException(nameof(computeAuthoritativeAvailabilityAsync));
            }

            // QA Issue 1 fix: idempotent per sale id. The FIRST publisher to reach this point for a given sale
            // (ScheduleAsync when the sale is created already-active, or the sweep when it first sees the window
            // open) claims the marker atomically and proceeds to broadcast; any later call for the SAME sale id
            // returns immediately, so the sale entering its window emits exactly ONE FlashSaleStarted regardless
            // of which publisher fires first. TryAdd is the atomic test-and-set — no separate check-then-act
            // window exists, so two threads (request + sweep) racing here still yield exactly one broadcast.
            if (!_startedSaleIds.TryAdd(sale.Id, 0))
            {
                return;
            }

            var gate = _productLocks.GetOrAdd(sale.ProductId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                var quantityAvailable = await computeAuthoritativeAvailabilityAsync();
                await _broadcaster.BroadcastFlashSaleStartedAsync(sale, quantityAvailable);
            }
            catch (Exception ex)
            {
                // M11: the sale is already committed and authoritative; a failed announcement must not fail the
                // caller. Log and let the next sweep tick / client reconnect reconcile.
                //
                // QA Issue 1 fix: release the "already announced" claim on failure so the OTHER publisher (or the
                // next sweep tick) can retry the FlashSaleStarted broadcast. Marking is thus effectively "commit
                // the marker only on a successful broadcast": exactly-once when the send succeeds (the duplicate
                // is gone), yet still resilient — a transient send failure does not permanently suppress the
                // event, preserving the self-heal the sweep previously provided via its own dedup set.
                _startedSaleIds.TryRemove(sale.Id, out _);
                _logger.LogWarning(ex,
                    "Flash-Sale: failed to publish FlashSaleStarted for product {ProductId}; " +
                    "the scheduled sale is persisted and will be reconciled by the next sweep broadcast.",
                    sale.ProductId);
            }
            finally
            {
                gate.Release();
            }
        }

        // QA Issue 1 fix: see IInventoryBroadcastCoordinator.ForgetStartedAnnouncements. Removes the "already
        // announced started" marker for sales whose window has closed, keeping the dedup set bounded by live
        // catalog concurrency (active + recently-ended sales) — the sweep supplies the just-ended sale ids each
        // tick. Removing only ENDED ids (never intersecting with a live active snapshot) is race-free: a sale
        // created active concurrently with a sweep tick can never be evicted here and then re-announced. Null or
        // empty input is a safe no-op.
        public void ForgetStartedAnnouncements(IEnumerable<int> endedSaleIds)
        {
            if (endedSaleIds == null)
            {
                return;
            }

            foreach (var saleId in endedSaleIds)
            {
                _startedSaleIds.TryRemove(saleId, out _);
            }
        }

        // See IInventoryBroadcastCoordinator. Announces an ended sale under the per-product lock; never throws.
        public async Task PublishFlashSaleEndedAsync(int productId)
        {
            var gate = _productLocks.GetOrAdd(productId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                await _broadcaster.BroadcastFlashSaleEndedAsync(productId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Flash-Sale: failed to publish FlashSaleEnded for product {ProductId}; " +
                    "the ended state is persisted and will be reconciled by the next sweep broadcast.",
                    productId);
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
