using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services
{
    /// <summary>
    /// Concrete flash-sale service for the Real-Time Inventory &amp; Flash Sale feature.
    /// Two responsibilities:
    ///   1. <see cref="ScheduleAsync"/> — validates and schedules a time-boxed flash sale, enforcing per-product
    ///      non-overlap, and persists it.
    ///   2. <see cref="GetActiveSalesAsync"/> — returns the currently-active sales, each paired with a
    ///      freshly-computed, SALE-SCOPED live <c>QuantityAvailable</c>.
    ///
    /// Hardening applied for the code review:
    /// <list type="bullet">
    ///   <item>
    ///     <b>C04 / m09 — sale-scoped availability.</b> Availability is derived per FLASH SALE authority, not
    ///     per product: <c>StockAllocation − SUM(Quantity WHERE FlashSaleId = &lt;that sale&gt; AND (Consumed OR
    ///     (Active AND ExpiresAt &gt; now)))</c>, clamped at zero. Durable Consumed (sold) units are included so
    ///     sold stock is never returned to the pool, and one sale's history can never contaminate another.
    ///   </item>
    ///   <item>
    ///     <b>C03 / M09 — validation + non-overlap.</b> The service is a trust boundary: it validates product
    ///     existence, positive allocation/price, discount-below-base-price, and a forward UTC window, and rejects
    ///     any sale whose window overlaps an existing sale for the same product. On relational providers the
    ///     check-and-insert runs inside a Serializable transaction so two concurrent schedulers cannot both
    ///     insert overlapping rows (PostgreSQL SSI detects the conflict; SQLite serialises writers).
    ///   </item>
    ///   <item>
    ///     <b>M10 — no N+1.</b> The active-sales read aggregates reservations for ALL active sales in ONE grouped
    ///     query, then projects in memory.
    ///   </item>
    ///   <item>
    ///     <b>M11 / M13 — persistence-authoritative, ordered broadcast.</b> A "flash sale started" event is
    ///     published AFTER commit through the shared <see cref="IInventoryBroadcastCoordinator"/>, which never
    ///     throws, so a broadcast failure can never turn a durably-scheduled sale into a caller-visible failure.
    ///   </item>
    ///   <item>
    ///     The catalog <c>products.price</c> write path is never touched here; the flash-sale price lives on its
    ///     own column and is a read-time overlay only (AAP §0.5.2). <see cref="FlashSale.Version"/> is owned by
    ///     the reservation read-modify-write path and is not mutated by this service.
    ///   </item>
    /// </list>
    /// </summary>
    public class FlashSaleService : IFlashSaleService
    {
        // EF Core DbContext for the e-commerce store (scoped), mirroring the sibling services' conventions.
        private readonly StoreContext _context;

        // Shared, ordered, persistence-authoritative broadcast coordinator (never throws after commit). Replaces
        // the previous direct IInventoryBroadcaster dependency so schedule broadcasts obey the same M11/M13
        // guarantees as reserve/release/consume/sweep.
        private readonly IInventoryBroadcastCoordinator _coordinator;

        private readonly ILogger<FlashSaleService> _logger;

        public FlashSaleService(
            StoreContext context,
            IInventoryBroadcastCoordinator coordinator,
            ILogger<FlashSaleService> logger)
        {
            _context = context;
            _coordinator = coordinator;
            _logger = logger;
        }

        /// <summary>
        /// Validates and schedules a flash sale for <paramref name="productId"/> over the inclusive window
        /// <c>[startAt, endAt]</c>, enforcing per-product non-overlap. Returns a deterministic
        /// <see cref="FlashSaleScheduleResult"/> the API maps to an exact HTTP status. On success, if the sale is
        /// already active, a <c>FlashSaleStarted</c> event is published via the coordinator.
        /// </summary>
        public async Task<FlashSaleScheduleResult> ScheduleAsync(int productId, DateTimeOffset startAt,
            DateTimeOffset endAt, decimal salePrice, int stockAllocation)
        {
            // --- M09: service-boundary domain validation (independent of the DTO, so a DIRECT caller cannot
            // persist an invalid authority row). Normalise the window to UTC first so comparisons and storage are
            // consistent regardless of the caller's offset.
            var startAtUtc = startAt.ToUniversalTime();
            var endAtUtc = endAt.ToUniversalTime();

            if (startAt == default || endAt == default || endAtUtc <= startAtUtc)
            {
                return Fail(FlashSaleScheduleOutcome.InvalidWindow);
            }
            if (stockAllocation <= 0)
            {
                return Fail(FlashSaleScheduleOutcome.InvalidAllocation);
            }
            if (salePrice <= 0m)
            {
                return Fail(FlashSaleScheduleOutcome.InvalidSalePrice);
            }

            // Establish product existence rather than trusting a deferred FK failure (M09).
            var product = await _context.Products.FindAsync(productId);
            if (product == null)
            {
                return Fail(FlashSaleScheduleOutcome.ProductNotFound);
            }

            // A "discount" must actually discount: the sale price must be strictly below the base price. The base
            // products.price write path is never modified (AAP §0.5.2) — it is only READ here for this rule.
            if (salePrice >= product.Price)
            {
                return Fail(FlashSaleScheduleOutcome.SalePriceNotBelowBasePrice);
            }

            var sale = new FlashSale
            {
                ProductId = productId,
                StartAt = startAtUtc,
                EndAt = endAtUtc,
                SalePrice = salePrice,
                StockAllocation = stockAllocation
                // Version defaults; owned by the reservation path, never set here.
            };

            // --- C03: transactional, per-product non-overlap enforcement.
            var persisted = await TryPersistNonOverlappingAsync(sale);
            if (!persisted)
            {
                return Fail(FlashSaleScheduleOutcome.Overlap);
            }

            // --- M11 / M13: announce a live sale AFTER commit via the coordinator (never throws). "now" is UTC to
            // match the persisted timestamps; the window is inclusive of both bounds.
            var now = DateTimeOffset.UtcNow;
            if (sale.StartAt <= now && sale.EndAt >= now)
            {
                await _coordinator.PublishFlashSaleStartedAsync(
                    sale, () => ComputeAvailableAsync(sale.Id, sale.StockAllocation));
            }

            // --- M12: compute the AUTHORITATIVE post-commit availability by re-reading committed reservation
            // state, instead of letting the controller fabricate QuantityAvailable = StockAllocation. This
            // reflects any reserve that committed between this sale's insert and now, so the POST response is
            // consistent with what GET /api/flash-sales/active returns for the same sale a moment later.
            var quantityAvailable = await ComputeAvailableAsync(sale.Id, sale.StockAllocation);

            return new FlashSaleScheduleResult
            {
                Outcome = FlashSaleScheduleOutcome.Success,
                FlashSale = sale,
                QuantityAvailable = quantityAvailable
            };
        }

        /// <summary>
        /// Returns every flash sale whose window currently contains <see cref="DateTimeOffset.UtcNow"/> (inclusive
        /// of both bounds), each paired with a freshly-computed, SALE-SCOPED live <c>QuantityAvailable</c>.
        /// Aggregates reservations for ALL active sales in ONE grouped query to avoid N+1 round trips (M10).
        /// Review finding N1: an optional <paramref name="productId"/> narrows the result to a single product's
        /// active sale(s); a null value preserves the full active-list behaviour.
        /// </summary>
        public async Task<IReadOnlyList<ActiveFlashSale>> GetActiveSalesAsync(int? productId = null)
        {
            var now = DateTimeOffset.UtcNow;

            // Only sales whose window contains "now" (inclusive on both ends) are active. N1: when a productId is
            // supplied, narrow to that product's active sale(s) so a product page fetches only what it needs
            // rather than every active sale in the catalog.
            var query = _context.FlashSales
                .Where(fs => fs.StartAt <= now && fs.EndAt >= now);
            if (productId.HasValue)
            {
                query = query.Where(fs => fs.ProductId == productId.Value);
            }
            var activeSales = await query.ToListAsync();

            if (activeSales.Count == 0)
            {
                return new List<ActiveFlashSale>();
            }

            var saleIds = activeSales.Select(s => s.Id).ToList();

            // M10: ONE grouped aggregation keyed by FlashSaleId (C04: sale-scoped, incl. durable Consumed units),
            // instead of one SUM per sale. Released/Expired holds do not count.
            var reservedBySale = await _context.InventoryReservations
                .Where(r => saleIds.Contains(r.FlashSaleId)
                    && (r.Status == ReservationStatus.Consumed
                        || (r.Status == ReservationStatus.Active && r.ExpiresAt > now)))
                .GroupBy(r => r.FlashSaleId)
                .Select(g => new { FlashSaleId = g.Key, Reserved = g.Sum(x => x.Quantity) })
                .ToListAsync();

            var reservedMap = reservedBySale.ToDictionary(x => x.FlashSaleId, x => x.Reserved);

            return activeSales
                .Select(s =>
                {
                    var reserved = reservedMap.TryGetValue(s.Id, out var r) ? r : 0;
                    var available = s.StockAllocation - reserved;
                    return new ActiveFlashSale
                    {
                        Sale = s,
                        QuantityAvailable = available < 0 ? 0 : available
                    };
                })
                .ToList();
        }

        // --- C03: persists the sale only if it does not overlap an existing sale for the same product. On a
        // relational provider the overlap CHECK and the INSERT run in one Serializable transaction so two
        // concurrent schedulers cannot both commit overlapping rows (PostgreSQL SSI raises a serialization
        // failure for one; SQLite serialises writers). The application check gives a fast, deterministic answer
        // for the common case; a serialization/constraint failure is also mapped to "overlap". Non-relational
        // providers (the InMemory test store) run the check+insert directly. Returns true when persisted.
        private async Task<bool> TryPersistNonOverlappingAsync(FlashSale sale)
        {
            if (!_context.Database.IsRelational())
            {
                if (await OverlapsAsync(sale)) return false;
                _context.FlashSales.Add(sale);
                await _context.SaveChangesAsync();
                return true;
            }

            // Relational: check + insert atomically under Serializable isolation. No retrying execution strategy
            // is configured on the StoreContext, so a manual transaction is safe here.
            await using var tx = await _context.Database
                .BeginTransactionAsync(IsolationLevel.Serializable);

            if (await OverlapsAsync(sale))
            {
                await tx.RollbackAsync();
                return false;
            }

            _context.FlashSales.Add(sale);
            try
            {
                await _context.SaveChangesAsync();
                await tx.CommitAsync();
                return true;
            }
            catch (DbUpdateException ex) when (IsConcurrencyOrConstraintConflict(ex.InnerException as DbException))
            {
                // A concurrent scheduler inserted an overlapping row first (surfaced as a unique/exclusion
                // violation). Deterministic conflict response rather than a 500.
                await SafeRollbackAsync(tx);
                _context.Entry(sale).State = EntityState.Detached;
                return false;
            }
            catch (DbException ex) when (IsConcurrencyOrConstraintConflict(ex))
            {
                // PostgreSQL SSI serialization failure (SQLSTATE 40001) at COMMIT under Serializable isolation:
                // the competing scheduler's overlapping insert won. Treat as overlap, not an error.
                await SafeRollbackAsync(tx);
                _context.Entry(sale).State = EntityState.Detached;
                return false;
            }
        }

        // Two inclusive windows [s1, e1] and [s2, e2] overlap iff s1 <= e2 AND s2 <= e1.
        private Task<bool> OverlapsAsync(FlashSale sale) =>
            _context.FlashSales.AnyAsync(fs =>
                fs.ProductId == sale.ProductId
                && fs.StartAt <= sale.EndAt
                && sale.StartAt <= fs.EndAt);

        // 40001 = serialization_failure (SSI); 23505 = unique_violation; 23P01 = exclusion_violation. Compared via
        // the provider-agnostic DbException.SqlState (available since .NET 5), so no Npgsql-specific dependency.
        private static bool IsConcurrencyOrConstraintConflict(DbException ex) =>
            ex != null && (ex.SqlState == "40001" || ex.SqlState == "23505" || ex.SqlState == "23P01");

        private static async Task SafeRollbackAsync(IDisposable tx)
        {
            // Rollback defensively; a transaction already aborted by a serialization failure may throw on rollback.
            try
            {
                if (tx is Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction dbTx)
                {
                    await dbTx.RollbackAsync();
                }
            }
            catch
            {
                // The transaction is being disposed regardless; a rollback error here is not actionable.
            }
        }

        private static FlashSaleScheduleResult Fail(FlashSaleScheduleOutcome outcome) =>
            new FlashSaleScheduleResult { Outcome = outcome };

        /// <summary>
        /// SALE-SCOPED live availability (review findings C04, m09): <c>stockAllocation − SUM(Quantity WHERE
        /// FlashSaleId = <paramref name="flashSaleId"/> AND (Consumed OR (Active AND ExpiresAt &gt; now)))</c>,
        /// clamped at zero. Mirrors InventoryReservationService's authoritative formula so scheduling, reserving,
        /// and sweeping all agree. Used for the single-sale broadcast on schedule; the active-sales list uses the
        /// grouped query above.
        /// </summary>
        private async Task<int> ComputeAvailableAsync(int flashSaleId, int stockAllocation)
        {
            var now = DateTimeOffset.UtcNow;

            var reserved = await _context.InventoryReservations
                .Where(r => r.FlashSaleId == flashSaleId
                    && (r.Status == ReservationStatus.Consumed
                        || (r.Status == ReservationStatus.Active && r.ExpiresAt > now)))
                .SumAsync(r => r.Quantity);

            var available = stockAllocation - reserved;
            return available < 0 ? 0 : available;
        }
    }
}
