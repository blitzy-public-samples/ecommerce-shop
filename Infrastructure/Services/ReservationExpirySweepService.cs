using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services
{
    /// <summary>
    /// Real-Time Inventory &amp; Flash-Sale feature (AAP §0.2.3, §0.4.1 Group 2, requirement R4).
    ///
    /// A hosted <see cref="BackgroundService"/> that, on a fixed cadence, performs two duties:
    /// <list type="number">
    ///   <item>
    ///     <description>
    ///     <b>Reservation TTL auto-release.</b> Transitions every <i>Active</i> <see cref="InventoryReservation"/>
    ///     whose <see cref="InventoryReservation.ExpiresAt"/> has passed to <see cref="ReservationStatus.Expired"/>,
    ///     thereby returning the held stock to the available pool, and re-broadcasts the recomputed live
    ///     availability.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///     <b>Flash-sale window-boundary events.</b> Emits <c>FlashSaleStarted</c> when a sale enters its window
    ///     and <c>FlashSaleEnded</c> when the window closes. This service is the authoritative driver for sales
    ///     that transition into/out of their window <i>after</i> creation (e.g. future-scheduled sales).
    ///     </description>
    ///   </item>
    /// </list>
    ///
    /// <para><b>Hardening applied for the code review:</b></para>
    /// <list type="bullet">
    ///   <item>
    ///     <b>C09 — atomic conditional expiry.</b> Each stale hold is transitioned <c>Active → Expired</c> under
    ///     the <see cref="InventoryReservation.Status"/> optimistic-concurrency token (one row per
    ///     <c>SaveChanges</c>); a hold concurrently Consumed/Released by another writer yields a
    ///     <see cref="DbUpdateConcurrencyException"/> which is caught, detached, and skipped — the sweep NEVER
    ///     clobbers a sold/released row, so a lapsed TTL can never resurrect sold stock (zero-oversell, AAP R3).
    ///   </item>
    ///   <item>
    ///     <b>C11 — sale-scoped availability.</b> Availability is computed per FLASH SALE authority
    ///     (by <c>FlashSaleId</c>), never by <c>ProductId</c>, using the SAME formula as
    ///     <see cref="InventoryReservationService"/> and <see cref="FlashSaleService"/> so every writer agrees.
    ///   </item>
    ///   <item>
    ///     <b>M13 / M14 — ordered, reliable broadcast via reconciliation.</b> All broadcasts go through the shared
    ///     <see cref="IInventoryBroadcastCoordinator"/> (per-product ordering, authoritative re-read inside the
    ///     lock, never throws). Each tick RE-broadcasts the authoritative availability for every product with an
    ///     active sale, so a broadcast dropped by any writer is self-healed on the next tick without a durable
    ///     outbox.
    ///   </item>
    ///   <item>
    ///     <b>M15 — bounded, DB-derived dedup.</b> The "already announced" sets are pruned every tick to the
    ///     currently-active (started) and recently-ended (ended) sale ids drawn from the database, so they are
    ///     bounded by live catalog concurrency rather than growing without bound over the process lifetime.
    ///     Boundary events are idempotent on the client, so a single re-announcement after a restart is harmless.
    ///   </item>
    ///   <item>
    ///     <b>M16 — mutually-exclusive boundary.</b> For boundary-event purposes the window is treated as
    ///     half-open <c>[StartAt, EndAt)</c>: a sale is "active" while <c>StartAt &lt;= now &amp;&amp; now &lt; EndAt</c>
    ///     and "ended" once <c>EndAt &lt;= now</c>, so <c>FlashSaleStarted</c> and <c>FlashSaleEnded</c> can never
    ///     both fire for the same sale in a single tick (in particular at the exact <c>EndAt</c> instant).
    ///   </item>
    ///   <item>
    ///     <b>M17 — safe cadence &amp; bounded work.</b> The poll interval is floored at
    ///     <see cref="MinPollIntervalMs"/> ms so a misconfigured tiny value cannot spin the CPU, each tick
    ///     processes at most <see cref="BatchSize"/> rows, and the expiry predicate leads with
    ///     <c>Status</c> then <c>ExpiresAt</c> to use the <c>(Status, ExpiresAt)</c> composite index.
    ///   </item>
    ///   <item>
    ///     <b>m07 — quiet shutdown.</b> An <see cref="OperationCanceledException"/> raised because the host is
    ///     stopping is treated as a graceful exit, NOT logged as an error.
    ///   </item>
    /// </list>
    ///
    /// <para>
    /// <b>No SignalR dependency</b> (broadcasts flow only through the hub-agnostic coordinator/broadcaster
    /// abstractions, AAP §0.4.2) and <b>single-instance, in-memory, no backplane</b> (AAP §0.5.2).
    /// </para>
    ///
    /// <para>
    /// <b>Scope discipline.</b> A hosted service is a singleton, whereas <see cref="StoreContext"/> is scoped; the
    /// service injects only singletons/factories and opens a fresh DI scope <b>inside every tick</b>, never
    /// capturing a scoped dependency in a field. The (also-singleton) coordinator is injected directly.
    /// Registration via <c>AddHostedService&lt;ReservationExpirySweepService&gt;()</c> is performed by the API
    /// composition root, not here.
    /// </para>
    /// </summary>
    public class ReservationExpirySweepService : BackgroundService
    {
        // Singleton-safe dependencies only. StoreContext (scoped) is resolved per-tick from a fresh scope; the
        // coordinator is a singleton and safe to hold. No direct IInventoryBroadcaster is captured here — the
        // coordinator owns the send so ordering + never-throw + authoritative re-read are uniform across writers.
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IInventoryBroadcastCoordinator _coordinator;
        private readonly IConfiguration _config;
        private readonly ILogger<ReservationExpirySweepService> _logger;

        // M15: bounded idempotency set for the ENDED boundary event. The sweep is the SOLE publisher of
        // FlashSaleEnded, so its dedup can safely stay local here; membership is reconciled (pruned) against the
        // DB-derived recently-ended set on every tick, so it is bounded by live catalog concurrency and can never
        // grow without bound. Single-instance, no backplane (AAP §0.5.2).
        //
        // QA Issue 1 fix: the STARTED dedup set is deliberately NOT held here any more. FlashSaleStarted has TWO
        // publishers (this sweep AND FlashSaleService.ScheduleAsync for a created-already-active sale); a set
        // private to the sweep could not see ScheduleAsync's publication, so the two produced a duplicate event.
        // The "already announced started" marker now lives in the shared IInventoryBroadcastCoordinator, which
        // dedups PublishFlashSaleStartedAsync atomically across BOTH publishers. This sweep keeps it bounded by
        // calling _coordinator.ForgetStartedAnnouncements(...) with the just-ended sale ids each tick.
        private readonly HashSet<int> _endedSaleIds = new HashSet<int>();

        // M17: an upper bound on the rows touched per tick so a large backlog is drained across several ticks
        // rather than loaded/updated in one unbounded operation.
        private const int BatchSize = 500;

        // M17: cadence guard rails. The default applies when the key is absent/invalid; the floor prevents a
        // misconfigured sub-floor value (e.g. 1 ms) from turning the loop into a CPU spin.
        private const int DefaultPollIntervalMs = 5000;
        private const int MinPollIntervalMs = 250;

        public ReservationExpirySweepService(
            IServiceScopeFactory scopeFactory,
            IInventoryBroadcastCoordinator coordinator,
            IConfiguration config,
            ILogger<ReservationExpirySweepService> logger)
        {
            _scopeFactory = scopeFactory;
            _coordinator = coordinator;
            _config = config;
            _logger = logger;
        }

        /// <summary>
        /// Poll cadence in milliseconds from <c>FLASH_SALE_POLL_INTERVAL_MS</c>. Falls back to
        /// <see cref="DefaultPollIntervalMs"/> when absent/invalid, and is floored at <see cref="MinPollIntervalMs"/>
        /// so an accidental tiny value cannot busy-loop the host (review finding M17).
        /// </summary>
        private int PollIntervalMs
        {
            get
            {
                if (int.TryParse(_config["FLASH_SALE_POLL_INTERVAL_MS"], out var ms) && ms > 0)
                {
                    return Math.Max(ms, MinPollIntervalMs);
                }
                return DefaultPollIntervalMs;
            }
        }

        /// <summary>
        /// The long-running loop. A failure inside a single sweep tick is logged and swallowed so the host is
        /// never brought down by a transient error (AAP §0.4.2); shutdown-initiated cancellation exits quietly
        /// (review finding m07).
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ExecuteSweepAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // m07: the host is stopping; an in-flight EF query was cancelled. This is expected — exit
                    // quietly rather than logging a spurious error. (TaskCanceledException derives from this.)
                    break;
                }
                catch (Exception ex)
                {
                    // A transient failure must NEVER crash the host (AAP §0.4.2). Log and continue to next tick.
                    _logger.LogError(ex, "Reservation expiry sweep tick failed; continuing to next tick.");
                }

                try
                {
                    await Task.Delay(PollIntervalMs, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // m07: graceful shutdown during the inter-tick delay.
                    break;
                }
            }
        }

        /// <summary>
        /// Executes a single sweep tick. Exposed as <c>protected virtual</c> to provide a deterministic test seam
        /// (a test subclass can invoke exactly one tick without any timing dependency), mirroring the proven
        /// <c>protected virtual</c> seam used by <see cref="PaymentService"/>; there is no <c>InternalsVisibleTo</c>
        /// in this repository, so an <c>internal</c> member would be invisible to the Infrastructure test project.
        /// </summary>
        /// <param name="stoppingToken">Cancellation token flowed into every asynchronous EF Core operation.</param>
        protected virtual async Task ExecuteSweepAsync(CancellationToken stoppingToken)
        {
            // Fresh scope per tick: a singleton hosted service must not capture the scoped StoreContext as a field.
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            // Single "now" reference for the whole tick so every comparison uses one consistent instant (UTC to
            // match the persisted, UTC-normalised timestamps).
            var now = DateTimeOffset.UtcNow;

            // 1) C09/M17: atomically expire stale ACTIVE holds (indexed predicate, bounded batch). Returns the
            //    distinct products whose stock was actually released this tick.
            var releasedProductIds = await ExpireStaleReservationsAsync(context, now, stoppingToken);

            // 2) M16: sales currently INSIDE their window, treated as half-open [StartAt, EndAt) for boundary
            //    events so "started" and "ended" are mutually exclusive at the exact EndAt instant.
            var activeSales = await context.FlashSales
                .Where(fs => fs.StartAt <= now && fs.EndAt > now)
                .OrderBy(fs => fs.Id)
                .Take(BatchSize)
                .ToListAsync(stoppingToken);

            // 3) M15/M16: sales whose window has CLOSED, restricted to a RECENT lookback so this set is bounded
            //    (a sale that ended long ago is never re-scanned and never re-announced).
            var lookbackMs = Math.Max((long)PollIntervalMs * 5, 60000);
            var endedLookback = now - TimeSpan.FromMilliseconds(lookbackMs);
            var endedSales = await context.FlashSales
                .Where(fs => fs.EndAt <= now && fs.EndAt > endedLookback)
                .OrderBy(fs => fs.Id)
                .Take(BatchSize)
                .ToListAsync(stoppingToken);

            // 4) FlashSaleStarted — once per sale per active window (M13/M14 via coordinator). QA Issue 1 fix:
            //    the sweep no longer keeps its own "already announced" set; it publishes every active sale through
            //    the shared coordinator, which is now IDEMPOTENT per sale id. So a sale that FlashSaleService
            //    .ScheduleAsync already announced at creation (created-already-active) is a no-op here, and a
            //    future-scheduled sale the sweep is the FIRST to observe is announced exactly once — the two
            //    publishers can no longer produce a duplicate.
            foreach (var sale in activeSales)
            {
                var startedSale = sale; // capture for the closure
                await _coordinator.PublishFlashSaleStartedAsync(
                    startedSale,
                    () => ComputeSaleScopedAvailabilityAsync(context, startedSale.ProductId, now));
            }

            // 5) FlashSaleEnded — once per sale as its window closes (M15 bounded dedup, M13/M14 via coordinator).
            var endedIds = endedSales.Select(s => s.Id).ToHashSet();
            foreach (var sale in endedSales)
            {
                if (_endedSaleIds.Add(sale.Id))
                {
                    await _coordinator.PublishFlashSaleEndedAsync(sale.ProductId);
                }
            }
            _endedSaleIds.IntersectWith(endedIds); // M15: prune ids outside the recent-ended window -> bounded.

            // QA Issue 1 fix: keep the coordinator's shared "already announced started" set bounded. Now that a
            // sale has left its window, the coordinator can forget its started marker; a future re-use of the id
            // will not occur (ids are monotonic), and this prevents the set from growing over the process
            // lifetime. Pruning by JUST-ENDED ids (not by an active snapshot) is race-free: a sale created active
            // concurrently with this tick is never in endedSales, so it can never be forgotten-then-re-announced.
            _coordinator.ForgetStartedAnnouncements(endedIds);

            // 6) M14/C11: authoritative availability reconciliation. Re-broadcast the sale-scoped availability for
            //    every product whose holds were just released, plus every product with an active sale (periodic
            //    re-send that self-heals any dropped broadcast), plus every just-ended product (zeroes the
            //    indicator as the sale closes). The coordinator serialises and re-reads per product, so the last
            //    value a client sees is always the newest committed truth.
            var reconcileProductIds = new HashSet<int>(releasedProductIds);
            foreach (var s in activeSales) reconcileProductIds.Add(s.ProductId);
            foreach (var s in endedSales) reconcileProductIds.Add(s.ProductId);

            foreach (var productId in reconcileProductIds)
            {
                var pid = productId; // capture for the closure
                await _coordinator.PublishAvailabilityAsync(
                    pid, () => ComputeSaleScopedAvailabilityAsync(context, pid, now));
            }
        }

        /// <summary>
        /// C09/M17: transitions each stale <see cref="ReservationStatus.Active"/> hold (<c>ExpiresAt &lt;= now</c>)
        /// to <see cref="ReservationStatus.Expired"/> ONE ROW AT A TIME under the <c>Status</c> concurrency token.
        /// A row concurrently transitioned by another writer surfaces a <see cref="DbUpdateConcurrencyException"/>
        /// (the conditional UPDATE affected 0 rows); that row is detached and skipped so a sold/released hold is
        /// never overwritten. The predicate leads with <c>Status</c> then <c>ExpiresAt</c> to use the
        /// <c>(Status, ExpiresAt)</c> composite index, and the batch is capped at <see cref="BatchSize"/>.
        /// </summary>
        /// <returns>The distinct product ids whose stock was actually released this tick.</returns>
        private async Task<HashSet<int>> ExpireStaleReservationsAsync(
            StoreContext context, DateTimeOffset now, CancellationToken stoppingToken)
        {
            var expired = await context.InventoryReservations
                .Where(r => r.Status == ReservationStatus.Active && r.ExpiresAt <= now)
                .OrderBy(r => r.ExpiresAt).ThenBy(r => r.Id)
                .Take(BatchSize)
                .ToListAsync(stoppingToken);

            var releasedProductIds = new HashSet<int>();

            foreach (var reservation in expired)
            {
                // Modify exactly one tracked entity, then persist it, so each SaveChanges emits a single
                // conditional UPDATE guarded by the Status token (WHERE Id = .. AND Status = Active).
                reservation.Status = ReservationStatus.Expired;
                try
                {
                    await context.SaveChangesAsync(stoppingToken);
                    releasedProductIds.Add(reservation.ProductId);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // C09: another writer already transitioned this hold (Consumed/Released). Our conditional
                    // update matched 0 rows. Never clobber that state — detach and skip.
                    context.Entry(reservation).State = EntityState.Detached;
                }
            }

            return releasedProductIds;
        }

        /// <summary>
        /// SALE-SCOPED live availability (review finding C11): <c>StockAllocation − SUM(Quantity WHERE
        /// FlashSaleId = &lt;the product's active sale&gt; AND (Consumed OR (Active AND ExpiresAt &gt; now)))</c>,
        /// clamped at zero. The active sale is selected deterministically (latest <c>StartAt</c>, then highest
        /// <c>Id</c>) exactly as <see cref="InventoryReservationService"/> and <see cref="FlashSaleService"/> do,
        /// so every writer computes an identical number. Returns 0 when the product has no active sale.
        /// </summary>
        private static async Task<int> ComputeSaleScopedAvailabilityAsync(
            StoreContext context, int productId, DateTimeOffset now)
        {
            var sale = await context.FlashSales
                .Where(fs => fs.ProductId == productId && fs.StartAt <= now && fs.EndAt >= now)
                .OrderByDescending(fs => fs.StartAt).ThenByDescending(fs => fs.Id)
                .FirstOrDefaultAsync();

            if (sale == null)
            {
                // No active sale: nothing to advertise for this product.
                return 0;
            }

            var reserved = await context.InventoryReservations
                .Where(r => r.FlashSaleId == sale.Id
                    && (r.Status == ReservationStatus.Consumed
                        || (r.Status == ReservationStatus.Active && r.ExpiresAt > now)))
                .SumAsync(r => r.Quantity);

            var available = sale.StockAllocation - reserved;
            return available < 0 ? 0 : available;
        }
    }
}
