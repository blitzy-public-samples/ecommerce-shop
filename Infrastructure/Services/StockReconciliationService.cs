using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    // (IInventoryService, IFlashSaleService) directly — doing so would either fail validation (captive
    // dependency) or reuse a disposed context. Instead it injects the singleton-safe IServiceScopeFactory
    // and opens a fresh DI scope per pass, resolving the scoped services from that scope. IConfiguration
    // and ILogger are singleton-safe and injected directly. No IConnectionMultiplexer is injected — this
    // service performs NO direct Redis I/O (see the sole-writer note below).
    //
    // Sole-writer note (P7-1): IInventoryService is THE sole writer of the Reservations table and the
    // Redis stock keys — reservation expiry included. This service ORCHESTRATES the periodic pass but no
    // longer mutates reservations or stock keys itself: it delegates the whole reclaim (Active -> Expired,
    // return held stock to the counters, publish, and residual hold-key cleanup) to
    // IInventoryService.ReclaimExpiredReservationsAsync(), and the authoritative counter reconvergence to
    // IInventoryService.SeedStockCountersAsync(). Previously this service set r.Status = Expired, called
    // StringIncrementAsync, and published to "stock-updates" DIRECTLY on its own scoped context and an
    // injected multiplexer, which violated that invariant; delegating restores it.
    //
    // Resilience: PostgreSQL is the source of truth. The delegated reclaim and reseed are best-effort /
    // fail-closed on Redis internally (any RedisException is swallowed inside InventoryService), so a
    // Redis outage never prevents a pass from persisting the Expired transitions and advancing flash-sale
    // statuses in PostgreSQL. A failing pass is caught in ExecuteAsync and the loop keeps running.
    //
    // Provider compatibility: the delegated operations use only LINQ + SaveChangesAsync with no raw SQL
    // and no explicit transaction, so a pass behaves identically on PostgreSQL (Npgsql), SQLite, and the
    // EF Core in-memory provider used by the test suite.
    public class StockReconciliationService : BackgroundService
    {
        // Default cadence (seconds) used when the Inventory:ReconciliationIntervalSeconds configuration
        // key is absent or cannot be parsed.
        private const int DefaultReconciliationIntervalSeconds = 30;

        // Lower bound (seconds): a misconfigured 0 or negative value must never turn the loop into a
        // tight CPU-spin (Task.Delay(<=0) returns immediately).
        private const int MinReconciliationIntervalSeconds = 1;

        // Upper bound (seconds): 24 hours. This caps the configured interval so that
        // TimeSpan.FromSeconds(interval) can never overflow Int32 milliseconds when handed to
        // Task.Delay. Task.Delay's ceiling is Int32.MaxValue ms (~24.855 days ≈ 2,147,483 s); any
        // configured value at/above ~2,147,484 s would throw ArgumentOutOfRangeException synchronously
        // and — since a .NET 5 BackgroundService does not observe an ExecuteAsync fault — SILENTLY kill
        // the loop (the sole enforcer of reservation expiry + the Redis↔PostgreSQL reconverger) after a
        // single pass with no log. 86,400 s = 86,400,000 ms is far below the ceiling, so a clamped value
        // is always safe. Any operator value above this sane maximum is bounded here rather than allowed
        // to disable reconciliation. (QA Issue #3.)
        private const int MaxReconciliationIntervalSeconds = 86_400;

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<StockReconciliationService> _logger;

        // The ILogger is optional (defaulted to null) so the service can be constructed directly in
        // unit tests with a mocked IServiceScopeFactory and a short interval, without wiring a logger.
        //
        // No IConnectionMultiplexer is injected: after P7-1 this service performs NO direct Redis I/O. All
        // Redis mutation (the reclaim INCR + publish, and the authoritative counter reseed) is delegated to
        // IInventoryService, resolved per-pass from the scope factory. This keeps InventoryService the sole
        // writer of the Redis stock keys and removes this service's former direct dependency on the client.
        public StockReconciliationService(
            IServiceScopeFactory scopeFactory,
            IConfiguration config,
            ILogger<StockReconciliationService> logger = null)
        {
            _scopeFactory = scopeFactory;
            _config = config;
            _logger = logger;
        }

        // Reads Inventory:ReconciliationIntervalSeconds via the IConfiguration indexer + int.TryParse.
        // The typed configuration-binder extension (the generic typed getter) is intentionally NOT used
        // because the binder package is absent from Infrastructure's dependency closure and would not
        // compile against the current reference set.
        // Evaluated once per loop iteration so the interval is test-overridable via configuration.
        // Clamped to [MinReconciliationIntervalSeconds, MaxReconciliationIntervalSeconds]:
        //   * the LOWER clamp (1s) stops a misconfigured 0/negative value from turning the loop into a
        //     tight, CPU-spinning loop (Task.Delay(<=0) returns immediately);
        //   * the UPPER clamp (86,400s / 24h) stops an extreme value from overflowing Int32 milliseconds
        //     inside TimeSpan.FromSeconds → Task.Delay and silently killing the loop (QA Issue #3).
        // Because the effective value is always within the safe range, Task.Delay can NEVER throw
        // ArgumentOutOfRangeException here. A missing/unparseable key falls back to the default (30s).
        private int ReconciliationIntervalSeconds =>
            int.TryParse(_config["Inventory:ReconciliationIntervalSeconds"], out var v)
                ? Math.Clamp(v, MinReconciliationIntervalSeconds, MaxReconciliationIntervalSeconds)
                : DefaultReconciliationIntervalSeconds;

        // BackgroundService entry point. Runs one reconciliation pass, then waits the configured
        // interval, until the host requests shutdown. A single failing pass is logged and swallowed so
        // the service keeps running; only cancellation ends the loop.
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Positive hosted-service startup confirmation (QA Issue #6): a single Information line so an
            // operator can tell from the logs alone that the reconciliation loop actually began and at what
            // cadence — liveness is no longer only inferable from side effects (counter reseeds).
            _logger?.LogInformation(
                "StockReconciliationService started; reconciliation interval = {IntervalSeconds}s.",
                ReconciliationIntervalSeconds);

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
                catch (ArgumentOutOfRangeException ex)
                {
                    // Defense in depth (QA Issue #3): the clamp on ReconciliationIntervalSeconds already
                    // guarantees the delay is within Task.Delay's Int32-millisecond ceiling, so this branch
                    // should be unreachable. But if some future change ever produced an out-of-range delay,
                    // this catch prevents the fault from escaping ExecuteAsync and silently terminating the
                    // loop (which a .NET 5 BackgroundService would NOT surface). Log it and fall back to the
                    // default cadence so the sole enforcer of reservation expiry keeps running.
                    _logger?.LogWarning(ex,
                        "Reconciliation delay was out of range; falling back to the default interval of " +
                        "{DefaultIntervalSeconds}s.", DefaultReconciliationIntervalSeconds);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(DefaultReconciliationIntervalSeconds), stoppingToken);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        // Executes exactly one reconciliation pass inside its own DI scope. This service ORCHESTRATES; it
        // does NOT itself write the Reservations table or the Redis stock keys (P7-1). Steps:
        //   (a)+(b) reclaim expired reservations (Active -> Expired) + return held stock to the counters +
        //           persist — delegated to IInventoryService.ReclaimExpiredReservationsAsync(), the SOLE
        //           writer of the Reservations table and the Redis stock keys;
        //   (c)     advance flash-sale statuses by time (Scheduled -> Active -> Ended);
        //   (d)     authoritatively reseed the Redis counters from committed PostgreSQL stock + republish.
        private async Task ReconcileOnceAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var inventoryService = scope.ServiceProvider.GetRequiredService<IInventoryService>();
            var flashSaleService = scope.ServiceProvider.GetRequiredService<IFlashSaleService>();

            // (a)+(b) Reclaim Active reservations whose hold window has elapsed. The ENTIRE reclaim — the
            // Active -> Expired transition, the persist, the per-reclaim INCR + publish, and the cleanup of
            // any residual hold keys — now lives behind the sole stock writer (P7-1). Previously this method
            // set r.Status = Expired, called StringIncrementAsync, and published to "stock-updates" DIRECTLY
            // on this service's scoped StoreContext and the injected IConnectionMultiplexer, which violated
            // the invariant that InventoryService is the ONLY component that writes the Reservations table
            // and the Redis stock keys (AAP §0.1.2 / §0.7). The reclaim is best-effort / fail-closed
            // internally, so a Redis outage never prevents the Expired transitions from being persisted to
            // PostgreSQL (the authority).
            await inventoryService.ReclaimExpiredReservationsAsync();

            // (c) Advance flash-sale statuses by time (Scheduled -> Active -> Ended). Runs on its own scoped
            // context and saves its own changes; it is the only driver of sale transitions.
            await flashSaleService.AdvanceFlashSaleStatusesAsync();

            // (d) Authoritative Redis reconvergence via the sole stock writer: StringSet each counter from
            // committed PostgreSQL stock and republish. This brings Redis into agreement with PostgreSQL
            // within this interval regardless of any transient counter drift during the pass.
            await inventoryService.SeedStockCountersAsync();
        }
    }
}
