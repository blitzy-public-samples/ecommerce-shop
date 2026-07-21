using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services
{
    /// <summary>
    /// Concrete flash-sale service for the Real-Time Inventory &amp; Flash Sale feature.
    /// Two responsibilities:
    ///   1. <see cref="ScheduleAsync"/> — creates/schedules a time-boxed flash sale and persists it.
    ///   2. <see cref="GetActiveSalesAsync"/> — returns the currently-active sales, each paired with a
    ///      freshly-computed live <c>QuantityAvailable</c>.
    ///
    /// Design notes (per AAP §0.4.2):
    /// <list type="bullet">
    ///   <item>
    ///     Availability is always <b>derived</b>, never stored: it is
    ///     <c>StockAllocation − SUM(active non-expired reservations for the product)</c>, clamped at zero.
    ///     There is no stock column on <c>Product</c>, so this projection is the single source of truth.
    ///   </item>
    ///   <item>
    ///     Real-time notifications go <b>only</b> through the <see cref="IInventoryBroadcaster"/> abstraction.
    ///     The service therefore carries <b>no</b> direct SignalR (or <c>API.Hubs</c>) dependency, keeping it
    ///     fully unit-testable with a mocked broadcaster and an EF in-memory <see cref="StoreContext"/>.
    ///   </item>
    ///   <item>
    ///     The catalog <c>products.price</c> write path is never touched here; the flash-sale price lives on
    ///     its own column and is applied elsewhere purely as a read-time overlay (AAP §0.5.2).
    ///   </item>
    ///   <item>
    ///     The <see cref="FlashSale.Version"/> optimistic-concurrency token is <b>not</b> mutated by this
    ///     service — it is owned by the reservation read-modify-write path that guards against oversell.
    ///   </item>
    /// </list>
    /// Registered as a scoped service by the API layer's <c>ApplicationServicesExtensions</c>; this file
    /// performs no DI wiring of its own.
    /// </summary>
    public class FlashSaleService : IFlashSaleService
    {
        // EF Core DbContext for the e-commerce store. Injected (scoped) so every request/operation gets a
        // fresh unit of work, mirroring the repository conventions of the sibling services.
        private readonly StoreContext _context;

        // Hub-agnostic broadcast seam. Concrete implementation (over IHubContext<InventoryHub>) lives in the
        // API layer; depending on the interface keeps this service free of any SignalR reference.
        private readonly IInventoryBroadcaster _broadcaster;

        /// <summary>
        /// Creates a new <see cref="FlashSaleService"/>.
        /// </summary>
        /// <param name="context">The store <see cref="StoreContext"/> used to persist and query flash sales.</param>
        /// <param name="broadcaster">The inventory broadcaster used to emit real-time flash-sale events.</param>
        public FlashSaleService(StoreContext context, IInventoryBroadcaster broadcaster)
        {
            _context = context;
            _broadcaster = broadcaster;
        }

        /// <summary>
        /// Creates and persists a flash sale for the given product over the inclusive window
        /// <c>[startAt, endAt]</c>. If the sale is already active at creation time (i.e.
        /// <see cref="DateTimeOffset.UtcNow"/> falls inside the window), a <c>FlashSaleStarted</c> event is
        /// broadcast immediately with the current live availability so connected clients light up without
        /// waiting for the next poll.
        /// </summary>
        /// <param name="productId">The product the sale applies to.</param>
        /// <param name="startAt">Inclusive start of the sale window.</param>
        /// <param name="endAt">Inclusive end of the sale window.</param>
        /// <param name="salePrice">The discounted price offered during the window (never overwrites the base product price).</param>
        /// <param name="stockAllocation">Number of units reserved for the flash sale — the ceiling for availability.</param>
        /// <returns>The persisted <see cref="FlashSale"/>, with its generated <c>Id</c> (and default <c>Version</c>) populated.</returns>
        public async Task<FlashSale> ScheduleAsync(int productId, DateTimeOffset startAt, DateTimeOffset endAt,
            decimal salePrice, int stockAllocation)
        {
            // Build the aggregate from the primitive arguments. Id and Version are assigned by the store on save.
            var sale = new FlashSale
            {
                ProductId = productId,
                StartAt = startAt,
                EndAt = endAt,
                SalePrice = salePrice,
                StockAllocation = stockAllocation
            };

            // Persist. After SaveChangesAsync the identity Id is materialized and the concurrency Version defaulted.
            _context.FlashSales.Add(sale);
            await _context.SaveChangesAsync();

            // If the sale is live the moment it is created, announce it right away. The window is inclusive of
            // BOTH bounds, and "now" is evaluated in UTC to match the persisted DateTimeOffset semantics.
            var now = DateTimeOffset.UtcNow;
            if (sale.StartAt <= now && sale.EndAt >= now)
            {
                var available = await ComputeAvailableAsync(sale.ProductId, sale.StockAllocation);
                await _broadcaster.BroadcastFlashSaleStartedAsync(sale, available);
            }

            return sale;
        }

        /// <summary>
        /// Returns every flash sale whose window currently contains <see cref="DateTimeOffset.UtcNow"/>
        /// (inclusive of both bounds), each paired with a freshly-computed live <c>QuantityAvailable</c>.
        /// This feeds the deliberately non-cached <c>GET /api/flash-sales/active</c> endpoint, so the numbers
        /// reflect stock depletion in real time rather than a stale response-cache snapshot.
        /// </summary>
        /// <returns>An <see cref="IReadOnlyList{T}"/> of <see cref="ActiveFlashSale"/> (empty when no sale is active).</returns>
        public async Task<IReadOnlyList<ActiveFlashSale>> GetActiveSalesAsync()
        {
            var now = DateTimeOffset.UtcNow;

            // Only sales whose window contains "now" (inclusive on both ends) are considered active.
            var activeSales = await _context.FlashSales
                .Where(fs => fs.StartAt <= now && fs.EndAt >= now)
                .ToListAsync();

            // Compute live availability per active sale. Active sales are few (typically one per product),
            // so awaiting inside the loop is acceptable; each aggregation runs against the same source of truth.
            var result = new List<ActiveFlashSale>(activeSales.Count);
            foreach (var sale in activeSales)
            {
                var available = await ComputeAvailableAsync(sale.ProductId, sale.StockAllocation);
                result.Add(new ActiveFlashSale
                {
                    Sale = sale,
                    QuantityAvailable = available
                });
            }

            // List<ActiveFlashSale> satisfies the IReadOnlyList<ActiveFlashSale> contract.
            return result;
        }

        /// <summary>
        /// Computes the live available quantity for a product's flash sale as
        /// <c>stockAllocation − SUM(active non-expired reservations for the product)</c>, clamped so it never
        /// goes negative. This is the single source of truth for availability shared by scheduling and the
        /// active-sales query.
        /// </summary>
        /// <param name="productId">The product whose reservations reduce availability.</param>
        /// <param name="stockAllocation">The flash sale's allocated ceiling.</param>
        /// <returns>The non-negative number of units still available.</returns>
        private async Task<int> ComputeAvailableAsync(int productId, int stockAllocation)
        {
            var now = DateTimeOffset.UtcNow;

            // Reconciled to the hardened Status-based model (Core/Entities/InventoryReservation.cs,
            // ReservationStatus.cs): a reservation still holds stock when it is Consumed (sold) OR Active and not
            // yet expired (ExpiresAt > now). Released/Expired holds have returned their stock and must not count.
            // SumAsync over an int selector returns 0 for an empty set. Aggregation is per-product, assuming a
            // single active sale per product at any moment (AAP key insight).
            var reserved = await _context.InventoryReservations
                .Where(r => r.ProductId == productId
                    && (r.Status == ReservationStatus.Consumed
                        || (r.Status == ReservationStatus.Active && r.ExpiresAt > now)))
                .SumAsync(r => r.Quantity);

            var available = stockAllocation - reserved;

            // Clamp: availability is a display/UX quantity and must never be reported as negative even if,
            // transiently, reservations were to exceed the allocation.
            return available < 0 ? 0 : available;
        }
    }
}
