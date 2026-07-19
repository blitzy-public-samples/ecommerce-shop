using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;                          // Barrier — genuine concurrent "starting gun" (CR-02)
using System.Threading.Tasks;
using API.Dtos;
using API.IntegrationTests.Infrastructure;
using Core.Entities;
using Core.Entities.OrderAggregate;
using FluentAssertions;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;                       // IConnectionMultiplexer — MJ-04 Redis key cleanup
using Xunit;

namespace API.IntegrationTests.Concurrency
{
    /// <summary>
    /// End-to-end <b>integration</b> tests for <c>Infrastructure.Services.OrderService.CreateOrderAsync</c>,
    /// driven through the <b>real</b> ASP.NET Core HTTP pipeline (<c>POST api/orders</c>) against <b>real</b>
    /// PostgreSQL + Redis provisioned by Testcontainers (via this class's dedicated <see cref="ContainerFixture"/>).
    /// The two behaviours under verification are the ones with the greatest financial blast radius, so they
    /// carry the highest coverage priority (AAP §0.1.4, §0.10.3):
    ///
    /// <list type="bullet">
    ///   <item>
    ///     <b>Single-order guarantee (stale-order deletion).</b> <c>CreateOrderAsync</c> builds an
    ///     <c>OrderByPaymentIntentIdSpecification</c> from <c>basket.PaymentIntentId</c>; when an
    ///     order with that same payment intent already exists it is DELETED before the replacement is
    ///     inserted. Done <b>sequentially</b>, reusing the same basket + <c>PaymentIntentId</c> therefore
    ///     always leaves <b>exactly one</b> persisted order.
    ///   </item>
    ///   <item>
    ///     <b>Server-side price authority.</b> For every basket item the service loads the product from the
    ///     database (<c>_unitOfWork.Repository&lt;Product&gt;().GetByIdAsync(item.Id)</c>) and builds the
    ///     <see cref="OrderItem"/> from <c>productItem.Price</c> — the DB price — <b>ignoring</b> the
    ///     client-supplied basket price. A tampered client price must therefore never influence the
    ///     persisted order total.
    ///   </item>
    /// </list>
    ///
    /// <para>
    /// <b>Real infrastructure only (AAP §0.10.1).</b> Nothing here is mocked or stubbed by this class:
    /// PostgreSQL, Redis and the HTTP transport are all genuine (supplied by the Testcontainers-backed
    /// <see cref="ContainerFixture"/>). The only test double in play is the harness-wired offline
    /// <c>StripePaymentServiceStub</c> (registered by <c>CustomWebApplicationFactory</c>), which satisfies
    /// the "no live Stripe" constraint; when the stale-order branch calls
    /// <c>_paymentService.CreateOrUpdatePaymentIntent</c> the stub returns an in-memory basket and the
    /// service discards the result, so it has no effect on any assertion here.
    /// </para>
    ///
    /// <para>
    /// <b>Per-class isolation + deterministic cleanup.</b> Under CR-01 this class owns its own isolated,
    /// disposable PostgreSQL/Redis pair (an <c>IClassFixture&lt;ContainerFixture&gt;</c>). Within the class
    /// every test still uses a fresh GUID-based <c>basketId</c> and <c>paymentIntentId</c>, asserts DB state
    /// through its own short-lived <see cref="StoreContext"/> scope with <c>AsNoTracking()</c>, and restores
    /// state in a <c>finally</c> block (MJ-04 deterministic cleanup of the orders it persists and the basket
    /// key it writes). There are no fixed delays anywhere — readiness is guaranteed by the fixture's
    /// Testcontainers wait strategies and all work is awaited directly (AAP §0.10.1, §0.7.2).
    /// </para>
    /// </summary>
    public class OrderConcurrencyTests : IClassFixture<ContainerFixture>
    {
        /// <summary>
        /// Number of duplicate submissions fired simultaneously by the concurrency test. Deliberately
        /// <b>2</b> (matching the test name): with a fresh <c>PaymentIntentId</c> and exactly two callers,
        /// at most ONE caller can ever observe an already-committed order and take the delete branch — the
        /// other caller must have committed first — so the delete always targets a row that still exists.
        /// A double-delete race (which would raise a <c>DbUpdateConcurrencyException</c> → HTTP 500) is thus
        /// impossible, and because <c>Order.PaymentId</c> has no unique index a both-insert outcome is also
        /// valid. This keeps the concurrency test deterministic (no 500; 1 OR 2 orders) and honours the
        /// zero-flakiness mandate (AAP §0.7.2).
        /// </summary>
        private const int ConcurrentSubmissionCount = 2;

        private readonly ContainerFixture _fixture;

        /// <summary>
        /// xUnit injects this class's dedicated <see cref="ContainerFixture"/> (an
        /// <c>IClassFixture&lt;ContainerFixture&gt;</c> started once for THIS class) that exposes the wired
        /// <c>CustomWebApplicationFactory</c> and HTTP-client factory methods this class drives.
        /// </summary>
        /// <param name="fixture">This class's isolated, already-initialised container fixture.</param>
        public OrderConcurrencyTests(ContainerFixture fixture) => _fixture = fixture;

        // -----------------------------------------------------------------------------------------------
        // Tests
        // -----------------------------------------------------------------------------------------------

        /// <summary>
        /// PRIMARY, deterministic single-order guarantee. Submitting a second order that reuses the same
        /// <c>PaymentIntentId</c> (sequentially) must delete the stale first order and leave EXACTLY ONE
        /// order persisted for that payment intent.
        /// </summary>
        [Fact]
        public async Task CreateOrder_WhenSecondOrderReusesSamePaymentIntentId_PersistsExactlyOneOrder()
        {
            // Arrange -----------------------------------------------------------------------------------
            // Unique ids so this test is isolated from every other test on the shared database.
            var basketId = $"order-concurrency-{Guid.NewGuid():N}";
            var paymentIntentId = $"pi_test_{Guid.NewGuid():N}";

            // Use a real seeded product id/price so OrderService.GetByIdAsync(item.Id) never returns null.
            var (productId, _) = await GetSeededProductAsync();

            // clientPrice here is irrelevant to the guarantee; it only needs to satisfy [Range(0.1, ...)].
            await SeedBasketAsync(BuildBasketDto(basketId, paymentIntentId, productId, clientPrice: 1m, quantity: 2));

            var orderDto = BuildOrderDto(basketId);

            // POST api/orders is [Authorize] → use a Bearer-authenticated client for the seeded user.
            using var client = await _fixture.CreateAuthenticatedClientAsync();

            // Act ---------------------------------------------------------------------------------------
            // Sequential submissions. Creating an order does NOT consume/delete the basket, so the same
            // basket (and thus the same PaymentIntentId) is reused for the second submission.
            using var first = await client.PostAsJsonAsync("api/orders", orderDto);
            using var second = await client.PostAsJsonAsync("api/orders", orderDto);

            try
            {
                // Assert --------------------------------------------------------------------------------
                first.StatusCode.Should().Be(HttpStatusCode.OK);
                second.StatusCode.Should().Be(HttpStatusCode.OK);

                // The second CreateOrderAsync found the first order (same PaymentIntentId), deleted it, and
                // inserted its replacement — so exactly one order remains for this payment intent.
                (await CountOrdersByPaymentIntentIdAsync(paymentIntentId)).Should().Be(1);
            }
            finally
            {
                // MJ-04 deterministic cleanup: remove the persisted order and the basket key this test wrote.
                await CleanupOrdersByPaymentIntentIdAsync(paymentIntentId);
                await CleanupBasketAsync(basketId);
            }
        }

        /// <summary>
        /// PRIMARY, deterministic server-side price authority. Even when the client submits a deliberately
        /// wrong (tampered) basket price, the persisted order must use the authoritative database product
        /// price for both the line item and the computed subtotal.
        /// </summary>
        [Fact]
        public async Task CreateOrder_WithManipulatedClientPrice_UsesServerSideProductPrice()
        {
            // Arrange -----------------------------------------------------------------------------------
            var basketId = $"order-concurrency-{Guid.NewGuid():N}";
            var paymentIntentId = $"pi_test_{Guid.NewGuid():N}";

            var (productId, productPrice) = await GetSeededProductAsync();

            // Deliberately WRONG client price. Every seeded product price is >= 8, so 0.1 (the minimum the
            // [Range(0.1, ...)] validation allows) is unambiguously different from the real price.
            const decimal manipulatedClientPrice = 0.1m;
            const int quantity = 3;

            await SeedBasketAsync(
                BuildBasketDto(basketId, paymentIntentId, productId, clientPrice: manipulatedClientPrice, quantity: quantity));

            var orderDto = BuildOrderDto(basketId);
            using var client = await _fixture.CreateAuthenticatedClientAsync();

            // Act ---------------------------------------------------------------------------------------
            using var resp = await client.PostAsJsonAsync("api/orders", orderDto);

            try
            {
                resp.StatusCode.Should().Be(HttpStatusCode.OK);

                // Assert --------------------------------------------------------------------------------
                // Read the persisted order (with its items) back from PostgreSQL through a fresh, no-tracking
                // scope so the assertion reflects what was actually committed, not any in-request state.
                using var scope = _fixture.Factory.Services.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();

                var order = await ctx.Orders.AsNoTracking()
                    .Include(o => o.OrderItems)
                    .SingleAsync(o => o.PaymentId == paymentIntentId);

                // The line item price equals the DB product price (productPrice, >= 8) and NOT the tampered
                // client price (0.1m) — proving OrderService built the OrderItem from productItem.Price and
                // ignored the client-supplied basket price (server-side price authority).
                order.OrderItems.Should().OnlyContain(oi => oi.Price == productPrice);

                // Subtotal = server price * quantity (NOT the manipulated client price * quantity).
                order.Subtotal.Should().Be(productPrice * quantity);
            }
            finally
            {
                // MJ-04 deterministic cleanup: remove the persisted order and the basket key this test wrote.
                await CleanupOrdersByPaymentIntentIdAsync(paymentIntentId);
                await CleanupBasketAsync(basketId);
            }
        }

        /// <summary>
        /// SECONDARY consistency-under-contention check that HONESTLY reproduces the documented duplicate-
        /// order race (CR-02). Firing <see cref="ConcurrentSubmissionCount"/> duplicate submissions with a
        /// genuinely synchronized start (a <see cref="Barrier"/> "starting gun"; see the Act section) must
        /// never surface an unhandled server error (HTTP 500) and must leave the database in a consistent
        /// state.
        ///
        /// <para>
        /// <b>Why this does NOT assert "exactly one".</b> <c>CreateOrderAsync</c>'s stale-order
        /// check → delete → re-create sequence is a classic Time-Of-Check-To-Time-Of-Use (TOCTOU) window: it
        /// is not wrapped in a database lock/transaction and <c>Order.PaymentId</c> has no unique index, so
        /// under TRUE concurrency both callers can miss the (initially absent) order and both insert —
        /// yielding 1 OR 2 persisted orders depending on timing (the review reproduced two inserts / zero
        /// deletes). This test therefore <b>surfaces the divergence the QA review flagged (dest GAP-1)</b>
        /// between the AAP §0.4.2 integration blueprint — <i>"stale-order concurrency (duplicate
        /// PaymentIntentId → exactly one order)"</i> — and the code's ACTUAL behavior under genuine
        /// concurrency. The root cause is the unguarded check → delete → insert sequence in
        /// <c>OrderService.CreateOrderAsync</c> combined with the absent unique index on
        /// <c>Order.PaymentId</c>; closing the gap so exactly-one holds even under concurrency is a
        /// PRODUCTION idempotency fix that is out of scope here and is escalated to the resolution report
        /// rather than silently absorbed. Guaranteeing exactly-one <i>even under concurrency</i> would require a PRODUCTION
        /// idempotency change — a unique constraint on <c>PaymentId</c> and/or a transactional
        /// check-and-insert in <c>OrderService</c>. That production change is deliberately
        /// <b>DEFERRED / separately authorized</b>: this is a test-only engagement whose production code,
        /// controllers and services are frozen (AAP §0.8.2 "Any production fix for discovered races ... is
        /// out of scope"; AAP §0.10.1). This test therefore asserts ONLY the invariants that are truthful
        /// without that change (no 500; every response 200; between 1 and
        /// <see cref="ConcurrentSubmissionCount"/> orders), never a false/flaky "exactly one". The
        /// authoritative exactly-one guarantee for the SEQUENTIAL delete-then-recreate path is proven by
        /// <see cref="CreateOrder_WhenSecondOrderReusesSamePaymentIntentId_PersistsExactlyOneOrder"/>
        /// (AAP §0.7.2 zero-flakiness).
        /// </para>
        /// </summary>
        [Fact]
        public async Task CreateOrder_TwoConcurrentSubmissionsSamePaymentIntentId_NoServerErrorAndStateConsistent()
        {
            // Arrange -----------------------------------------------------------------------------------
            var basketId = $"order-concurrency-{Guid.NewGuid():N}";
            var paymentIntentId = $"pi_test_{Guid.NewGuid():N}";

            var (productId, _) = await GetSeededProductAsync();

            await SeedBasketAsync(BuildBasketDto(basketId, paymentIntentId, productId, clientPrice: 1m, quantity: 2));

            var orderDto = BuildOrderDto(basketId);

            // A single HttpClient is safe for concurrent requests; it is disposed after all tasks complete.
            using var client = await _fixture.CreateAuthenticatedClientAsync();

            // Act ---------------------------------------------------------------------------------------
            // Genuinely synchronize the start (CR-02): rather than relying on eager LINQ enumeration to
            // launch the requests "roughly together", every submission runs on its own thread-pool thread
            // and rendezvouses at a Barrier "starting gun", so all callers are released to issue their POST
            // at the same instant. This makes the stale-order check->delete->insert sequences genuinely
            // overlap (a real TOCTOU race) rather than incidentally serializing. The Barrier is a
            // synchronization primitive, not a fixed Thread.Sleep, so there is still no fixed delay anywhere.
            using var startingGun = new Barrier(ConcurrentSubmissionCount);
            var submissions = Enumerable.Range(0, ConcurrentSubmissionCount)
                .Select(_ => Task.Run(async () =>
                {
                    startingGun.SignalAndWait();
                    return await client.PostAsJsonAsync("api/orders", orderDto);
                }))
                .ToList();

            var responses = await Task.WhenAll(submissions);

            try
            {
                // Assert --------------------------------------------------------------------------------
                // No submission surfaced an unhandled exception as a 500; each completed successfully.
                responses.Should().NotContain(r => r.StatusCode == HttpStatusCode.InternalServerError,
                    "the TOCTOU duplicate-order race must degrade gracefully, never into an unhandled 500");
                responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK,
                    "with a fresh PaymentIntentId and two callers no delete can ever target an " +
                    "already-deleted row, and PaymentId has no unique index, so every concurrent " +
                    "submission returns 200 OK");

                // HONEST invariant (CR-02): between 1 (one caller observed the other's committed order and
                // took the delete-then-recreate branch) and ConcurrentSubmissionCount (both callers missed
                // the initially-absent order and both inserted duplicates). Never 0, never more than the
                // number of submissions. This deliberately tolerates the duplicate outcome the review
                // documented (two inserts / zero deletes) because the exactly-one PRODUCTION fix is deferred
                // (see this method's summary); the exactly-one oracle is the sequential test above.
                var count = await CountOrdersByPaymentIntentIdAsync(paymentIntentId);
                count.Should().BeInRange(1, ConcurrentSubmissionCount,
                    "under true concurrency without a production unique/transactional guard the stale-order " +
                    "path yields 1 or 2 orders; exactly-one is proven only for the sequential path");
            }
            finally
            {
                // Dispose each response message now that all tasks have been awaited.
                foreach (var response in responses)
                {
                    response.Dispose();
                }

                // MJ-04 deterministic cleanup: remove every order persisted for this payment intent and the
                // basket key this test wrote, restoring this class's own infrastructure between its tests.
                await CleanupOrdersByPaymentIntentIdAsync(paymentIntentId);
                await CleanupBasketAsync(basketId);
            }
        }

        // -----------------------------------------------------------------------------------------------
        // Private helpers — keep each test independent and repeatable on the SHARED database/cache.
        // -----------------------------------------------------------------------------------------------

        /// <summary>
        /// Returns the authoritative id and price of a seeded product, read directly from the Store
        /// database through a fresh no-tracking scope. Deriving these from the DB (rather than hardcoding a
        /// seed id/price) keeps the tests robust to seed-ordering changes and guarantees the basket item id
        /// is real, so <c>OrderService</c>'s <c>GetByIdAsync(item.Id)</c> never returns null (which would
        /// otherwise surface as an HTTP 500 via a NullReferenceException).
        /// </summary>
        /// <returns>A tuple of the product's identity id and its authoritative database price.</returns>
        private async Task<(int productId, decimal productPrice)> GetSeededProductAsync()
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();

            var product = await ctx.Products.AsNoTracking().OrderBy(p => p.Id).FirstAsync();
            return (product.Id, product.Price);
        }

        /// <summary>
        /// Builds a fully-populated <see cref="CustomerBasketDto"/> with a single line item. Every
        /// <see cref="BasketItemDto"/> field is <c>[Required]</c> (and <c>Price</c>/<c>Quantity</c> carry
        /// <c>[Range]</c> constraints), so all fields are set to valid values to avoid a 400 from the
        /// <c>[ApiController]</c> model validation on <c>POST api/basket</c>.
        /// </summary>
        /// <param name="basketId">Redis key / basket id (the order references this via <c>OrderDto.BasketId</c>).</param>
        /// <param name="paymentIntentId">
        /// Preserved through the AutoMapper <c>CustomerBasketDto → CustomerBasket</c> map and later copied
        /// onto <c>Order.PaymentId</c>; the single-order guarantee is keyed on this value.
        /// </param>
        /// <param name="productId">A REAL seeded product id (see <see cref="GetSeededProductAsync"/>).</param>
        /// <param name="clientPrice">The client-supplied price; must be &gt;= 0.1 to satisfy <c>[Range]</c>.</param>
        /// <param name="quantity">The client-supplied quantity; must be &gt;= 1 to satisfy <c>[Range]</c>.</param>
        private static CustomerBasketDto BuildBasketDto(
            string basketId, string paymentIntentId, int productId, decimal clientPrice, int quantity)
        {
            return new CustomerBasketDto
            {
                Id = basketId,
                PaymentIntentId = paymentIntentId, // preserved through AutoMapper CustomerBasketDto -> CustomerBasket
                Items = new List<BasketItemDto>
                {
                    new BasketItemDto
                    {
                        Id = productId,            // MUST be a real seeded product id (OrderService does GetByIdAsync(item.Id))
                        ProductName = "client-supplied-name",
                        Price = clientPrice,       // deliberately controllable client price; ignored by server-side authority
                        Quantity = quantity,
                        PictureUrl = "client-supplied.png",
                        Brand = "TestBrand",
                        Type = "TestType"
                    }
                }
            };
        }

        /// <summary>
        /// Builds a valid <see cref="OrderDto"/> with a fully-populated <see cref="AddressDto"/> (all
        /// address fields are <c>[Required]</c>). The default <paramref name="deliveryMethodId"/> of 1
        /// corresponds to the seeded "UPS1" delivery method.
        /// </summary>
        /// <param name="basketId">The basket to turn into an order (must already be seeded in Redis).</param>
        /// <param name="deliveryMethodId">Seeded delivery-method id; defaults to 1 ("UPS1").</param>
        private static OrderDto BuildOrderDto(string basketId, int deliveryMethodId = 1)
        {
            return new OrderDto
            {
                BasketId = basketId,
                DeliveryMethodId = deliveryMethodId,
                ShipToAddress = new AddressDto
                {
                    Id = 0,
                    FirstName = "Bob",
                    LastName = "Tester",
                    Street = "10 High St",
                    City = "Testville",
                    State = "TS",
                    ZipCode = "12345"
                }
            };
        }

        /// <summary>
        /// Seeds the basket in the real Redis instance by POSTing it through the anonymous client
        /// (<c>POST api/basket</c> is NOT <c>[Authorize]</c>). Asserts success so a setup failure is
        /// reported at its source rather than as a confusing downstream order failure.
        /// </summary>
        /// <param name="dto">The basket to persist.</param>
        private async Task SeedBasketAsync(CustomerBasketDto dto)
        {
            using var client = _fixture.CreateClient();
            using var resp = await client.PostAsJsonAsync("api/basket", dto);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        /// <summary>
        /// Counts the persisted orders whose <c>PaymentId</c> equals <paramref name="paymentIntentId"/>,
        /// through a FRESH no-tracking <see cref="StoreContext"/> scope so the read reflects committed
        /// database state with no stale change-tracking. (<c>Order.PaymentId</c> is set from
        /// <c>basket.PaymentIntentId</c>; the stale-order spec filters on the same column.)
        /// </summary>
        /// <param name="paymentIntentId">The payment intent id to match.</param>
        /// <returns>The number of persisted orders for that payment intent.</returns>
        private async Task<int> CountOrdersByPaymentIntentIdAsync(string paymentIntentId)
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();

            return await ctx.Orders.AsNoTracking().CountAsync(o => o.PaymentId == paymentIntentId);
        }

        /// <summary>
        /// MJ-04 deterministic cleanup: removes every order whose <c>PaymentId</c> equals
        /// <paramref name="paymentIntentId"/> (and, via the <c>onDelete: Cascade</c>
        /// <c>FK_OrderItems_Orders_OrderId</c> constraint, their order items) through a fresh scope, so this
        /// class's own database is restored between its sequentially-run tests regardless of how many
        /// duplicate orders the concurrent test happened to persist.
        /// </summary>
        /// <param name="paymentIntentId">The payment intent whose persisted orders should be removed.</param>
        private async Task CleanupOrdersByPaymentIntentIdAsync(string paymentIntentId)
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();

            var orders = await ctx.Orders.Where(o => o.PaymentId == paymentIntentId).ToListAsync();
            if (orders.Count == 0)
            {
                return;
            }

            ctx.Orders.RemoveRange(orders);
            await ctx.SaveChangesAsync();
        }

        /// <summary>
        /// MJ-04 deterministic cleanup: deletes the basket key this test wrote from the real Redis instance
        /// (the basket id IS the Redis key) through the application's own
        /// <see cref="IConnectionMultiplexer"/>, so no key lingers in this class's cache between its tests.
        /// </summary>
        /// <param name="basketId">The basket id / Redis key to delete.</param>
        private async Task CleanupBasketAsync(string basketId)
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var mux = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();

            await mux.GetDatabase().KeyDeleteAsync(basketId);
        }
    }
}
