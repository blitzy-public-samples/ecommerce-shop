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
    ///     <b>Reservation TTL auto-release.</b> Removes every <see cref="InventoryReservation"/> whose
    ///     <see cref="InventoryReservation.ExpiresAt"/> has passed, thereby returning the held stock to the
    ///     available pool, and re-broadcasts the recomputed live availability via
    ///     <see cref="IInventoryBroadcaster.BroadcastInventoryUpdatedAsync"/>.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///     <b>Flash-sale window-boundary events.</b> Emits <c>FlashSaleStarted</c> once when a sale enters its
    ///     <c>[StartAt, EndAt]</c> window and <c>FlashSaleEnded</c> once when the window closes. This service is
    ///     the authoritative driver for sales that transition into/out of their window <i>after</i> creation
    ///     (e.g. future-scheduled sales).
    ///     </description>
    ///   </item>
    /// </list>
    ///
    /// <para>
    /// <b>No SignalR dependency.</b> Broadcasting is performed exclusively through the hub-agnostic
    /// <see cref="IInventoryBroadcaster"/> abstraction (AAP §0.4.2), which keeps this Infrastructure-layer
    /// service free of any <c>API.Hubs</c>/SignalR reference and fully unit-testable.
    /// </para>
    ///
    /// <para>
    /// <b>Single-instance, in-memory, no backplane</b> (AAP §0.5.2). The two <see cref="HashSet{T}"/> de-duplication
    /// sets guarantee each window-boundary event fires at most once on this instance; horizontal scaling and a
    /// distributed backplane are explicitly out of scope.
    /// </para>
    ///
    /// <para>
    /// <b>Scope discipline.</b> A hosted service is resolved as a <i>singleton</i>, whereas
    /// <see cref="StoreContext"/> and the broadcaster are <i>scoped</i>. This class therefore injects only
    /// singletons/factories and opens a fresh DI scope <b>inside every tick</b> (see <see cref="ExecuteSweepAsync"/>),
    /// never capturing a scoped dependency in a field. Registration via
    /// <c>AddHostedService&lt;ReservationExpirySweepService&gt;()</c> is performed by the API composition root, not here.
    /// </para>
    /// </summary>
    public class ReservationExpirySweepService : BackgroundService
    {
        // Singleton-safe dependencies only. Scoped dependencies (StoreContext, IInventoryBroadcaster) are
        // resolved per-tick from a freshly created scope — see ExecuteSweepAsync — never held as fields.
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<ReservationExpirySweepService> _logger;

        // In-memory idempotency sets (single-instance, no backplane — AAP §0.5.2). They remember which sales
        // have already had their FlashSaleStarted / FlashSaleEnded event announced so each fires exactly once
        // for the lifetime of this process. HashSet<int>.Add returns false when the id is already present.
        private readonly HashSet<int> _startedSaleIds = new HashSet<int>();
        private readonly HashSet<int> _endedSaleIds = new HashSet<int>();

        /// <summary>
        /// Creates the sweep service. Only singleton/factory dependencies are injected; scoped services are
        /// resolved per tick from <paramref name="scopeFactory"/>.
        /// </summary>
        /// <param name="scopeFactory">Factory used to open a fresh DI scope on every sweep tick.</param>
        /// <param name="config">Application configuration; supplies <c>FLASH_SALE_POLL_INTERVAL_MS</c>.</param>
        /// <param name="logger">Logger used to record (and swallow) transient per-tick failures.</param>
        public ReservationExpirySweepService(
            IServiceScopeFactory scopeFactory,
            IConfiguration config,
            ILogger<ReservationExpirySweepService> logger)
        {
            _scopeFactory = scopeFactory;
            _config = config;
            _logger = logger;
        }

        /// <summary>
        /// Poll cadence in milliseconds, read from the <c>FLASH_SALE_POLL_INTERVAL_MS</c> configuration key
        /// (environment-overridable). Falls back to a sane default of 5000 ms whenever the key is absent,
        /// non-numeric, or non-positive.
        /// </summary>
        private int PollIntervalMs =>
            int.TryParse(_config["FLASH_SALE_POLL_INTERVAL_MS"], out var ms) && ms > 0 ? ms : 5000;

        /// <summary>
        /// The long-running loop. It is deliberately resilient: a failure inside a single sweep tick is logged
        /// and swallowed so the host is never brought down by a transient error (AAP §0.4.2), and the delay
        /// between ticks honors <paramref name="stoppingToken"/> for a graceful shutdown.
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ExecuteSweepAsync(stoppingToken);
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
                catch (TaskCanceledException)
                {
                    // The host is shutting down; exit the loop cleanly.
                    break;
                }
            }
        }

        /// <summary>
        /// Executes a single sweep tick. Exposed as <c>protected virtual</c> to provide a deterministic test
        /// seam: a test subclass can invoke exactly one tick (or override the behavior) without any timing
        /// dependency. This mirrors the proven <c>protected virtual</c> seam used by
        /// <see cref="PaymentService"/> — there is no <c>InternalsVisibleTo</c> in this repository, so an
        /// <c>internal</c> member would be invisible to the Infrastructure test project.
        ///
        /// <para>The tick opens a fresh DI scope, resolves the scoped <see cref="StoreContext"/> and
        /// <see cref="IInventoryBroadcaster"/> from it, then:</para>
        /// <list type="number">
        ///   <item><description>expires reservations with <c>ExpiresAt &lt;= now</c>, releasing their stock and re-broadcasting availability;</description></item>
        ///   <item><description>announces <c>FlashSaleStarted</c> once for sales that have entered their window;</description></item>
        ///   <item><description>announces <c>FlashSaleEnded</c> once for sales whose window has closed.</description></item>
        /// </list>
        /// </summary>
        /// <param name="stoppingToken">Cancellation token flowed into every asynchronous EF Core operation.</param>
        protected virtual async Task ExecuteSweepAsync(CancellationToken stoppingToken)
        {
            // Fresh scope per tick: a singleton hosted service must not capture scoped services as fields.
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();
            var broadcaster = scope.ServiceProvider.GetRequiredService<IInventoryBroadcaster>();

            // Single "now" reference for the whole tick so every comparison uses a consistent instant.
            var now = DateTimeOffset.UtcNow;

            // 1) Expire stale ACTIVE reservations (ExpiresAt <= now). Reconciled to the hardened Status-based
            //    model: transition each to Status = Expired (NEVER delete) so its held stock returns to the
            //    available pool. Consumed (sold) and already-Released rows are deliberately left untouched so a
            //    lapsed ExpiresAt can never erase a sale — preserving the zero-oversell invariant (AAP R3).
            var expired = await context.InventoryReservations
                .Where(r => r.Status == ReservationStatus.Active && r.ExpiresAt <= now)
                .ToListAsync(stoppingToken);

            if (expired.Count > 0)
            {
                // Capture the distinct products affected BEFORE the update so availability can be recomputed after.
                var affectedProductIds = expired.Select(r => r.ProductId).Distinct().ToList();

                foreach (var reservation in expired)
                {
                    reservation.Status = ReservationStatus.Expired;
                }
                await context.SaveChangesAsync(stoppingToken);

                // Re-broadcast the freshly recomputed availability for every product whose holds were released.
                foreach (var productId in affectedProductIds)
                {
                    var available = await ComputeAvailableAsync(context, productId, now);
                    await broadcaster.BroadcastInventoryUpdatedAsync(productId, available);
                }
            }

            // 2) FlashSaleStarted for sales that have ENTERED their window (idempotent on this single instance).
            var activeSales = await context.FlashSales
                .Where(fs => fs.StartAt <= now && fs.EndAt >= now)
                .ToListAsync(stoppingToken);

            foreach (var sale in activeSales)
            {
                // Add returns false if this sale's start was already announced — keeps the event firing once.
                if (_startedSaleIds.Add(sale.Id))
                {
                    var available = await ComputeAvailableAsync(context, sale.ProductId, now);
                    await broadcaster.BroadcastFlashSaleStartedAsync(sale, available);
                }
            }

            // 3) FlashSaleEnded for sales whose window has CLOSED (idempotent, fires once per sale).
            var endedSales = await context.FlashSales
                .Where(fs => fs.EndAt <= now)
                .ToListAsync(stoppingToken);

            foreach (var sale in endedSales)
            {
                if (_endedSaleIds.Add(sale.Id))
                {
                    await broadcaster.BroadcastFlashSaleEndedAsync(sale.ProductId);
                }
            }
        }

        /// <summary>
        /// Computes the live available quantity for a product as
        /// <c>StockAllocation − SUM(active non-expired reservations)</c>, where an <i>active</i> reservation is
        /// one whose <see cref="InventoryReservation.ExpiresAt"/> is still in the future relative to
        /// <paramref name="now"/>. When there is no active flash sale for the product the availability is 0.
        /// The result is clamped to a minimum of 0 so a transient over-reservation can never surface as a
        /// negative quantity to clients.
        /// </summary>
        /// <param name="context">The scoped store context for this tick.</param>
        /// <param name="productId">The product whose availability is being recomputed.</param>
        /// <param name="now">The consistent tick instant used for all window/expiry comparisons.</param>
        /// <returns>The non-negative available quantity.</returns>
        private static async Task<int> ComputeAvailableAsync(StoreContext context, int productId, DateTimeOffset now)
        {
            var sale = await context.FlashSales
                .FirstOrDefaultAsync(fs => fs.ProductId == productId && fs.StartAt <= now && fs.EndAt >= now);

            // Reconciled to the hardened Status-based model: a reservation still holds stock when it is Consumed
            // (sold) OR Active and not yet expired (ExpiresAt > now). Released/Expired holds have returned stock.
            var reserved = await context.InventoryReservations
                .Where(r => r.ProductId == productId
                    && (r.Status == ReservationStatus.Consumed
                        || (r.Status == ReservationStatus.Active && r.ExpiresAt > now)))
                .SumAsync(r => r.Quantity);

            var available = sale != null ? sale.StockAllocation - reserved : 0;
            return available < 0 ? 0 : available;
        }
    }
}
