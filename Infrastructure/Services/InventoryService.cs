using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
// Disambiguate IDatabase: both StackExchange.Redis and Microsoft.EntityFrameworkCore.Storage
// (imported above for IDbContextTransaction) declare an IDatabase type. In this file IDatabase
// always means the Redis client database.
using IDatabase = StackExchange.Redis.IDatabase;

namespace Infrastructure.Services
{
    // Infrastructure-layer implementation of IInventoryService and THE SOLE STOCK WRITER: the only
    // component permitted to write the Reservations table and the Redis stock keys (stock:*, reservation:*).
    //
    // Architecture: this file lives in the Infrastructure layer, which references ONLY Core. It NEVER
    // references any API-layer real-time type (the SignalR hub or its hub-context abstraction).
    // Real-time broadcasting is decoupled EXCLUSIVELY through the Redis "stock-updates" Pub/Sub channel:
    // this service PUBLISHES stock mutations, and the API-layer StockBroadcastBackgroundService subscribes
    // and performs the SignalR broadcast. That bridge preserves the API -> Infrastructure -> Core direction.
    //
    // Correctness authority: PostgreSQL is the source of truth. A SELECT ... FOR UPDATE row lock on the
    // Products row (inside an explicit EF Core transaction) serializes concurrent reservation creation and
    // order finalization so oversell is impossible under load. Redis is a hot-path accelerator only and is
    // treated as best-effort: every Redis call is wrapped fail-closed so a Redis outage never causes stock
    // to be assumed available (the row-locked DB check remains the authority).
    public class InventoryService : IInventoryService
    {
        private const string StockUpdatesChannel = "stock-updates";

        private readonly StoreContext _context;
        private readonly IDatabase _database;
        private readonly ISubscriber _subscriber;
        private readonly IFlashSaleService _flashSaleService;
        private readonly IConfiguration _config;
        private readonly ILogger<InventoryService> _logger;

        // ILogger is an OPTIONAL trailing dependency (defaults to null). The DI container injects the
        // real logger in production; leaving it optional keeps every existing unit test compiling and
        // constructing unchanged — including the TestableInventoryService subclass that calls
        // base(context, redis, flashSaleService, config) with four arguments. When null, all logging is
        // a no-op via the _logger?. null-conditional calls, so the fail-closed behavior is unchanged.
        public InventoryService(StoreContext context, IConnectionMultiplexer redis,
            IFlashSaleService flashSaleService, IConfiguration config,
            ILogger<InventoryService> logger = null)
        {
            _context = context;
            _database = redis.GetDatabase();
            _subscriber = redis.GetSubscriber();
            _flashSaleService = flashSaleService;
            _config = config;
            _logger = logger;
        }

        // Observability for the fail-closed Redis hot path. Every Redis operation in this service is
        // best-effort: on a Redis outage the operation is swallowed and PostgreSQL remains the source
        // of truth. Previously these catches were silent, so lost counter mutations / holds / publishes
        // were invisible in production. This helper emits a single structured WARNING per degraded
        // operation. It logs ONLY non-sensitive identifiers (operation name, product id, basket id — a
        // client-supplied cart UUID); it never logs connection strings, credentials, or tokens.
        private void LogRedisDegraded(Exception ex, string operation, int productId, string basketId = null)
        {
            _logger?.LogWarning(ex,
                "Redis unavailable during {Operation} (product {ProductId}, basket {BasketId}); continuing " +
                "fail-closed — PostgreSQL is authoritative and stock counters are re-seeded on the next " +
                "reconciliation pass.",
                operation, productId, basketId ?? "n/a");
        }

        // Config via the IConfiguration indexer + int.TryParse, because the typed configuration-binder
        // extension package is absent from Infrastructure's closure and the generic typed getter will not compile.
        // Clamped to a minimum of 1 minute: a misconfigured 0 or negative value must never produce a
        // reservation that is already expired at creation (ExpiresAt <= CreatedAt), which would let the
        // reconciliation service immediately reclaim every hold. A missing/unparseable key falls back to
        // the documented default of 10.
        private int ReservationTtlMinutes =>
            int.TryParse(_config["Inventory:ReservationTtlMinutes"], out var v) ? Math.Max(1, v) : 10;

        private static string StockKey(int productId) => $"stock:product:{productId}";
        private static string HoldKey(string basketId, int productId) => $"reservation:{basketId}:{productId}";

        // ---------- provider-guard seams (overridable by unit tests) ----------
        protected virtual bool SupportsRowLocking() => _context.Database.IsNpgsql();

        // Returns null on non-relational providers (EF InMemory) so callers null-check before Commit/Rollback.
        protected virtual async Task<IDbContextTransaction> BeginLockingTransactionAsync()
        {
            if (_context.Database.IsRelational())
                return await _context.Database.BeginTransactionAsync();
            return null;
        }

        // SELECT ... FOR UPDATE is PostgreSQL-only; parameterized (NEVER string-concatenate the id).
        protected virtual async Task AcquireProductRowLockAsync(int productId)
        {
            if (SupportsRowLocking())
            {
                await _context.Products
                    .FromSqlRaw("SELECT * FROM \"Products\" WHERE \"Id\" = {0} FOR UPDATE", productId)
                    .ToListAsync();
            }
        }

        // ---------- create / extend ----------
        public async Task<bool> CreateReservationAsync(string basketId, int productId, int quantity)
        {
            if (quantity <= 0) return false;

            await using var tx = await BeginLockingTransactionAsync();
            // Issue 2 (connection-pool exhaustion): track commit state so the catch never rolls back an
            // already-committed transaction after the post-commit (best-effort) Redis call below.
            var committed = false;
            try
            {
                await AcquireProductRowLockAsync(productId);

                var product = await _context.Set<Product>().FindAsync(productId);
                if (product == null)
                {
                    if (tx != null) await tx.RollbackAsync();
                    return false;
                }

                var sale = await _flashSaleService.GetActiveFlashSaleForProductAsync(productId);
                int? flashSaleId = sale?.Id;
                var capacity = sale?.SaleStockQuantity ?? product.StockQuantity;

                var reserved = await _context.Set<Reservation>()
                    .Where(r => r.Status == ReservationStatus.Active
                                && r.ProductId == productId
                                && r.FlashSaleId == flashSaleId)
                    .SumAsync(r => (int?)r.Quantity) ?? 0;

                var available = capacity - reserved;
                if (available < quantity)
                {
                    if (tx != null) await tx.RollbackAsync();
                    return false;
                }

                var now = DateTimeOffset.UtcNow;
                var expiresAt = now.AddMinutes(ReservationTtlMinutes);

                // create OR extend: top-up an existing Active hold for the same basket+product+pool
                var existing = await _context.Set<Reservation>()
                    .FirstOrDefaultAsync(r => r.Status == ReservationStatus.Active
                                              && r.ProductId == productId
                                              && r.BasketId == basketId
                                              && r.FlashSaleId == flashSaleId);
                if (existing != null)
                {
                    existing.Quantity += quantity;
                    existing.ExpiresAt = expiresAt;
                }
                else
                {
                    _context.Set<Reservation>().Add(new Reservation
                    {
                        ProductId = productId,
                        BasketId = basketId,
                        Quantity = quantity,
                        CreatedAt = now,
                        ExpiresAt = expiresAt,
                        Status = ReservationStatus.Active,
                        FlashSaleId = flashSaleId
                    });
                }

                await _context.SaveChangesAsync();

                // Issue 2 (connection-pool exhaustion) fix — COMMIT FIRST, then touch Redis. Committing
                // releases the pooled Npgsql connection AND the SELECT ... FOR UPDATE row lock BEFORE any
                // Redis round-trip. Redis is only a best-effort hot-path accelerator (never the oversell
                // authority — that is the DB row lock + Active-reservation SUM above), so it must not extend
                // the critical section that holds a scarce pooled connection across Redis latency.
                if (tx != null) await tx.CommitAsync();
                committed = true;

                // Redis best-effort (fail-closed): hold key + DECR counter + publish. Runs AFTER commit; the
                // row lock is already released, so a slow/unreachable Redis no longer pins a pooled connection.
                await TryReserveRedisAsync(basketId, productId, quantity, expiresAt - now, flashSaleId);

                return true;
            }
            catch (Exception)
            {
                // Only roll back when the transaction has NOT already been committed; a post-commit failure
                // must never attempt to roll back a committed transaction.
                if (tx != null && !committed) await tx.RollbackAsync();
                throw; // genuine DB errors propagate; insufficient stock already returned false above
            }
        }

        // Create-or-extend an Active hold for (basketId, productId) to the desired TOTAL quantity for that
        // basket line. Returns true when the requested total was granted, false when the hold could not be
        // grown to the requested total within its BOUND pool (insufficient stock). The caller (BasketController)
        // inspects this result and rejects the basket update when any line cannot be reserved, so the persisted
        // basket never claims a quantity that was not safely held.
        public async Task<bool> ExtendReservationAsync(string basketId, int productId, int quantity)
        {
            await using var tx = await BeginLockingTransactionAsync();
            // Issue 2 (connection-pool exhaustion): see CreateReservationAsync. Track commit state so the
            // catch never rolls back an already-committed transaction after the post-commit Redis call.
            var committed = false;
            try
            {
                await AcquireProductRowLockAsync(productId);

                var existing = await _context.Set<Reservation>()
                    .FirstOrDefaultAsync(r => r.Status == ReservationStatus.Active
                                              && r.ProductId == productId
                                              && r.BasketId == basketId);

                var now = DateTimeOffset.UtcNow;
                var expiresAt = now.AddMinutes(ReservationTtlMinutes);

                if (existing == null)
                {
                    // No Active hold yet for this basket line: CREATE one at the requested desired TOTAL,
                    // INSIDE the "SELECT ... FOR UPDATE" row lock already held above. This is the P8-01 fix:
                    // the previous implementation rolled the lock BACK and delegated to CreateReservationAsync
                    // (which opened its own transaction and ADDS to any existing hold — existing.Quantity +=
                    // quantity). Under a same-basket race that additive semantics treated concurrent desired
                    // totals as cumulative, inflating the hold past available stock and rejecting valid writes
                    // (multiple 409s). Doing the create-or-set here, without releasing the lock, serializes
                    // concurrent requests on the product row: each sees the previous request's committed hold
                    // and takes the delta path below, so the outcome is a coherent last-write-wins to the
                    // desired total (all-200 while stock is sufficient) rather than an additive over-reservation.
                    if (quantity <= 0)
                    {
                        // A non-positive desired total has nothing to hold. Reject WITHOUT creating a row
                        // (P4-26): the old delegation floored the quantity to 1 (`quantity > 0 ? quantity : 1`),
                        // which manufactured a phantom 1-unit hold and a spurious counter decrement.
                        if (tx != null) await tx.RollbackAsync();
                        return false;
                    }

                    var product = await _context.Set<Product>().FindAsync(productId);
                    if (product == null)
                    {
                        // Unknown product cannot be reserved: fail-closed (never assume availability).
                        if (tx != null) await tx.RollbackAsync();
                        return false;
                    }

                    // Resolve the pool active at creation (a specific flash sale, else the general product
                    // stock) and bind the new hold to it, exactly as CreateReservationAsync does.
                    var sale = await _flashSaleService.GetActiveFlashSaleForProductAsync(productId);
                    int? newFlashSaleId = sale?.Id;
                    var newCapacity = sale?.SaleStockQuantity ?? product.StockQuantity;

                    var newReserved = await _context.Set<Reservation>()
                        .Where(r => r.Status == ReservationStatus.Active
                                    && r.ProductId == productId
                                    && r.FlashSaleId == newFlashSaleId)
                        .SumAsync(r => (int?)r.Quantity) ?? 0;

                    if (newCapacity - newReserved < quantity)
                    {
                        // Insufficient stock in the bound pool: deny so the caller (BasketController) can
                        // reject the basket line. Nothing is persisted and the counter is untouched.
                        if (tx != null) await tx.RollbackAsync();
                        return false;
                    }

                    _context.Set<Reservation>().Add(new Reservation
                    {
                        ProductId = productId,
                        BasketId = basketId,
                        Quantity = quantity,
                        CreatedAt = now,
                        ExpiresAt = expiresAt,
                        Status = ReservationStatus.Active,
                        FlashSaleId = newFlashSaleId
                    });
                    await _context.SaveChangesAsync();

                    // Issue 2 fix — COMMIT FIRST, then touch Redis (releases the pooled connection + FOR
                    // UPDATE row lock before the Redis round-trip; see CreateReservationAsync).
                    if (tx != null) await tx.CommitAsync();
                    committed = true;

                    // Redis best-effort (fail-closed): set the hold key with TTL, DECR the counter by the
                    // newly-held quantity, and publish — identical to the create path's counter movement.
                    await TryReserveRedisAsync(basketId, productId, quantity, expiresAt - now, newFlashSaleId);

                    return true;
                }

                var delta = quantity - existing.Quantity; // interpret quantity as new desired total for the basket line
                if (delta > 0)
                {
                    // F3 FIX: validate the grow against the pool this reservation is BOUND to
                    // (existing.FlashSaleId), NEVER the currently-active sale. A reservation created during a
                    // flash sale stays bound to that sale's pool even after the sale window ends, and it is
                    // that same bound pool that CommitReservationAsync decrements. Resolving the currently-active
                    // sale here would (a) fall back to general product stock once the bound sale ended and
                    // (b) let the hold grow beyond the sale pool, driving SaleStockQuantity and the Redis
                    // counter negative at commit. Selecting the bound pool keeps the grow-check, the counter
                    // mutation, and the eventual commit all consistent against a single pool.
                    int capacity;
                    if (existing.FlashSaleId == null)
                    {
                        var product = await _context.Set<Product>().FindAsync(productId);
                        capacity = product?.StockQuantity ?? 0;
                    }
                    else
                    {
                        // Load the SPECIFIC bound flash sale by id (not the currently-active one). A missing
                        // pool row is treated as zero capacity => fail-closed (deny the grow).
                        var boundSale = await _context.Set<FlashSale>().FindAsync(existing.FlashSaleId.Value);
                        capacity = boundSale?.SaleStockQuantity ?? 0;
                    }

                    // Sum of Active reservations against the SAME bound pool (includes this hold's current
                    // quantity), so `available` is the remaining headroom into which `delta` must fit.
                    var reserved = await _context.Set<Reservation>()
                        .Where(r => r.Status == ReservationStatus.Active
                                    && r.ProductId == productId
                                    && r.FlashSaleId == existing.FlashSaleId)
                        .SumAsync(r => (int?)r.Quantity) ?? 0;

                    var available = capacity - reserved;
                    if (available < delta)
                    {
                        // Cannot grow to the requested total within the bound pool: keep the current quantity,
                        // renew the TTL only, and signal denial (false). The counter is NOT decremented, so it
                        // can never go negative, and the bound pool is never oversubscribed at commit.
                        existing.ExpiresAt = expiresAt;
                        await _context.SaveChangesAsync();
                        if (tx != null) await tx.CommitAsync();
                        committed = true;
                        // Issue 2: Redis TTL refresh runs AFTER commit (fail-closed); the row lock is already released.
                        await TrySetHoldTtlAsync(basketId, productId, expiresAt - now);
                        return false;
                    }
                }

                existing.ExpiresAt = expiresAt;
                // The authoritative DB hold quantity is only updated for a positive new total; a
                // non-positive quantity is a TTL "refresh only" and leaves the held quantity unchanged.
                if (quantity > 0) existing.Quantity = quantity;
                await _context.SaveChangesAsync();

                // Issue 2 fix — COMMIT FIRST, then touch Redis (releases the pooled connection + FOR UPDATE
                // row lock before the Redis round-trip; see CreateReservationAsync).
                if (tx != null) await tx.CommitAsync();
                committed = true;

                // The Redis counter tracks available stock = pool - SUM(Active reservation quantities),
                // so it MUST move by exactly the amount the authoritative DB hold moved. Because the hold
                // quantity is unchanged when quantity <= 0, availability is unchanged and the counter must
                // NOT move either; otherwise an extend-to-0 (or negative) call would spuriously INCR the
                // counter by the held amount and transiently over-report available stock (and the broadcast
                // badge) until the next reconciliation reseed. Mirror the DB change in the counter delta.
                var counterDelta = quantity > 0 ? delta : 0;
                // adjust counter by counterDelta (>0 => DECR, <0 => INCR, 0 => publish-only), refresh hold TTL,
                // and write the authoritative held quantity (existing.Quantity — already updated above when
                // quantity > 0, unchanged on a TTL-only refresh) to the hold-key value (F-REDIS-1).
                await TryAdjustReserveRedisAsync(basketId, productId, existing.Quantity, counterDelta, expiresAt - now, existing.FlashSaleId);

                return true;
            }
            catch (Exception)
            {
                // Only roll back when the transaction has NOT already been committed (see CreateReservationAsync).
                if (tx != null && !committed) await tx.RollbackAsync();
                throw;
            }
        }

        // ---------- commit (STAGES ONLY — OrderService owns the flush) ----------
        public async Task CommitReservationAsync(string basketId)
        {
            // Order the basket's reservations by ProductId so the per-product "SELECT ... FOR UPDATE" row
            // locks taken in the loop below are always acquired in a DETERMINISTIC order (P4-21). Two orders
            // that share more than one product would otherwise be able to lock those product rows in opposite
            // orders and deadlock; a stable global lock ordering (ascending ProductId) makes such a cycle
            // impossible while leaving the committed quantities and single-product behavior unchanged.
            var reservations = await _context.Set<Reservation>()
                .Where(r => r.Status == ReservationStatus.Active && r.BasketId == basketId)
                .OrderBy(r => r.ProductId)
                .ToListAsync();

            foreach (var r in reservations)
            {
                // Take the SAME PostgreSQL "SELECT ... FOR UPDATE" row lock used at reservation creation,
                // on the Products row, BEFORE reading the pool to decrement. OrderService wraps this whole
                // commit in an explicit transaction over the shared scoped StoreContext, so this lock enlists
                // in that transaction and is held until the order commits. That serializes concurrent order
                // finalizations for the same product and closes the oversell / lost-update defect where two
                // finalizations both read the pre-decrement stock and one silently overwrote the other.
                await AcquireProductRowLockAsync(r.ProductId);

                // P6-1 idempotency guard (CRITICAL): the Active -> Committed transition and the pool decrement
                // MUST both happen AFTER the row lock is held, never before it. Two concurrent finalizations of
                // the SAME basket (e.g. a double-submitted checkout) each load this reservation while it is
                // still Active, then serialize on the product row lock acquired above. Previously the status was
                // flipped to Committed BEFORE the lock and the reservation was never re-read under it, so the
                // second finalization decremented the pool a SECOND time for a hold the first had already
                // committed — driving stock to -1. Re-load the reservation under the lock and skip it when it is
                // no longer Active: the first finalization already committed it and decremented the pool, so
                // this pass must be a no-op for that line. Guarded by SupportsRowLocking() so the non-relational
                // EF InMemory provider (single-threaded unit tests) skips the reload and keeps the original
                // straight-line "set Committed then decrement" behavior unchanged.
                if (SupportsRowLocking())
                {
                    await _context.Entry(r).ReloadAsync();
                    if (r.Status != ReservationStatus.Active) continue;
                }

                r.Status = ReservationStatus.Committed;

                if (r.FlashSaleId == null)
                {
                    var product = await _context.Set<Product>().FindAsync(r.ProductId);
                    if (product != null)
                    {
                        // The Product is very likely ALREADY tracked in this scoped context (OrderService
                        // reads it through the repository while building the order items), and EF Core does
                        // NOT refresh a tracked entity from the FOR UPDATE query above — it keeps the stale
                        // in-memory snapshot. Reload it under the lock so the decrement is applied to the
                        // freshly-locked committed value. Guarded by SupportsRowLocking() so the
                        // non-relational InMemory provider (unit tests) skips the reload and is unchanged.
                        if (SupportsRowLocking()) await _context.Entry(product).ReloadAsync();
                        product.StockQuantity -= r.Quantity;
                    }
                }
                else
                {
                    var sale = await _context.Set<FlashSale>().FindAsync(r.FlashSaleId.Value);
                    if (sale != null)
                    {
                        // Same rationale as the general pool: reload the bound flash-sale row under the
                        // Products row lock so the sale-pool decrement is applied to the fresh committed
                        // value rather than a stale tracked snapshot. Concurrent commits for the product
                        // serialize on its row lock. No-op on the InMemory provider.
                        if (SupportsRowLocking()) await _context.Entry(sale).ReloadAsync();
                        sale.SaleStockQuantity -= r.Quantity;
                    }
                }

                // NOTE (F4): the Redis hold key is intentionally NOT deleted here. Deletion is deferred to
                // FinalizeCommittedHoldsAsync, which OrderService invokes ONLY AFTER a successful flush, so a
                // rolled-back order leaves the hold key intact and Redis consistent with the still-Active
                // reservation. The counter is NOT changed on commit either (the DECR happened at creation).
            }
            // NO SaveChangesAsync here: OrderService's _unitOfWork.Complete() performs the single atomic flush.
        }

        // ---------- finalize committed holds (POST-FLUSH cleanup — OrderService calls this after Complete() > 0) ----------
        public async Task FinalizeCommittedHoldsAsync(string basketId)
        {
            // Delete the Redis hold keys for the basket's Committed reservations. This runs only after the order
            // flush has succeeded, so the hold key is removed exactly when the DB reservation is durably Committed.
            // If the flush had rolled back, OrderService would not call this, and the hold key would survive to
            // stay consistent with the reverted (still-Active) reservation. Best-effort / fail-closed on Redis outage.
            var committed = await _context.Set<Reservation>()
                .Where(r => r.Status == ReservationStatus.Committed && r.BasketId == basketId)
                .ToListAsync();

            foreach (var r in committed)
            {
                await TryDeleteHoldKeyAsync(basketId, r.ProductId);
            }
        }

        // ---------- release / cancel ----------
        public async Task ReleaseReservationAsync(string basketId, int productId)
        {
            var reservations = await _context.Set<Reservation>()
                .Where(r => r.Status == ReservationStatus.Active
                            && r.BasketId == basketId
                            && r.ProductId == productId)
                .ToListAsync();

            if (reservations.Count == 0) return;

            var released = reservations.Sum(r => r.Quantity);
            var flashSaleId = reservations[0].FlashSaleId;
            foreach (var r in reservations) r.Status = ReservationStatus.Cancelled;
            await _context.SaveChangesAsync();

            try
            {
                var currentStock = await _database.StringIncrementAsync(StockKey(productId), released);
                await _database.KeyDeleteAsync(HoldKey(basketId, productId));
                await PublishStockAsync(productId, currentStock, flashSaleId);
            }
            // P6-6: RedisException base covers connection + server faults (e.g. a malformed counter); the
            // separate RedisTimeoutException catch is required because it derives from System.TimeoutException,
            // NOT RedisException. Both degrade fail-closed — PostgreSQL/reconciliation remain authoritative.
            catch (RedisException ex) { LogRedisDegraded(ex, "ReleaseReservation", productId, basketId); }
            catch (RedisTimeoutException ex) { LogRedisDegraded(ex, "ReleaseReservation", productId, basketId); }
        }

        // Release EVERY Active hold for a basket (used when the basket is deleted or fully cleared). Each
        // Active reservation for the basket transitions Active -> Cancelled; then, per product line, the Redis
        // counter is INCR'd back by the released quantity, the per-product hold key is deleted, and the
        // corrected stock is published. This guarantees a deleted/cleared basket leaves NO Active hold behind
        // and restores available stock immediately (rather than waiting for the reconciliation TTL sweep).
        public async Task ReleaseAllReservationsForBasketAsync(string basketId)
        {
            var reservations = await _context.Set<Reservation>()
                .Where(r => r.Status == ReservationStatus.Active && r.BasketId == basketId)
                .ToListAsync();

            if (reservations.Count == 0) return;

            foreach (var r in reservations) r.Status = ReservationStatus.Cancelled;
            await _context.SaveChangesAsync();

            // A basket can hold several product lines (and, in principle, distinct pools); restore each line's
            // counter independently, matching the per-product Redis key layout (stock:product:{id}).
            foreach (var group in reservations.GroupBy(r => new { r.ProductId, r.FlashSaleId }))
            {
                var productId = group.Key.ProductId;
                var released = group.Sum(r => r.Quantity);
                try
                {
                    var currentStock = await _database.StringIncrementAsync(StockKey(productId), released);
                    await _database.KeyDeleteAsync(HoldKey(basketId, productId));
                    await PublishStockAsync(productId, currentStock, group.Key.FlashSaleId);
                }
                // P6-6: RedisException base covers connection + server faults; RedisTimeoutException is caught
                // separately because it derives from System.TimeoutException, NOT RedisException. Both degrade
                // fail-closed — PostgreSQL/reconciliation remain authoritative.
                catch (RedisException ex) { LogRedisDegraded(ex, "ReleaseAllReservations", productId, basketId); }
                catch (RedisTimeoutException ex) { LogRedisDegraded(ex, "ReleaseAllReservations", productId, basketId); }
            }
        }

        // ---------- reclaim (SOLE-WRITER expiry seam — orchestrated by StockReconciliationService) ----------
        // Reclaim every Active reservation whose hold window has elapsed (ExpiresAt < now): transition it
        // Active -> Expired, return its held quantity to the Redis counter (INCR), delete its now-defunct hold
        // key, and publish the corrected stock; then persist the transitions. Because PostgreSQL has no native
        // row TTL, this method is the SOLE enforcer of reservation expiry — the reconciliation background loop
        // merely invokes it (it no longer writes the Reservations table or the Redis stock keys directly), so
        // InventoryService remains the only writer of the Reservations table and the stock:* / reservation:*
        // keys (the AAP sole-writer invariant, §0.1.2 / §0.7).
        //
        // The DB reclaim (the authority) is committed first and always proceeds; every Redis touch is
        // best-effort / fail-closed so a Redis outage during a pass never aborts the reclaim — the
        // authoritative counter reseed in SeedStockCountersAsync (invoked by the same reconciliation pass)
        // converges Redis regardless.
        public async Task ReclaimExpiredReservationsAsync()
        {
            var now = DateTimeOffset.UtcNow;

            // (a) Authoritatively reclaim Active-but-expired holds. Semantics match
            // ExpiredReservationsSpecification(now); expressed as direct LINQ so no API.Specifications
            // dependency leaks into the Infrastructure layer.
            var expired = await _context.Set<Reservation>()
                .Where(r => r.Status == ReservationStatus.Active && r.ExpiresAt < now)
                .ToListAsync();

            if (expired.Count > 0)
            {
                // Persist the Expired transitions FIRST: this is the durable record of expiry and must succeed
                // independently of Redis availability.
                foreach (var r in expired) r.Status = ReservationStatus.Expired;
                await _context.SaveChangesAsync();

                // Return the held quantity to each product's counter and publish the corrected value so
                // subscribed clients see stock come back promptly. Grouped per (product, bound pool) to match
                // the per-product Redis key layout (stock:product:{id}); best-effort / fail-closed on Redis.
                foreach (var group in expired.GroupBy(r => new { r.ProductId, r.FlashSaleId }))
                {
                    var productId = group.Key.ProductId;
                    var released = group.Sum(r => r.Quantity);
                    try
                    {
                        var currentStock = await _database.StringIncrementAsync(StockKey(productId), released);
                        currentStock = await RepairCounterIfNegativeAsync(productId, currentStock);
                        await PublishStockAsync(productId, currentStock, group.Key.FlashSaleId);
                    }
                    // RedisException base covers connection + server faults; RedisTimeoutException derives from
                    // System.TimeoutException (NOT RedisException) so it needs its own sibling catch. Both are
                    // fail-closed — the DB status transition above is already committed.
                    catch (RedisException ex) { LogRedisDegraded(ex, "ReclaimExpired", productId); }
                    catch (RedisTimeoutException ex) { LogRedisDegraded(ex, "ReclaimExpired", productId); }

                    // The reservation no longer holds stock, so its Redis hold key is defunct. Its native TTL
                    // has almost certainly already elapsed (the TTL matched ExpiresAt, now in the past), but
                    // delete it explicitly for determinism — idempotent and best-effort.
                    foreach (var r in group)
                    {
                        await TryDeleteHoldKeyAsync(r.BasketId, productId);
                    }
                }
            }

            // (b) P6-5: converge Redis by removing STALE hold keys left behind for reservations that are no
            // longer Active but whose original hold window has NOT yet elapsed (ExpiresAt > now) — so their
            // Redis TTL would otherwise keep the defunct `reservation:{basketId}:{productId}` key alive until it
            // self-expires. A hold key is normally deleted the moment its reservation leaves Active (commit ->
            // FinalizeCommittedHoldsAsync; release -> ReleaseReservationAsync), but a Redis outage at that
            // instant skips the best-effort delete and orphans the key. Reconciliation reaps those residual
            // holds here. Bounded to the live-TTL window (ExpiresAt > now); older keys have already self-expired.
            var staleHoldOwners = await _context.Set<Reservation>()
                .Where(r => r.Status != ReservationStatus.Active && r.ExpiresAt > now)
                .Select(r => new { r.BasketId, r.ProductId })
                .Distinct()
                .ToListAsync();

            foreach (var owner in staleHoldOwners)
            {
                await TryDeleteHoldKeyAsync(owner.BasketId, owner.ProductId);
            }
        }

        // ---------- reads ----------
        public async Task<int> GetAvailableStockAsync(int productId)
        {
            try
            {
                var val = await _database.StringGetAsync(StockKey(productId));
                if (val.HasValue && int.TryParse(val, out var cached))
                    return cached < 0 ? 0 : cached;
            }
            // P6-6: catch the RedisException base (covers RedisConnectionException AND RedisServerException —
            // e.g. a malformed/non-integer counter) so a server-side fault degrades fail-closed to the
            // authoritative PostgreSQL read below instead of surfacing as an unhandled 500. RedisTimeoutException
            // is caught SEPARATELY because it derives from System.TimeoutException, NOT RedisException, so the
            // base catch alone would let a timeout escape.
            catch (RedisException ex) { LogRedisDegraded(ex, "GetAvailableStock", productId); }
            catch (RedisTimeoutException ex) { LogRedisDegraded(ex, "GetAvailableStock", productId); }

            // fail-closed fallback: authoritative committed PostgreSQL state
            var (available, _) = await ComputeAvailableFromDbAsync(productId);
            return available;
        }

        // ---------- seeding / reconcile reseed ----------
        // Set-based reseed of the Redis stock counters for EVERY product using a FIXED number of queries
        // (three), replacing the previous 3N+1 pattern (a per-product FindAsync + active-sale lookup +
        // reservation SUM => 3*18+1 = 55 round-trips for the seed catalog, 3001 for 1000 products). This
        // method runs at startup AND after every reconciliation pass, so its cost grew linearly with the
        // catalog; batching bounds it to three queries regardless of catalog size. The available-stock
        // formula is IDENTICAL to ComputeAvailableFromDbAsync (the single-product path), preserving exact
        // parity: capacity is the active flash-sale pool when one is open for the product, otherwise the
        // product's own StockQuantity; reserved is the sum of Active holds bound to that same pool; and
        // available is the clamped-non-negative remainder. Grouping/joining is done in memory (no
        // server-side GROUP BY) so the logic is provider-agnostic across PostgreSQL, SQLite and the EF Core
        // InMemory provider used by unit tests. The per-product Redis write + publish and the fail-closed
        // Redis exception handling are unchanged, so counter values and the stock-updates payload shape are
        // byte-for-byte identical to the previous implementation.
        public async Task SeedStockCountersAsync()
        {
            var now = DateTimeOffset.UtcNow;

            // (1) Every product's id and general-pool stock.
            var products = await _context.Set<Product>()
                .Select(p => new { p.Id, p.StockQuantity })
                .ToListAsync();

            // (2) All currently-active flash sales — same window predicate as
            // FlashSaleService.GetActiveFlashSaleForProductAsync — reduced to at most one sale per product
            // (the lowest Id, deterministic). The domain permits at most one active sale per product at a
            // time, so this matches the single-product resolver's FirstOrDefault result.
            var activeSales = await _context.Set<FlashSale>()
                .Where(f => f.Status == FlashSaleStatus.Active && f.StartsAt <= now && now < f.EndsAt)
                .Select(f => new { f.Id, f.ProductId, f.SaleStockQuantity })
                .ToListAsync();
            var saleByProduct = activeSales
                .GroupBy(s => s.ProductId)
                .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Id).First());

            // (3) All Active reservations, summed in memory per (product, bound pool). The bound pool is the
            // reservation's FlashSaleId (null == general pool), exactly the key ComputeAvailableFromDbAsync
            // filters on.
            var activeReservations = await _context.Set<Reservation>()
                .Where(r => r.Status == ReservationStatus.Active)
                .Select(r => new { r.ProductId, r.FlashSaleId, r.Quantity })
                .ToListAsync();
            var reservedByPool = activeReservations
                .GroupBy(r => new { r.ProductId, r.FlashSaleId })
                .ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity));

            foreach (var product in products)
            {
                int? flashSaleId = null;
                var capacity = product.StockQuantity;
                if (saleByProduct.TryGetValue(product.Id, out var sale))
                {
                    flashSaleId = sale.Id;
                    capacity = sale.SaleStockQuantity;
                }

                reservedByPool.TryGetValue(new { ProductId = product.Id, FlashSaleId = flashSaleId }, out var reserved);
                var available = capacity - reserved;
                if (available < 0) available = 0;

                try
                {
                    await _database.StringSetAsync(StockKey(product.Id), available);
                    await PublishStockAsync(product.Id, available, flashSaleId);
                }
                // P6-6: RedisException base covers connection + server faults; RedisTimeoutException is caught
                // separately (it derives from System.TimeoutException, NOT RedisException). Both fail-closed —
                // a counter that cannot be seeded now is re-converged on the next reconciliation pass.
                catch (RedisException ex) { LogRedisDegraded(ex, "SeedStockCounters", product.Id); }
                catch (RedisTimeoutException ex) { LogRedisDegraded(ex, "SeedStockCounters", product.Id); }
            }
        }

        // ---------- private helpers ----------
        private async Task<(int available, int? flashSaleId)> ComputeAvailableFromDbAsync(int productId)
        {
            var product = await _context.Set<Product>().FindAsync(productId);
            if (product == null) return (0, null);

            var sale = await _flashSaleService.GetActiveFlashSaleForProductAsync(productId);
            int? flashSaleId = sale?.Id;
            var capacity = sale?.SaleStockQuantity ?? product.StockQuantity;

            var reserved = await _context.Set<Reservation>()
                .Where(r => r.Status == ReservationStatus.Active
                            && r.ProductId == productId
                            && r.FlashSaleId == flashSaleId)
                .SumAsync(r => (int?)r.Quantity) ?? 0;

            var available = capacity - reserved;
            return (available < 0 ? 0 : available, flashSaleId);
        }

        /// <summary>
        /// Redis cache-miss self-heal for the hot-path stock counter. An atomic <c>DECR</c>/<c>INCR</c>
        /// against an ABSENT <c>stock:product:{id}</c> key (never seeded, evicted under memory pressure,
        /// or lost to a <c>FLUSHDB</c>) is materialized by Redis as 0 and then driven below zero, so the
        /// counter — and every low-stock badge derived from it — reports a spurious negative available
        /// stock. When the value observed after a mutation is negative, reconverge the counter to the
        /// authoritative committed value computed from PostgreSQL (pool capacity minus the sum of Active
        /// reservations, already inclusive of the reservation just persisted in the surrounding
        /// row-locked transaction) via an idempotent <c>SET</c>, and return that repaired value so the
        /// caller publishes truth rather than the transient negative. A non-negative observation is
        /// returned unchanged, leaving the normal DECR/INCR/GET hot path completely untouched.
        ///
        /// This NEVER widens the oversell surface: the PostgreSQL <c>SELECT ... FOR UPDATE</c> row lock
        /// remains the sole authority on whether a reservation is granted (that decision has already been
        /// made and committed before this runs); this only repairs the read-only mirror clients observe.
        /// It executes inside the caller's Redis try/catch, so a Redis outage during the repair itself is
        /// swallowed under the same fail-closed contract as the mutation it follows.
        /// </summary>
        private async Task<long> RepairCounterIfNegativeAsync(int productId, long observedStock)
        {
            if (observedStock >= 0) return observedStock;

            var (available, _) = await ComputeAvailableFromDbAsync(productId);
            await _database.StringSetAsync(StockKey(productId), available);
            return available;
        }

        /// <summary>
        /// P6-6 server-fault self-heal for the hot-path stock counter. Distinct from
        /// <see cref="RepairCounterIfNegativeAsync"/> (which repairs a NEGATIVE value observed AFTER a
        /// successful mutation): this repairs a counter that is CORRUPT — e.g. it holds a non-integer value, so
        /// the atomic DECR/INCR itself raises <see cref="RedisServerException"/> and never returns a value to
        /// inspect. Overwrite the key with the authoritative committed value computed from PostgreSQL (pool
        /// capacity minus the sum of Active reservations, already inclusive of the reservation just persisted in
        /// the surrounding row-locked transaction) via an idempotent SET, healing the malformed counter in
        /// place, and publish the corrected value so subscribed clients converge immediately.
        ///
        /// Invoked ONLY from the RedisException branch of the counter-mutating Redis helpers, where Redis is
        /// reachable (the fault was server-side, not a connectivity outage), so the SET is expected to succeed.
        /// It is nonetheless wrapped fail-closed: a further Redis fault during the repair is swallowed under the
        /// same contract as the mutation it follows — PostgreSQL and its SELECT ... FOR UPDATE row lock remain
        /// the SOLE authority on whether stock is available, so a still-broken mirror never widens oversell.
        /// </summary>
        private async Task TryReseedCounterFromDbAsync(int productId, int? flashSaleId)
        {
            try
            {
                var (available, _) = await ComputeAvailableFromDbAsync(productId);
                await _database.StringSetAsync(StockKey(productId), available);
                await PublishStockAsync(productId, available, flashSaleId);
            }
            catch (RedisException) { /* still faulting — PostgreSQL + the row lock remain authoritative */ }
            // RedisTimeoutException derives from System.TimeoutException (not RedisException); swallow it too so
            // a timeout during the self-heal never escapes the enclosing catch handler that invoked this method.
            catch (RedisTimeoutException) { /* still faulting — PostgreSQL + the row lock remain authoritative */ }
        }

        private async Task TryReserveRedisAsync(string basketId, int productId, int quantity, TimeSpan ttl, int? flashSaleId)
        {
            try
            {
                await _database.StringSetAsync(HoldKey(basketId, productId), quantity, ttl > TimeSpan.Zero ? ttl : (TimeSpan?)null);
                var currentStock = await _database.StringDecrementAsync(StockKey(productId), quantity);
                currentStock = await RepairCounterIfNegativeAsync(productId, currentStock);
                await PublishStockAsync(productId, currentStock, flashSaleId);
            }
            // A pure connectivity/timeout outage: log only. Reseeding is futile because the very next Redis
            // write would fault again; the reconciliation reseed converges the counter once Redis recovers.
            catch (RedisConnectionException ex) { LogRedisDegraded(ex, "ReserveRedis", productId, basketId); }
            catch (RedisTimeoutException ex) { LogRedisDegraded(ex, "ReserveRedis", productId, basketId); }
            // P6-6 (primary root cause): a SERVER-side Redis fault — most importantly a non-integer
            // `stock:product:{id}` counter, which makes StringDecrementAsync raise RedisServerException —
            // previously escaped the two catches above and surfaced as an HTTP 500 AFTER the reservation was
            // already durably committed to PostgreSQL (CreateReservationAsync commits the DB transaction before
            // this best-effort Redis mirror step). Catch the RedisException base so any server-side fault is
            // fail-closed, then self-heal the malformed counter by reseeding it from the authoritative
            // committed PostgreSQL value (Redis is reachable in this branch, so the SET succeeds and the
            // counter stops reporting garbage). The reservation stands regardless — the row lock is authority.
            catch (RedisException ex)
            {
                LogRedisDegraded(ex, "ReserveRedis", productId, basketId);
                await TryReseedCounterFromDbAsync(productId, flashSaleId);
            }
        }

        private async Task TryAdjustReserveRedisAsync(string basketId, int productId, int heldQuantity, int delta, TimeSpan ttl, int? flashSaleId)
        {
            try
            {
                // F-REDIS-1: write the CURRENT held quantity to the hold-key value (NOT a literal 0) so an
                // operator inspecting `reservation:{basketId}:{productId}` sees the true held amount, matching
                // the create path (TryReserveRedisAsync writes the quantity). When.Exists (XX) is preserved so
                // an already-expired/absent hold key is NOT recreated here — reservation expiry authority stays
                // exclusively with PostgreSQL + the reconciliation service (the value is never read for
                // correctness; this fixes its observability fidelity only).
                await _database.StringSetAsync(HoldKey(basketId, productId), heldQuantity, ttl > TimeSpan.Zero ? ttl : (TimeSpan?)null, when: When.Exists);
                long currentStock;
                if (delta > 0) currentStock = await _database.StringDecrementAsync(StockKey(productId), delta);
                else if (delta < 0) currentStock = await _database.StringIncrementAsync(StockKey(productId), -delta);
                else currentStock = (long)await _database.StringGetAsync(StockKey(productId));
                currentStock = await RepairCounterIfNegativeAsync(productId, currentStock);
                await PublishStockAsync(productId, currentStock, flashSaleId);
            }
            // Pure connectivity/timeout outage: log only (reseeding would fault again).
            catch (RedisConnectionException ex) { LogRedisDegraded(ex, "AdjustReserveRedis", productId, basketId); }
            catch (RedisTimeoutException ex) { LogRedisDegraded(ex, "AdjustReserveRedis", productId, basketId); }
            // P6-6: a server-side Redis fault (e.g. a non-integer counter making DECR/INCR raise
            // RedisServerException) is fail-closed here and self-heals the malformed counter from committed
            // PostgreSQL truth, exactly as on the create path.
            catch (RedisException ex)
            {
                LogRedisDegraded(ex, "AdjustReserveRedis", productId, basketId);
                await TryReseedCounterFromDbAsync(productId, flashSaleId);
            }
        }

        private async Task TrySetHoldTtlAsync(string basketId, int productId, TimeSpan ttl)
        {
            try { if (ttl > TimeSpan.Zero) await _database.KeyExpireAsync(HoldKey(basketId, productId), ttl); }
            // P6-6: RedisException base covers connection + server faults; RedisTimeoutException is caught
            // separately (it derives from System.TimeoutException, NOT RedisException). Both fail-closed — the
            // hold key's absence only forgoes an optimization; reconciliation remains the expiry authority.
            catch (RedisException ex) { LogRedisDegraded(ex, "SetHoldTtl", productId, basketId); }
            catch (RedisTimeoutException ex) { LogRedisDegraded(ex, "SetHoldTtl", productId, basketId); }
        }

        private async Task TryDeleteHoldKeyAsync(string basketId, int productId)
        {
            try { await _database.KeyDeleteAsync(HoldKey(basketId, productId)); }
            // P6-6: RedisException base covers connection + server faults; RedisTimeoutException is caught
            // separately (it derives from System.TimeoutException, NOT RedisException). Both fail-closed — a
            // stale hold key is harmless and is swept on the next reconciliation pass.
            catch (RedisException ex) { LogRedisDegraded(ex, "DeleteHoldKey", productId, basketId); }
            catch (RedisTimeoutException ex) { LogRedisDegraded(ex, "DeleteHoldKey", productId, basketId); }
        }

        private async Task PublishStockAsync(int productId, long currentStock, int? flashSaleId)
        {
            try
            {
                var payload = JsonSerializer.Serialize(
                    new { productId, currentStock, flashSaleId },
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                await _subscriber.PublishAsync(StockUpdatesChannel, payload);
            }
            // P6-6: RedisException base covers connection + server faults; RedisTimeoutException is caught
            // separately (it derives from System.TimeoutException, NOT RedisException). Both fail-closed — a
            // dropped publish only delays a UI badge until the next mutation or reconciliation republish.
            catch (RedisException ex) { LogRedisDegraded(ex, "PublishStock", productId); }
            catch (RedisTimeoutException ex) { LogRedisDegraded(ex, "PublishStock", productId); }
        }
    }
}
