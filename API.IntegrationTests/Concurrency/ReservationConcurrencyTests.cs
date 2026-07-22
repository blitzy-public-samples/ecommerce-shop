using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;                          // JsonElement — parse the exact 409 INSUFFICIENT_STOCK body
using System.Threading;                          // SemaphoreSlim — bound simultaneous in-flight reserves
using System.Threading.Tasks;
using API.Dtos;
using API.IntegrationTests.Infrastructure;
using Core.Entities;
using FluentAssertions;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace API.IntegrationTests.Concurrency
{
    /// <summary>
    /// End-to-end <b>integration</b> tests proving the Real-Time Inventory &amp; Flash Sale feature's hard,
    /// non-negotiable <b>zero-oversell invariant</b> (AAP §0.2.3, §0.4.1 Group 5, §0.5.1, §0.6). Requests flow
    /// through the <b>real</b> ASP.NET Core HTTP pipeline (<c>POST api/inventory/reserve</c>,
    /// <c>POST api/flash-sales</c>, <c>GET api/flash-sales/active</c>) against a <b>real</b> PostgreSQL +
    /// Redis pair provisioned by Testcontainers via this class's dedicated <see cref="ContainerFixture"/>.
    /// Nothing on the reserve path is mocked — the only harness double is the offline Stripe stub wired by
    /// <c>CustomWebApplicationFactory</c>, which the reservation flow never touches.
    ///
    /// <para>
    /// <b>Why a relational provider is mandatory.</b> The guarantee is delivered by the EF Core
    /// optimistic-concurrency token <c>FlashSale.Version</c> with a single server-side retry (no distributed
    /// lock): concurrent reservers all read the same original <c>Version</c>, only one
    /// "<c>UPDATE ... SET Version = original+1 WHERE Version = original</c>" wins, and the losers'
    /// transactions (including their reservation INSERT) roll back. EF Core's <b>InMemory</b> provider does
    /// NOT enforce concurrency tokens, so it can never reproduce this race; a genuine relational database
    /// (Testcontainers PostgreSQL) is the ONLY place the guard can be proven. This file is therefore the
    /// authoritative relational-provider proof of the <c>FlashSale.Version</c> concurrency guard under real
    /// concurrency.
    /// </para>
    ///
    /// <para>
    /// <b>⭐ The invariant is an UPPER BOUND, never equality.</b> Firing 500 concurrent single-unit reserves
    /// against a 100-unit allocation, the sum of active reserved units persisted in the database must be
    /// <c>&lt;= StockAllocation</c> — the system must NEVER over-allocate. It is NOT asserted to equal 100:
    /// because every successful reserve bumps <c>FlashSale.Version</c>, highly-concurrent reserves collide on
    /// the token and yield many <c>RESERVATION_CONFLICT</c> 409s, so the number of <c>200 OK</c> successes
    /// (and thus the total reserved) is typically <b>fewer</b> than 100. Asserting exactly 100 successes would
    /// be WRONG and flaky; the truthful, stable invariant is the ceiling <c>&lt;= 100</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Per-class isolation + deterministic cleanup.</b> This class owns its own isolated, disposable
    /// PostgreSQL/Redis pair (an <c>IClassFixture&lt;ContainerFixture&gt;</c>). The two facts use DISTINCT
    /// seeded products (<c>skip: 0</c> and <c>skip: 1</c>) so their state never overlaps regardless of
    /// execution order, each asserts persisted state through a fresh short-lived <see cref="StoreContext"/>
    /// scope with <c>AsNoTracking()</c>, and each restores state in a <c>finally</c> block by removing the
    /// reservations it created and the flash sale it scheduled. There is no fixed <c>Thread.Sleep</c>
    /// anywhere — readiness is a bounded asynchronous poll and all work is awaited directly. Naming follows
    /// the repository convention <c>MethodName_StateUnderTest_ExpectedBehavior</c> with an Arrange-Act-Assert
    /// structure and FluentAssertions.
    /// </para>
    /// </summary>
    public class ReservationConcurrencyTests : IClassFixture<ContainerFixture>
    {
        /// <summary>
        /// Total number of <c>POST api/inventory/reserve</c> requests fired behind the starting gun. 500
        /// vastly exceeds the 100-unit allocation, guaranteeing heavy contention on <c>FlashSale.Version</c>.
        /// </summary>
        private const int ConcurrentReservationCount = 500;

        /// <summary>
        /// The flash-sale allocation under test — the oversell ceiling. The sum of active reserved units must
        /// never exceed this value.
        /// </summary>
        private const int StockAllocation = 100;

        // Cap simultaneous in-flight reserves below the PostgreSQL max_connections=100 / Npgsql Max Pool
        // Size=100 ceiling. 64-way concurrency still VASTLY exceeds allocation 100 and fully exercises
        // FlashSale.Version optimistic-concurrency contention, so the zero-oversell invariant is proven
        // without risking a "53300 too many clients" / pool-timeout flake. All 500 tasks are still launched
        // behind the starting gun (they simply queue at the throttle once released).
        private const int MaxInFlightReservations = 64;

        /// <summary>
        /// This class's dedicated fixture (started once for THIS class) providing the real Testcontainers
        /// PostgreSQL + Redis and the wired in-process host. Injected by xUnit through the class fixture.
        /// </summary>
        private readonly ContainerFixture _fixture;

        /// <summary>
        /// Receives this class's dedicated <see cref="ContainerFixture"/> from xUnit's class-fixture machinery.
        /// </summary>
        /// <param name="fixture">This class's isolated, already-initialised container fixture.</param>
        public ReservationConcurrencyTests(ContainerFixture fixture) => _fixture = fixture;

        // -----------------------------------------------------------------------------------------------
        // Tests
        // -----------------------------------------------------------------------------------------------

        /// <summary>
        /// THE core zero-oversell proof. Fires <see cref="ConcurrentReservationCount"/> (500) concurrent
        /// single-unit <c>POST api/inventory/reserve</c> requests — each with a <b>distinct</b> session id —
        /// against an active flash sale whose <see cref="StockAllocation"/> is 100, and asserts the system
        /// NEVER over-allocates: the sum of active reserved units persisted in the database is
        /// <c>&lt;= 100</c> (an UPPER BOUND, never <c>== 100</c>).
        ///
        /// <para>
        /// Every request carries a fresh <c>Guid.NewGuid().ToString()</c> session id. This is MANDATORY: the
        /// <c>SessionRateLimitFilter</c> allows 10 requests / 60s per session, so reusing a session would
        /// trip a <c>429</c> that would mask the oversell guard. With 500 distinct sessions each session
        /// makes exactly one request, so the limiter never fires (asserted via <c>NotContain(429)</c>).
        /// </para>
        ///
        /// <para>
        /// Under this contention many reserves lose the <c>FlashSale.Version</c> optimistic-concurrency race
        /// and return <c>409 RESERVATION_CONFLICT</c>, so the number of <c>200 OK</c> successes is typically
        /// fewer than 100. Because the in-process TestServer loses no response, no reservation expires
        /// mid-run (TTL 300s), and the sweep only removes already-expired rows, each <c>200 OK</c>
        /// corresponds one-to-one with exactly one persisted single-unit reservation — hence
        /// <c>successCount == totalReserved</c>, and both are <c>&lt;= 100</c>.
        /// </para>
        /// </summary>
        [Fact]
        public async Task Reserve_With500ConcurrentSingleUnitRequestsAgainst100Allocation_NeverOversells()
        {
            // Arrange -----------------------------------------------------------------------------------
            // A REAL seeded product id (skip: 0) so ReserveAsync's active-sale lookup resolves a genuine row.
            var productId = await GetSeededProductIdAsync(skip: 0);

            // Schedule an ACTIVE flash sale (window [now-1min, now+1hr]) with the 100-unit allocation. This is
            // the required precondition: ReserveAsync only reserves against an active sale for the product.
            var sale = await CreateActiveFlashSaleAsync(productId, StockAllocation);

            // Reserve is anonymous (no [Authorize]); use the anonymous burst client for all 500 requests.
            using var client = _fixture.CreateClient();

            // Warm the Store-DB read path / connection pool AND confirm the sale is active + queryable BEFORE
            // the burst. This is a bounded async readiness poll, never a fixed Thread.Sleep.
            await WarmUpActiveSalesAsync(client, sale.Id);

            // 500 DISTINCT basket-style UUID session ids — one per request — so SessionRateLimitFilter
            // (10/min/session) can NEVER return 429 and mask the oversell guard.
            var sessionIds = Enumerable.Range(0, ConcurrentReservationCount)
                .Select(_ => Guid.NewGuid().ToString())
                .ToList();

            // Act ---------------------------------------------------------------------------------------
            // A genuine synchronized "starting gun": all 500 async tasks park on a TaskCompletionSource gate
            // and are released together, so their read-modify-write sequences genuinely overlap (a real race
            // on FlashSale.Version) rather than incidentally serializing. This scales to 500 parked tasks
            // WITHOUT the thread-pool starvation a Barrier(500) of blocking SignalAndWait threads would risk.
            // A SemaphoreSlim bounds the number of simultaneously in-flight reserves under the PostgreSQL
            // max_connections=100 / Npgsql Max Pool Size=100 ceiling; all 500 tasks still launch behind the
            // gun and queue at the throttle. Neither the gate nor the throttle is a fixed delay.
            using var throttle = new SemaphoreSlim(MaxInFlightReservations);
            // RunContinuationsAsynchronously so SetResult does not inline-run 500 continuations on the caller.
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var tasks = sessionIds.Select(async sessionId =>
            {
                await gate.Task;                // all 500 tasks park here until the gun fires
                await throttle.WaitAsync();     // bound simultaneous DB-hitting reserves under the ceiling
                try
                {
                    return await client.PostAsJsonAsync("api/inventory/reserve",
                        BuildReserveDto(productId, sessionId));
                }
                finally
                {
                    throttle.Release();
                }
            }).ToList();

            gate.SetResult(true);               // fire the starting gun — release all 500 together
            var responses = await Task.WhenAll(tasks); // WhenAll preserves input order

            try
            {
                // Assert ------------------------------------------------------------------------------------
                responses.Should().HaveCount(ConcurrentReservationCount);

                // No request may surface an unhandled 500 (the global ExceptionMiddleware would render one).
                responses.Should().NotContain(r => (int)r.StatusCode == 500,
                    "no concurrent reserve may degrade into an unhandled server error");

                // Distinct sessions => the 10/min/session rate limiter must NEVER trip; a 429 here would
                // indicate a session-uniqueness bug in the test that would otherwise mask the oversell guard.
                responses.Should().NotContain(r => (int)r.StatusCode == 429,
                    "every request uses a distinct session id, so SessionRateLimitFilter must never return 429");

                // Every response is either a success or a documented conflict (INSUFFICIENT_STOCK or
                // RESERVATION_CONFLICT — both surface as HTTP 409), never any other status.
                responses.Should().OnlyContain(
                    r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict,
                    "each concurrent reserve resolves to 200 OK or a 409 conflict, never any other status");

                var successCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);

                // THE ZERO-OVERSELL INVARIANT: the sum of active reserved units persisted in the DB must NEVER
                // exceed the allocation. This is an UPPER BOUND, not equality — Version contention yields many
                // RESERVATION_CONFLICT 409s, so successCount (and thus totalReserved) is typically < 100.
                // Asserting == 100 would be WRONG/flaky.
                var totalReserved = await GetActiveReservedTotalAsync(productId);
                totalReserved.Should().BeLessThanOrEqualTo(StockAllocation,
                    "the system must NEVER over-allocate beyond the flash sale's stock allocation");

                // Each 200 OK persists exactly one 1-unit reservation, so successes == persisted units.
                successCount.Should().Be(totalReserved,
                    "each successful reserve persists exactly one single-unit reservation");
                successCount.Should().BeGreaterThan(0,
                    "under 500 attempts against 100 units at least one reserve must succeed");
                successCount.Should().BeLessThanOrEqualTo(StockAllocation);

                // Availability afterwards is consistent and never negative, observed through the same
                // non-cached GET the client relies on for live stock.
                using var getResp = await client.GetAsync("api/flash-sales/active");
                getResp.StatusCode.Should().Be(HttpStatusCode.OK);
                var activeSales = await getResp.Content.ReadFromJsonAsync<List<FlashSaleDto>>();
                var current = activeSales.Should().ContainSingle(s => s.Id == sale.Id,
                    "the created sale is still active and uniquely identifiable").Which;
                current.QuantityAvailable.Should().Be(StockAllocation - totalReserved,
                    "quantityAvailable == allocation - totalReserved");
                current.QuantityAvailable.Should().BeGreaterThanOrEqualTo(0);
            }
            finally
            {
                // Dispose every response, then remove this test's reservations + flash sale so the shared
                // per-class container is clean for the next test.
                foreach (var r in responses)
                {
                    r?.Dispose();
                }

                await CleanupReservationsAndSaleAsync(productId, sale.Id);
            }
        }

        /// <summary>
        /// Deterministic insufficient-stock contract. Against an active flash sale whose allocation is a
        /// single unit, a lone reserve for 5 units must be rejected with <c>409 Conflict</c> carrying the
        /// EXACT user-contract body <c>{"error":"INSUFFICIENT_STOCK","available":1}</c>, and NO partial
        /// reservation may be persisted. Uses a DISTINCT seeded product (<c>skip: 1</c>) so it is fully
        /// decoupled from the concurrent oversell fact regardless of xUnit's intra-class execution order.
        /// </summary>
        [Fact]
        public async Task Reserve_WhenRequestedQuantityExceedsAllocation_Returns409InsufficientStock()
        {
            // Arrange -----------------------------------------------------------------------------------
            // A DISTINCT seeded product (skip: 1) keeps this fact independent of the primary oversell fact.
            var productId = await GetSeededProductIdAsync(skip: 1);

            // A single-unit allocation makes the insufficient-stock branch deterministic for a qty-5 request.
            var sale = await CreateActiveFlashSaleAsync(productId, stockAllocation: 1);

            using var client = _fixture.CreateClient();
            await WarmUpActiveSalesAsync(client, sale.Id);

            // Act ---------------------------------------------------------------------------------------
            // One reserve for 5 units against a 1-unit allocation (distinct session id so the rate limiter
            // is irrelevant): requested (5) > available (1) => InsufficientStock, nothing persisted.
            using var response = await client.PostAsJsonAsync("api/inventory/reserve",
                BuildReserveDto(productId, Guid.NewGuid().ToString(), quantity: 5));
            try
            {
                // Assert --------------------------------------------------------------------------------
                response.StatusCode.Should().Be(HttpStatusCode.Conflict);

                // Exact user-contract body: {"error":"INSUFFICIENT_STOCK","available":1}. System.Text.Json's
                // camelCase name policy leaves the already-lowercase property NAMES and the string VALUE
                // verbatim, so both the "error" string and the "available" integer are asserted exactly.
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                body.GetProperty("error").GetString().Should().Be("INSUFFICIENT_STOCK");
                body.GetProperty("available").GetInt32().Should().Be(1);

                // No partial reservation may be persisted on the insufficient-stock path.
                (await GetActiveReservedTotalAsync(productId)).Should().Be(0,
                    "the insufficient-stock path must persist NOTHING (no partial reservation)");
            }
            finally
            {
                // Remove the created flash sale (and any reservations for this product) so the shared
                // per-class container is clean for the next test.
                await CleanupReservationsAndSaleAsync(productId, sale.Id);
            }
        }

        // -----------------------------------------------------------------------------------------------
        // Private helpers — keep each test independent and repeatable on the SHARED per-class database.
        // -----------------------------------------------------------------------------------------------

        /// <summary>
        /// Returns the identity id of a seeded product, read directly from the Store database through a fresh
        /// no-tracking scope. Deriving the id from the DB (rather than hardcoding a seed id) keeps the test
        /// robust to seed-ordering changes and guarantees a real product row exists. <paramref name="skip"/>
        /// lets the two facts select DISTINCT products so productId-scoped cleanup is surgical and the facts
        /// are decoupled (the seed provides 18 products, so both <c>skip: 0</c> and <c>skip: 1</c> resolve).
        /// </summary>
        /// <param name="skip">Number of ordered products to skip before selecting one (0 or 1 here).</param>
        /// <returns>A real seeded product identity id.</returns>
        private async Task<int> GetSeededProductIdAsync(int skip = 0)
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();

            var product = await ctx.Products.AsNoTracking().OrderBy(p => p.Id).Skip(skip).FirstAsync();
            return product.Id;
        }

        /// <summary>
        /// Schedules an ACTIVE flash sale for <paramref name="productId"/> via an <b>authenticated</b> client
        /// (<c>POST /api/flash-sales</c> is <c>[Authorize]</c>), over the window <c>[now-1min, now+1hr]</c> so
        /// the sale is active for the whole test. Asserts <c>200 OK</c> and returns the created
        /// <see cref="FlashSaleDto"/> (its <c>Id</c> is needed for cleanup and the availability cross-check).
        ///
        /// <para>
        /// <b>Server-side price rule.</b> The committed <c>FlashSaleService.ScheduleAsync</c> is a trust
        /// boundary that REJECTS a sale whose <c>SalePrice</c> is not strictly below the product's
        /// authoritative base price (<c>FlashSaleScheduleOutcome.SalePriceNotBelowBasePrice</c> → HTTP 400).
        /// A hardcoded price could exceed a low-priced seeded product (the seed's cheapest product is $8), so
        /// the sale price is DERIVED from the product's real base price — half of it, rounded to the mapped
        /// <c>decimal(18,2)</c> scale. That value is <c>&gt;= 0.01</c>, has at most two decimals, and is
        /// strictly below the base price for every seeded product, so the active-sale precondition is robust
        /// to the seed's actual prices. The sale price is display-only and is never charged (AAP §0.5.2); its
        /// exact value is irrelevant to the zero-oversell invariant under test.
        /// </para>
        /// </summary>
        /// <param name="productId">A REAL seeded product id (see <see cref="GetSeededProductIdAsync"/>).</param>
        /// <param name="stockAllocation">The flash sale's stock allocation (the oversell ceiling).</param>
        /// <returns>The persisted sale projected as a <see cref="FlashSaleDto"/>.</returns>
        private async Task<FlashSaleDto> CreateActiveFlashSaleAsync(int productId, int stockAllocation)
        {
            // Read the product's authoritative base price so the derived SalePrice is provably below it (the
            // service rejects SalePrice >= Price). A fresh no-tracking scope reflects committed DB state.
            decimal basePrice;
            using (var scope = _fixture.Factory.Services.CreateScope())
            {
                var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();
                var product = await ctx.Products.AsNoTracking().FirstAsync(p => p.Id == productId);
                basePrice = product.Price;
            }

            // Half the base price, rounded to decimal(18,2): >= 0.01, <= 2 decimals, and strictly < basePrice
            // for every seeded product (cheapest is $8 -> 4.00), satisfying the DTO Range/scale rules and the
            // service's SalePrice-below-base-price rule.
            var salePrice = decimal.Round(basePrice / 2m, 2);

            using var authClient = await _fixture.CreateAuthenticatedClientAsync();
            var dto = new CreateFlashSaleDto
            {
                ProductId = productId,
                StartAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                EndAt = DateTimeOffset.UtcNow.AddHours(1),
                SalePrice = salePrice,
                StockAllocation = stockAllocation
            };

            using var resp = await authClient.PostAsJsonAsync("api/flash-sales", dto);
            resp.StatusCode.Should().Be(HttpStatusCode.OK,
                "creating an ACTIVE flash sale is a required precondition (ReserveAsync loads the active sale by product)");

            var created = await resp.Content.ReadFromJsonAsync<FlashSaleDto>();
            created.Should().NotBeNull();
            created.StockAllocation.Should().Be(stockAllocation);
            created.QuantityAvailable.Should().Be(stockAllocation, "a brand-new sale has zero reservations");
            return created;
        }

        /// <summary>
        /// A bounded, asynchronous READINESS poll performed BEFORE the concurrent burst. It warms the
        /// Store-DB read path / connection pool AND confirms the created sale is visible and active through
        /// <c>GET /api/flash-sales/active</c> before the burst begins. It is non-mutating (a GET, so it never
        /// perturbs the allocation) and is a wait STRATEGY — never a fixed <c>Thread.Sleep</c> — using a
        /// brief async back-off between attempts.
        /// </summary>
        /// <param name="client">An HTTP client for the running application (anonymous is sufficient).</param>
        /// <param name="saleId">The scheduled sale's id that must be observable before the burst.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown if the sale is not observable within the bounded number of readiness attempts, so a setup
        /// problem surfaces at its source rather than as a confusing downstream burst failure.
        /// </exception>
        private static async Task WarmUpActiveSalesAsync(HttpClient client, int saleId)
        {
            const int maxAttempts = 15;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                using var response = await client.GetAsync("api/flash-sales/active");
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var sales = await response.Content.ReadFromJsonAsync<List<FlashSaleDto>>();
                    if (sales != null && sales.Any(s => s.Id == saleId))
                    {
                        return; // read path warm AND the sale is active/queryable — safe to start the burst
                    }
                }

                await Task.Delay(250); // brief async back-off between readiness polls; NOT a fixed sleep
            }

            throw new InvalidOperationException(
                $"The active flash sale {saleId} was not observable through GET /api/flash-sales/active within " +
                $"{maxAttempts} readiness attempts; the concurrency test cannot proceed.");
        }

        /// <summary>
        /// The CORE oversell measurement: the sum of ACTIVE (non-expired) reserved units for
        /// <paramref name="productId"/>, read from a FRESH no-tracking scope so it reflects committed DB
        /// state. Filtering to <c>ExpiresAt &gt; now</c> matches the service's availability formula; because
        /// the reservation TTL is 300s no hold expires mid-run, and this test performs no consume/release, so
        /// this sum also equals the total single-unit rows the test created for its (single) flash sale.
        /// </summary>
        /// <param name="productId">The product whose active reserved units are summed.</param>
        /// <returns>The sum of quantities of active, non-expired reservations for the product.</returns>
        private async Task<int> GetActiveReservedTotalAsync(int productId)
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();

            var now = DateTimeOffset.UtcNow;
            return await ctx.InventoryReservations.AsNoTracking()
                .Where(r => r.ProductId == productId && r.ExpiresAt > now)
                .SumAsync(r => r.Quantity);
        }

        /// <summary>
        /// Deterministic <c>finally</c> cleanup through a fresh scope: removes the reservations this test
        /// created (scoped by <paramref name="productId"/> — surgical because the two facts use distinct
        /// products) and the created <see cref="FlashSale"/>, so this class's shared container is restored
        /// for the next test.
        /// </summary>
        /// <param name="productId">The product whose reservations should be removed.</param>
        /// <param name="saleId">The flash sale to remove.</param>
        private async Task CleanupReservationsAndSaleAsync(int productId, int saleId)
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();

            var reservations = await ctx.InventoryReservations.Where(r => r.ProductId == productId).ToListAsync();
            if (reservations.Count > 0)
            {
                ctx.InventoryReservations.RemoveRange(reservations);
            }

            var sale = await ctx.FlashSales.FirstOrDefaultAsync(s => s.Id == saleId);
            if (sale != null)
            {
                ctx.FlashSales.Remove(sale);
            }

            await ctx.SaveChangesAsync();
        }

        /// <summary>
        /// Small factory building a valid <see cref="ReserveInventoryDto"/>. <paramref name="sessionId"/> is a
        /// canonical basket-style UUID (<c>Guid.NewGuid().ToString()</c>), which satisfies the DTO's
        /// <c>[CanonicalUuidV4]</c> validation and the rate limiter's canonical-UUID key.
        /// </summary>
        /// <param name="productId">The product to reserve against.</param>
        /// <param name="sessionId">The reservation session id (a canonical v4 UUID).</param>
        /// <param name="quantity">Units to reserve; defaults to 1 for the single-unit concurrency burst.</param>
        /// <returns>A populated <see cref="ReserveInventoryDto"/>.</returns>
        private static ReserveInventoryDto BuildReserveDto(int productId, string sessionId, int quantity = 1)
        {
            return new ReserveInventoryDto
            {
                ProductId = productId,
                Quantity = quantity,
                SessionId = sessionId
            };
        }
    }
}

