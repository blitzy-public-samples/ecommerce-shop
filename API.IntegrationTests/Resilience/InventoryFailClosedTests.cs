using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using API.IntegrationTests.Infrastructure;
using Core.Entities;
using Core.Interfaces;
using FluentAssertions;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace API.IntegrationTests.Resilience
{
    /// <summary>
    /// Fail-closed CORRECTNESS test for the inventory reservation path. With the real ASP.NET Core
    /// application running in-process (<see cref="CustomWebApplicationFactory"/>) against REAL
    /// Testcontainers PostgreSQL + Redis, this test STOPS the Redis container mid-test and proves that
    /// <see cref="IInventoryService.CreateReservationAsync"/> falls back to the PostgreSQL
    /// <c>SELECT ... FOR UPDATE</c> row-locked stock check: exactly the committed stock is grantable, the
    /// rest are denied, and stock is NEVER assumed available on the cache outage (no oversell).
    ///
    /// <para>
    /// <b>Divergence from <see cref="FailClosedTests"/>.</b> That sibling asserts a structured HTTP 500 for
    /// endpoints that have NO fallback when their datastore dies. <see cref="Infrastructure.Services.InventoryService"/>
    /// is the opposite by design: every Redis call is wrapped in try/catch
    /// (<c>RedisConnectionException</c>/<c>RedisTimeoutException</c>), so with Redis down
    /// <c>CreateReservationAsync</c> must NOT throw and must NOT surface a 500 - it enforces stock via the
    /// PostgreSQL row lock. This test therefore mirrors the STRUCTURE of <see cref="FailClosedTests"/>
    /// (private per-<c>[Fact]</c> <see cref="ContainerFixture"/>, <see cref="IAsyncLifetime"/>,
    /// <c>StopAsync</c>) but the ASSERTION is fail-closed correctness, not a structured error.
    /// </para>
    ///
    /// <para>
    /// <b>Per-<c>[Fact]</c> fixture ownership (NOT <c>IClassFixture</c>).</b> Stopping a container is
    /// destructive, so this class owns its own <see cref="ContainerFixture"/> instance and implements
    /// <see cref="IAsyncLifetime"/>; xUnit builds a fresh instance per <c>[Fact]</c>, giving each test its
    /// own isolated PostgreSQL + Redis pair. Stopping Redis here can never disrupt a sibling test/class.
    /// </para>
    /// </summary>
    public class InventoryFailClosedTests : IAsyncLifetime
    {
        /// <summary>Dedicated, non-shared container fixture owned by this test instance (per <c>[Fact]</c>).</summary>
        private readonly ContainerFixture _fixture = new ContainerFixture();

        /// <summary>Starts and seeds this instance's own PostgreSQL + Redis containers and the in-process host.</summary>
        public Task InitializeAsync() => _fixture.InitializeAsync();

        /// <summary>Disposes this instance's containers/host; the stopped Redis container is disposed here too.</summary>
        public Task DisposeAsync() => _fixture.DisposeAsync();

        /// <summary>
        /// Hard upper bound on any single fail-closed reservation call. With Redis down, the fallback to the
        /// PostgreSQL row-locked check must complete PROMPTLY and never hang; a call that exceeds this bound
        /// fails the no-hang guard rather than stalling the suite.
        /// </summary>
        private static readonly TimeSpan FailClosedTimeout = TimeSpan.FromSeconds(60);

        [Fact]
        public async Task CreateReservation_WhenRedisStopped_FallsBackToPostgresRowLockAndNeverOversells()
        {
            // ---------------- Arrange ----------------
            const int stock = 3;   // N: authoritative committed PostgreSQL stock for the product under test.
            const int extra = 3;   // Additional attempts beyond stock that MUST be denied (no oversell).

            // A real seeded product id (fresh no-tracking scope; read-only).
            int productId;
            using (var scope = _fixture.Factory.Services.CreateScope())
            {
                var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();
                productId = await ctx.Products.AsNoTracking()
                    .OrderBy(p => p.Id).Select(p => p.Id).FirstAsync();
            }

            // Set authoritative stock to N and remove any pre-existing reservations for this product.
            using (var scope = _fixture.Factory.Services.CreateScope())
            {
                var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();
                var product = await ctx.Products.FirstAsync(p => p.Id == productId);
                product.StockQuantity = stock;

                var stale = await ctx.Set<Reservation>()
                    .Where(r => r.ProductId == productId).ToListAsync();
                ctx.Set<Reservation>().RemoveRange(stale);

                await ctx.SaveChangesAsync();
            }

            // Warm the singleton IConnectionMultiplexer WHILE Redis is UP (and seed counters: a hot path that
            // then goes cold). The app registers IConnectionMultiplexer via ConnectionMultiplexer.Connect with
            // AbortOnConnectFail=true, so Connect THROWS if Redis is down at first resolution. Warming here
            // guarantees the multiplexer is already connected before the stop, so post-stop Redis ops throw
            // (and are caught) INSIDE InventoryService rather than at DI construction.
            using (var scope = _fixture.Factory.Services.CreateScope())
            {
                var inv = scope.ServiceProvider.GetRequiredService<IInventoryService>();
                await inv.SeedStockCountersAsync();
            }

            // Pre-generate N + extra unique basket ids.
            var basketIds = new List<string>();
            for (var i = 0; i < stock + extra; i++)
            {
                basketIds.Add($"inv-failclosed-{Guid.NewGuid():N}");
            }

            // ---------------- Act ----------------
            // Stop the REAL Redis container. The released port refuses the next connection promptly (no sleep).
            await _fixture.RedisContainer.StopAsync();

            // Attempt all reservations sequentially, each in its own DI scope (fresh Scoped InventoryService +
            // StoreContext). Do NOT wrap the call in a swallowing try/catch: if a Redis exception escapes
            // CreateReservationAsync, that is a real fail-closed DEFECT and this test MUST fail.
            var results = new List<bool>();
            foreach (var basketId in basketIds)
            {
                using (var scope = _fixture.Factory.Services.CreateScope())
                {
                    var inv = scope.ServiceProvider.GetRequiredService<IInventoryService>();

                    var callTask = inv.CreateReservationAsync(basketId, productId, 1);

                    // No-hang guard: the fail-closed path must complete well within the bound.
                    var completed = await Task.WhenAny(callTask, Task.Delay(FailClosedTimeout));
                    completed.Should().BeSameAs(callTask,
                        "with Redis down, CreateReservationAsync must fall back to the PostgreSQL row-locked " +
                        "check promptly and never hang");

                    // Awaiting the call re-throws if the service let a Redis exception escape (a fail-closed
                    // defect) - surfacing it as a test failure instead of silently assuming availability.
                    results.Add(await callTask);
                }
            }

            // ---------------- Assert (fail-closed correctness) ----------------
            // Exactly N granted and the rest denied: the PostgreSQL SELECT ... FOR UPDATE path enforced the
            // stock limit even though Redis was down (never assumed available on the cache outage).
            results.Count(granted => granted).Should().Be(stock,
                "exactly the committed PostgreSQL stock must be grantable when Redis is unavailable");
            results.Count(granted => !granted).Should().Be(extra,
                "attempts beyond committed stock must be denied via the PostgreSQL fallback (no oversell)");

            // Authoritative DB: the sum of Active reservation quantity equals N (no oversell).
            using (var scope = _fixture.Factory.Services.CreateScope())
            {
                var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();
                var activeHeld = await ctx.Set<Reservation>()
                    .Where(r => r.ProductId == productId && r.Status == ReservationStatus.Active)
                    .SumAsync(r => (int?)r.Quantity) ?? 0;
                activeHeld.Should().Be(stock, "the authoritative PostgreSQL state must never oversell");
            }

            // Available stock reads 0 (never negative): with Redis down GetAvailableStockAsync falls back to
            // the committed-PostgreSQL recompute (capacity - sum(Active)) and clamps negatives to 0.
            using (var scope = _fixture.Factory.Services.CreateScope())
            {
                var inv = scope.ServiceProvider.GetRequiredService<IInventoryService>();
                var available = await inv.GetAvailableStockAsync(productId);
                available.Should().Be(0, "all committed stock is held by Active reservations; never negative");
            }

            // Teardown: the stopped Redis container and PostgreSQL are disposed by _fixture.DisposeAsync().
        }
    }
}
