using System;
using System.Linq;
using System.Text.Json;
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
using StackExchange.Redis;

namespace Infrastructure.Services
{
    // Infrastructure-layer BackgroundService that owns two responsibilities for the Real-Time
    // Inventory & Flash-Sale System:
    //
    //   1. THE SOLE ENFORCER OF RESERVATION EXPIRY. PostgreSQL has no native row TTL and the Redis
    //      hold-key TTL is only an optimization (a hint), never the authority. Expiry is therefore
    //      enforced exclusively here: on each pass every Active reservation whose ExpiresAt has passed
    //      is transitioned to Expired and its held quantity is returned to the available pool.
    //
    //   2. THE Redis <-> PostgreSQL RECONVERGER. Redis stock counters are a hot-path accelerator that
    //      can drift from committed PostgreSQL state (e.g. a Redis outage, a dropped DECR/INCR, or a
    //      reclaimed hold). Each pass reseeds the counters from authoritative committed stock so the
    //      two stores agree within a single reconciliation interval.
    //
    // Architecture (LAYER RULE, ABSOLUTE): this file lives in the Infrastructure layer, which
    // references ONLY Core. It NEVER references any API-layer real-time type (the SignalR hub or its
    // hub-context abstraction). Real-time broadcasting is decoupled EXCLUSIVELY through the Redis
    // "stock-updates" Pub/Sub channel: this service PUBLISHES corrected stock values, and the
    // API-layer broadcast bridge subscribes and performs the SignalR broadcast. That bridge preserves
    // the API -> Infrastructure -> Core dependency direction.
    //
    // Lifetime & DI: this type is registered in the API layer via AddHostedService<T>(), which makes
    // it a SINGLETON. It must therefore NOT constructor-inject the scoped services it needs
    // (StoreContext, IInventoryService, IFlashSaleService) directly — doing so would either fail
    // validation (captive dependency) or reuse a disposed context. Instead it injects the
    // singleton-safe IServiceScopeFactory and opens a fresh DI scope per pass, resolving the scoped
    // services from that scope. IConfiguration, IConnectionMultiplexer and ILogger are all
    // singleton-safe and injected directly.
    //
    // Sole-writer note (documented, deliberate): IInventoryService is the primary sole writer of the
    // Reservations table and the Redis stock keys. However, IInventoryService exposes no reclaim
    // method, and the feature specification explicitly assigns reclaim (set Expired) + INCR + publish +
    // reseed to THIS service. The reclaim DB write is performed directly on this service's per-pass
    // scoped StoreContext; the per-reclaim INCR + publish provides immediate client feedback; and the
    // AUTHORITATIVE Redis reconvergence is delegated to IInventoryService.SeedStockCountersAsync(),
    // which supersedes the transient INCR and brings Redis into agreement with PostgreSQL.
    //
    // Resilience: PostgreSQL is the source of truth. Every Redis call is wrapped fail-closed
    // (RedisConnectionException / RedisTimeoutException are swallowed) so a Redis outage never prevents
    // the pass from persisting the Expired transitions and advancing flash-sale statuses in PostgreSQL.
    //
    // Provider compatibility: the pass uses only LINQ + SaveChangesAsync with no raw SQL and no
    // explicit transaction, so it behaves identically on PostgreSQL (Npgsql), SQLite, and the EF Core
    // in-memory provider used by the test suite.
    public class StockReconciliationService : BackgroundService
    {
        // Verbatim Redis Pub/Sub channel name shared with InventoryService and the API-layer broadcast
        // bridge. Every stock mutation (including a reclaim) is published here as
        // { productId, currentStock, flashSaleId } in camelCase.
        private const string StockUpdatesChannel = "stock-updates";

        // Default cadence (seconds) used when the Inventory:ReconciliationIntervalSeconds configuration
        // key is absent or cannot be parsed.
        private const int DefaultReconciliationIntervalSeconds = 30;

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _config;
        private readonly IConnectionMultiplexer _redis;
        private readonly ILogger<StockReconciliationService> _logger;

        // The ILogger is optional (defaulted to null) so the service can be constructed directly in
        // unit tests with a mocked IServiceScopeFactory and a short interval, without wiring a logger.
        public StockReconciliationService(
            IServiceScopeFactory scopeFactory,
            IConfiguration config,
            IConnectionMultiplexer redis,
            ILogger<StockReconciliationService> logger = null)
        {
            _scopeFactory = scopeFactory;
            _config = config;
            _redis = redis;
            _logger = logger;
        }

        // Reads Inventory:ReconciliationIntervalSeconds via the IConfiguration indexer + int.TryParse.
        // The typed configuration-binder extension (the generic typed getter) is intentionally NOT used
        // because the binder package is absent from Infrastructure's dependency closure and would not
        // compile against the current reference set.
        // Evaluated once per loop iteration so the interval is test-overridable via configuration.
        private int ReconciliationIntervalSeconds =>
            int.TryParse(_config["Inventory:ReconciliationIntervalSeconds"], out var v)
                ? v
                : DefaultReconciliationIntervalSeconds;

        // BackgroundService entry point. Runs one reconciliation pass, then waits the configured
        // interval, until the host requests shutdown. A single failing pass is logged and swallowed so
        // the service keeps running; only cancellation ends the loop.
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Graceful shutdown requested mid-pass: exit the loop without logging an error.
                    break;
                }
                catch (Exception ex)
                {
                    // Never let a single bad pass terminate the background service; log and continue.
                    _logger?.LogError(ex, "Stock reconciliation pass failed");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(ReconciliationIntervalSeconds), stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    // Delay interrupted by shutdown: exit the loop.
                    break;
                }
            }
        }

        // Executes exactly one reconciliation pass inside its own DI scope. Steps:
        //   (a) reclaim Active reservations past ExpiresAt -> Expired, with best-effort INCR + publish;
        //   (b) persist the reclaimed status transitions to PostgreSQL;
        //   (c) advance flash-sale statuses by time (Scheduled -> Active -> Ended);
        //   (d) authoritatively reseed the Redis counters from committed PostgreSQL stock + republish.
        private async Task ReconcileOnceAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();
            var inventoryService = scope.ServiceProvider.GetRequiredService<IInventoryService>();
            var flashSaleService = scope.ServiceProvider.GetRequiredService<IFlashSaleService>();

            var now = DateTimeOffset.UtcNow;

            // (a) Reclaim Active reservations whose hold window has elapsed. Semantics are identical to
            // ExpiredReservationsSpecification(now); expressed as direct LINQ so no API.Specifications
            // dependency is introduced into the Infrastructure layer.
            var expired = await context.Set<Reservation>()
                .Where(r => r.Status == ReservationStatus.Active && r.ExpiresAt < now)
                .ToListAsync(ct);

            // Resolve the Redis primitives once for the pass. GetDatabase()/GetSubscriber() are cheap
            // and normally non-throwing, but guard them fail-closed so a Redis outage leaves the DB
            // reclaim path fully intact (database/subscriber simply remain null).
            IDatabase database = null;
            ISubscriber subscriber = null;
            try
            {
                database = _redis.GetDatabase();
                subscriber = _redis.GetSubscriber();
            }
            catch (RedisConnectionException) { }
            catch (RedisTimeoutException) { }

            foreach (var r in expired)
            {
                // Authoritative reclaim: the reservation no longer holds stock.
                r.Status = ReservationStatus.Expired;

                // Immediate hot-path feedback: return the held quantity to the counter and publish the
                // corrected value so subscribed clients see stock come back promptly. Best-effort and
                // fail-closed — a Redis failure here must not abort the DB reclaim; the authoritative
                // reseed in step (d) will converge the counter regardless.
                try
                {
                    if (database != null)
                    {
                        var currentStock = await database.StringIncrementAsync(
                            $"stock:product:{r.ProductId}", r.Quantity);

                        if (subscriber != null)
                        {
                            var payload = JsonSerializer.Serialize(
                                new { productId = r.ProductId, currentStock, flashSaleId = r.FlashSaleId },
                                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                            await subscriber.PublishAsync(StockUpdatesChannel, payload);
                        }
                    }
                }
                catch (RedisConnectionException) { }
                catch (RedisTimeoutException) { }
            }

            // (b) Persist the Expired transitions. This is the authoritative record of expiry and must
            // succeed independently of Redis availability.
            await context.SaveChangesAsync(ct);

            // (c) Advance flash-sale statuses by time (Scheduled -> Active -> Ended). This runs on the
            // same scoped context and saves its own changes; it is the only driver of sale transitions.
            await flashSaleService.AdvanceFlashSaleStatusesAsync();

            // (d) Authoritative Redis reconvergence via the sole stock writer: StringSet each counter
            // from committed PostgreSQL stock and republish. This supersedes the transient per-reclaim
            // INCR above and brings Redis into agreement with PostgreSQL within this interval.
            await inventoryService.SeedStockCountersAsync();
        }
    }
}
