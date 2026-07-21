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
        private readonly IConnectionMultiplexer _redis;
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
            _redis = redis;
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

                // Redis best-effort (fail-closed): hold key + DECR counter + publish
                await TryReserveRedisAsync(basketId, productId, quantity, expiresAt - now, flashSaleId);

                if (tx != null) await tx.CommitAsync();
                return true;
            }
            catch (Exception)
            {
                if (tx != null) await tx.RollbackAsync();
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
                    // nothing to extend -> release lock, delegate to create (which opens its own tx).
                    // Propagate create's grant/deny result so the caller can reject on insufficient stock.
                    if (tx != null) await tx.RollbackAsync();
                    return await CreateReservationAsync(basketId, productId, quantity > 0 ? quantity : 1);
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
                        await TrySetHoldTtlAsync(basketId, productId, expiresAt - now);
                        return false;
                    }
                }

                existing.ExpiresAt = expiresAt;
                // The authoritative DB hold quantity is only updated for a positive new total; a
                // non-positive quantity is a TTL "refresh only" and leaves the held quantity unchanged.
                if (quantity > 0) existing.Quantity = quantity;
                await _context.SaveChangesAsync();

                // The Redis counter tracks available stock = pool - SUM(Active reservation quantities),
                // so it MUST move by exactly the amount the authoritative DB hold moved. Because the hold
                // quantity is unchanged when quantity <= 0, availability is unchanged and the counter must
                // NOT move either; otherwise an extend-to-0 (or negative) call would spuriously INCR the
                // counter by the held amount and transiently over-report available stock (and the broadcast
                // badge) until the next reconciliation reseed. Mirror the DB change in the counter delta.
                var counterDelta = quantity > 0 ? delta : 0;
                // adjust counter by counterDelta (>0 => DECR, <0 => INCR, 0 => publish-only), refresh hold TTL
                await TryAdjustReserveRedisAsync(basketId, productId, counterDelta, expiresAt - now, existing.FlashSaleId);

                if (tx != null) await tx.CommitAsync();
                return true;
            }
            catch (Exception)
            {
                if (tx != null) await tx.RollbackAsync();
                throw;
            }
        }

        // ---------- commit (STAGES ONLY — OrderService owns the flush) ----------
        public async Task CommitReservationAsync(string basketId)
        {
            var reservations = await _context.Set<Reservation>()
                .Where(r => r.Status == ReservationStatus.Active && r.BasketId == basketId)
                .ToListAsync();

            foreach (var r in reservations)
            {
                r.Status = ReservationStatus.Committed;

                if (r.FlashSaleId == null)
                {
                    var product = await _context.Set<Product>().FindAsync(r.ProductId);
                    if (product != null) product.StockQuantity -= r.Quantity;
                }
                else
                {
                    var sale = await _context.Set<FlashSale>().FindAsync(r.FlashSaleId.Value);
                    if (sale != null) sale.SaleStockQuantity -= r.Quantity;
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
            catch (RedisConnectionException ex) { LogRedisDegraded(ex, "ReleaseReservation", productId, basketId); }
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
                catch (RedisConnectionException ex) { LogRedisDegraded(ex, "ReleaseAllReservations", productId, basketId); }
                catch (RedisTimeoutException ex) { LogRedisDegraded(ex, "ReleaseAllReservations", productId, basketId); }
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
            catch (RedisConnectionException ex) { LogRedisDegraded(ex, "GetAvailableStock", productId); }
            catch (RedisTimeoutException ex) { LogRedisDegraded(ex, "GetAvailableStock", productId); }

            // fail-closed fallback: authoritative committed PostgreSQL state
            var (available, _) = await ComputeAvailableFromDbAsync(productId);
            return available;
        }

        // ---------- seeding / reconcile reseed ----------
        public async Task SeedStockCountersAsync()
        {
            var productIds = await _context.Set<Product>().Select(p => p.Id).ToListAsync();
            foreach (var productId in productIds)
            {
                var (available, flashSaleId) = await ComputeAvailableFromDbAsync(productId);
                try
                {
                    await _database.StringSetAsync(StockKey(productId), available);
                    await PublishStockAsync(productId, available, flashSaleId);
                }
                catch (RedisConnectionException ex) { LogRedisDegraded(ex, "SeedStockCounters", productId); }
                catch (RedisTimeoutException ex) { LogRedisDegraded(ex, "SeedStockCounters", productId); }
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

        private async Task TryReserveRedisAsync(string basketId, int productId, int quantity, TimeSpan ttl, int? flashSaleId)
        {
            try
            {
                await _database.StringSetAsync(HoldKey(basketId, productId), quantity, ttl > TimeSpan.Zero ? ttl : (TimeSpan?)null);
                var currentStock = await _database.StringDecrementAsync(StockKey(productId), quantity);
                await PublishStockAsync(productId, currentStock, flashSaleId);
            }
            catch (RedisConnectionException ex) { LogRedisDegraded(ex, "ReserveRedis", productId, basketId); }
            catch (RedisTimeoutException ex) { LogRedisDegraded(ex, "ReserveRedis", productId, basketId); }
        }

        private async Task TryAdjustReserveRedisAsync(string basketId, int productId, int delta, TimeSpan ttl, int? flashSaleId)
        {
            try
            {
                await _database.StringSetAsync(HoldKey(basketId, productId), 0, ttl > TimeSpan.Zero ? ttl : (TimeSpan?)null, when: When.Exists);
                long currentStock;
                if (delta > 0) currentStock = await _database.StringDecrementAsync(StockKey(productId), delta);
                else if (delta < 0) currentStock = await _database.StringIncrementAsync(StockKey(productId), -delta);
                else currentStock = (long)await _database.StringGetAsync(StockKey(productId));
                await PublishStockAsync(productId, currentStock, flashSaleId);
            }
            catch (RedisConnectionException ex) { LogRedisDegraded(ex, "AdjustReserveRedis", productId, basketId); }
            catch (RedisTimeoutException ex) { LogRedisDegraded(ex, "AdjustReserveRedis", productId, basketId); }
        }

        private async Task TrySetHoldTtlAsync(string basketId, int productId, TimeSpan ttl)
        {
            try { if (ttl > TimeSpan.Zero) await _database.KeyExpireAsync(HoldKey(basketId, productId), ttl); }
            catch (RedisConnectionException ex) { LogRedisDegraded(ex, "SetHoldTtl", productId, basketId); }
            catch (RedisTimeoutException ex) { LogRedisDegraded(ex, "SetHoldTtl", productId, basketId); }
        }

        private async Task TryDeleteHoldKeyAsync(string basketId, int productId)
        {
            try { await _database.KeyDeleteAsync(HoldKey(basketId, productId)); }
            catch (RedisConnectionException ex) { LogRedisDegraded(ex, "DeleteHoldKey", productId, basketId); }
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
            catch (RedisConnectionException ex) { LogRedisDegraded(ex, "PublishStock", productId); }
            catch (RedisTimeoutException ex) { LogRedisDegraded(ex, "PublishStock", productId); }
        }
    }
}
