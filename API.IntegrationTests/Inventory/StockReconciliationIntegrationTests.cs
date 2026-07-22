using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using API.IntegrationTests.Infrastructure;
using Core.Entities;
using Core.Interfaces;
using FluentAssertions;
using Infrastructure.Data;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace API.IntegrationTests.Inventory
{
    /// <summary>
    /// End-to-end reconciliation test (AAP §0.5.2, §0.7). Proves that the StockReconciliationService
    /// background loop, within a single reconciliation interval, reclaims an Active reservation whose
    /// ExpiresAt is in the past (-> Expired), and reseeds/republishes the Redis stock counter so it
    /// re-converges with committed PostgreSQL state. Runs against the REAL Testcontainers PostgreSQL +
    /// Redis provided by the reused ContainerFixture (no SQLite/InMemory/mocks).
    ///
    /// <para>
    /// <b>Why reconciliation owns expiry (AAP §0.7).</b> PostgreSQL has no native row TTL and the Redis
    /// hold-key TTL is only a hot-path optimization, never the authority. Reservation expiry is therefore
    /// enforced EXCLUSIVELY by the <see cref="StockReconciliationService"/> background loop: each pass
    /// reclaims every <c>Active</c> reservation whose <c>ExpiresAt</c> has elapsed (transitioning it to
    /// <c>Expired</c>), returns its held quantity to the available pool, and — because the Redis counter
    /// is a fail-closed accelerator that can drift from committed PostgreSQL state — authoritatively
    /// reseeds <c>stock:product:{id}</c> from committed stock and republishes the corrected value. This
    /// test asserts the CONVERGED end state (reservation <c>Expired</c>; counter back to committed stock),
    /// not the intermediate INCR-vs-reseed mechanism, so it is insensitive to whether the per-reclaim
    /// <c>INCR</c> (8 -> 10) or the full reseed (-> 10) produced the final value; both converge to 10.
    /// </para>
    ///
    /// <para>
    /// <b>Real infrastructure only, deterministic, self-cleaning (AAP §0.7 testing convention).</b> The
    /// class reuses the existing <see cref="ContainerFixture"/> and <c>CustomWebApplicationFactory</c>
    /// exactly as-is — genuine Testcontainers PostgreSQL + Redis, no new fixture/factory/stub and no
    /// SQLite/InMemory/mocks (a running Docker daemon is required). There is no fixed <c>Thread.Sleep</c>
    /// anywhere: convergence is awaited with a deadline-bounded <c>await Task.Delay(...)</c> poll loop. A
    /// FRESH <see cref="StockReconciliationService"/> with a 1-second interval is constructed for the
    /// test rather than depending on the 30-second hosted instance inside the factory host (which
    /// <c>CustomWebApplicationFactory</c> does not reconfigure), keeping the test fast and deterministic.
    /// State is restored in a <c>finally</c> block: the fresh service is stopped, reservation rows for
    /// the product are deleted, the Redis keys are removed, and <c>Products.StockQuantity</c> is reset to
    /// its original value.
    /// </para>
    /// </summary>
    public class StockReconciliationIntegrationTests : IClassFixture<ContainerFixture>
    {
        private readonly ContainerFixture _fixture;

        /// <summary>
        /// xUnit injects this class's dedicated <see cref="ContainerFixture"/> (a per-class
        /// <c>IClassFixture&lt;ContainerFixture&gt;</c>, started once for THIS class), which exposes the
        /// wired <c>CustomWebApplicationFactory</c> pointing at the isolated Testcontainers
        /// PostgreSQL + Redis instances.
        /// </summary>
        /// <param name="fixture">This class's isolated, already-initialised container fixture.</param>
        public StockReconciliationIntegrationTests(ContainerFixture fixture)
        {
            _fixture = fixture;
        }

        /// <summary>
        /// Proves Redis↔PostgreSQL convergence within one reconciliation interval (AAP §0.5.2 step (d),
        /// §0.7). Arranges a divergent state — committed <c>StockQuantity = 10</c>, a stale <c>Active</c>
        /// reservation (qty 2) whose <c>ExpiresAt</c> is already in the past, and a Redis counter manually
        /// driven to 8 — then starts a fresh 1-second <see cref="StockReconciliationService"/> and awaits
        /// (with a bounded, sleep-free poll) until the loop has: (1) reclaimed the expired reservation to
        /// <c>ReservationStatus.Expired</c>, and (2) reseeded/republished <c>stock:product:{id}</c> back
        /// to the committed authoritative value (10). Both conditions gate the convergence signal so the
        /// assertion is immune to reclaim/reseed ordering and to the harmless, idempotent 30-second hosted
        /// instance also firing. All state is cleaned up deterministically in the <c>finally</c> block.
        /// </summary>
        [Fact]
        public async Task Reconciliation_WhenReservationExpired_ReclaimsAndReconvergesRedisWithPostgres()
        {
            // Arrange -----------------------------------------------------------------------------------
            const int seededStock = 10;
            const int heldQuantity = 2;
            var basketId = $"recon-{Guid.NewGuid():N}";

            var productId = await GetFirstSeededProductIdAsync();
            var originalStock = await GetProductStockQuantityAsync(productId);

            await SetProductStockQuantityAsync(productId, seededStock);
            await DeleteReservationsForProductAsync(productId);

            // Seed the Redis counter from committed PostgreSQL stock -> stock:product:{id} = 10.
            await SeedStockCountersAsync();

            // Simulate a stale hold that was never committed and has already expired.
            await InsertReservationAsync(new Reservation
            {
                ProductId = productId,
                BasketId = basketId,
                Quantity = heldQuantity,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5), // PAST -> reclaimable
                Status = ReservationStatus.Active,
                FlashSaleId = null
            });

            var redis = _fixture.Factory.Services.GetRequiredService<IConnectionMultiplexer>();

            // Mirror the hold in Redis: DECR to 8 so pre-reconciliation Redis (8) diverges from the
            // committed authoritative available (10, once the expired hold is reclaimed).
            await redis.GetDatabase().StringDecrementAsync($"stock:product:{productId}", heldQuantity);

            // Build a FRESH reconciliation service with a 1-second interval (do NOT touch the 30s host
            // instance). CustomWebApplicationFactory does not override Inventory:* keys.
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["Inventory:ReconciliationIntervalSeconds"] = "1"
                })
                .Build();
            var scopeFactory = _fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
            // P7-1: the reconciliation service no longer injects an IConnectionMultiplexer — it delegates all
            // reclaim/reseed Redis I/O to the sole stock writer (the real InventoryService resolved per-pass
            // from this scope factory). The `redis` handle above is still used only for the test's own setup
            // (mirroring the hold via DECR) and to assert convergence below.
            var svc = new StockReconciliationService(scopeFactory, config, logger: null);

            var stopped = false;
            using var cts = new CancellationTokenSource();
            try
            {
                // Act ---------------------------------------------------------------------------------
                await svc.StartAsync(cts.Token);

                // Poll (awaited delay-loop; NO Thread.Sleep) until BOTH conditions hold or timeout.
                var deadline = DateTime.UtcNow.AddSeconds(10);
                var converged = false;
                while (DateTime.UtcNow < deadline)
                {
                    using (var scope = _fixture.Factory.Services.CreateScope())
                    {
                        var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();
                        var reservation = await ctx.Reservations.AsNoTracking()
                            .FirstOrDefaultAsync(r => r.BasketId == basketId && r.ProductId == productId);
                        var counter = (long?)await redis.GetDatabase()
                            .StringGetAsync($"stock:product:{productId}");

                        if (reservation != null
                            && reservation.Status == ReservationStatus.Expired
                            && counter == seededStock)
                        {
                            converged = true;
                            break;
                        }
                    }

                    await Task.Delay(200);
                }

                await svc.StopAsync(CancellationToken.None);
                stopped = true;

                // Assert ------------------------------------------------------------------------------
                converged.Should().BeTrue(
                    "reconciliation must reclaim the expired reservation and reconverge Redis with " +
                    "committed PostgreSQL within one interval");

                using (var scope = _fixture.Factory.Services.CreateScope())
                {
                    var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();
                    var reservation = await ctx.Reservations.AsNoTracking()
                        .FirstOrDefaultAsync(r => r.BasketId == basketId && r.ProductId == productId);
                    reservation.Should().NotBeNull();
                    reservation.Status.Should().Be(ReservationStatus.Expired);
                }

                var finalCounter = (long?)await redis.GetDatabase()
                    .StringGetAsync($"stock:product:{productId}");
                finalCounter.Should().Be(seededStock,
                    "the reclaimed 2 units are returned and the counter is reseeded to committed stock");

                // Optional cross-check of the authoritative read path.
                using (var scope = _fixture.Factory.Services.CreateScope())
                {
                    var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
                    (await inventory.GetAvailableStockAsync(productId)).Should().Be(seededStock);
                }
            }
            finally
            {
                if (!stopped)
                {
                    await svc.StopAsync(CancellationToken.None);
                }

                await DeleteReservationsForProductAsync(productId);
                var db = redis.GetDatabase();
                await db.KeyDeleteAsync($"stock:product:{productId}");
                await db.KeyDeleteAsync($"reservation:{basketId}:{productId}");
                await SetProductStockQuantityAsync(productId, originalStock);
            }
        }

        // ---- helpers (fresh-scope pattern mirrored from OrderConcurrencyTests) ----------------------

        private async Task<int> GetFirstSeededProductIdAsync()
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();
            var product = await ctx.Products.AsNoTracking().OrderBy(p => p.Id).FirstAsync();
            return product.Id;
        }

        private async Task<int> GetProductStockQuantityAsync(int productId)
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();
            var product = await ctx.Products.AsNoTracking().FirstAsync(p => p.Id == productId);
            return product.StockQuantity;
        }

        private async Task SetProductStockQuantityAsync(int productId, int stockQuantity)
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();
            var product = await ctx.Products.FirstAsync(p => p.Id == productId);
            product.StockQuantity = stockQuantity;
            await ctx.SaveChangesAsync();
        }

        private async Task DeleteReservationsForProductAsync(int productId)
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();
            var reservations = await ctx.Reservations.Where(r => r.ProductId == productId).ToListAsync();
            if (reservations.Count == 0) return;
            ctx.Reservations.RemoveRange(reservations);
            await ctx.SaveChangesAsync();
        }

        private async Task InsertReservationAsync(Reservation reservation)
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();
            ctx.Reservations.Add(reservation);
            await ctx.SaveChangesAsync();
        }

        private async Task SeedStockCountersAsync()
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
            await inventory.SeedStockCountersAsync();
        }
    }
}
