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

        public InventoryService(StoreContext context, IConnectionMultiplexer redis,
            IFlashSaleService flashSaleService, IConfiguration config)
        {
            _context = context;
            _redis = redis;
            _database = redis.GetDatabase();
            _subscriber = redis.GetSubscriber();
            _flashSaleService = flashSaleService;
            _config = config;
        }

        // Config via the IConfiguration indexer + int.TryParse, because the typed configuration-binder
        // extension package is absent from Infrastructure's closure and the generic typed getter will not compile.
        private int ReservationTtlMinutes =>
            int.TryParse(_config["Inventory:ReservationTtlMinutes"], out var v) ? v : 10;

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

        public async Task ExtendReservationAsync(string basketId, int productId, int quantity)
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
                    // nothing to extend -> release lock, delegate to create (which opens its own tx)
                    if (tx != null) await tx.RollbackAsync();
                    await CreateReservationAsync(basketId, productId, quantity > 0 ? quantity : 1);
                    return;
                }

                var delta = quantity - existing.Quantity; // interpret quantity as new desired total for the basket line
                if (delta > 0)
                {
                    var product = await _context.Set<Product>().FindAsync(productId);
                    var sale = await _flashSaleService.GetActiveFlashSaleForProductAsync(productId);
                    var capacity = sale?.SaleStockQuantity ?? (product?.StockQuantity ?? 0);
                    int? poolId = sale?.Id;
                    var reserved = await _context.Set<Reservation>()
                        .Where(r => r.Status == ReservationStatus.Active
                                    && r.ProductId == productId
                                    && r.FlashSaleId == poolId)
                        .SumAsync(r => (int?)r.Quantity) ?? 0;
                    var available = capacity - reserved;
                    if (available < delta)
                    {
                        // cannot grow: renew TTL only
                        existing.ExpiresAt = expiresAt;
                        await _context.SaveChangesAsync();
                        if (tx != null) await tx.CommitAsync();
                        await TrySetHoldTtlAsync(basketId, productId, expiresAt - now);
                        return;
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

                // delete hold key (best-effort/fail-closed); counter is NOT changed on commit
                await TryDeleteHoldKeyAsync(basketId, r.ProductId);
            }
            // NO SaveChangesAsync here: OrderService's _unitOfWork.Complete() performs the single atomic flush.
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
            catch (RedisConnectionException) { }
            catch (RedisTimeoutException) { }
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
            catch (RedisConnectionException) { }
            catch (RedisTimeoutException) { }

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
                catch (RedisConnectionException) { }
                catch (RedisTimeoutException) { }
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
            catch (RedisConnectionException) { }
            catch (RedisTimeoutException) { }
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
            catch (RedisConnectionException) { }
            catch (RedisTimeoutException) { }
        }

        private async Task TrySetHoldTtlAsync(string basketId, int productId, TimeSpan ttl)
        {
            try { if (ttl > TimeSpan.Zero) await _database.KeyExpireAsync(HoldKey(basketId, productId), ttl); }
            catch (RedisConnectionException) { }
            catch (RedisTimeoutException) { }
        }

        private async Task TryDeleteHoldKeyAsync(string basketId, int productId)
        {
            try { await _database.KeyDeleteAsync(HoldKey(basketId, productId)); }
            catch (RedisConnectionException) { }
            catch (RedisTimeoutException) { }
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
            catch (RedisConnectionException) { }
            catch (RedisTimeoutException) { }
        }
    }
}
