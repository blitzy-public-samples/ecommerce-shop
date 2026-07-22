using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using API.IntegrationTests.Infrastructure;
using Core.Interfaces;
using FluentAssertions;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace API.IntegrationTests.Inventory
{
    /// <summary>
    /// End-to-end real-time stock-propagation integration test for the Real-Time Inventory &amp;
    /// Flash-Sale System (AAP §0.1.3 success criterion: <i>"Stock badge update visible to other clients
    /// within 2 s"</i>; §0.5.2 the SignalR/Redis layering bridge).
    ///
    /// <para>
    /// <b>What it proves.</b> A real .NET SignalR <see cref="HubConnection"/>
    /// (<c>Microsoft.AspNetCore.SignalR.Client 5.0.17</c>) connects to the anonymous hub at
    /// <c>/hubs/stock</c>, joins a product group via <c>SubscribeToProduct(productId)</c>, and — after a
    /// <b>genuine</b> stock mutation performed through <see cref="IInventoryService.CreateReservationAsync"/>
    /// — receives the resulting <c>StockChanged</c> broadcast for that product in <b>under 2000 ms</b>.
    /// This exercises the complete production path end to end: <c>InventoryService</c> atomically
    /// <c>DECR</c>s the Redis counter <c>stock:product:{id}</c> and <c>PUBLISH</c>es the camelCase payload
    /// <c>{ productId, currentStock, flashSaleId }</c> to the <c>stock-updates</c> channel; the API-layer
    /// <c>StockBroadcastBackgroundService</c> (running inside the in-process host) subscribes to that
    /// channel and rebroadcasts <c>StockChanged(productId, currentStock)</c> to the per-product SignalR
    /// group; and the subscribed <see cref="HubConnection"/> observes it. That Redis Pub/Sub hop is the
    /// architectural bridge that lets the Infrastructure layer trigger a real-time broadcast without ever
    /// referencing an API-layer type, preserving the <c>API → Infrastructure → Core</c> dependency chain.
    /// </para>
    ///
    /// <para>
    /// <b>Real infrastructure only (AAP §0.6, §0.7 testing convention).</b> The test reuses the existing
    /// <see cref="ContainerFixture"/> and <c>CustomWebApplicationFactory</c> exactly as-is — real
    /// Testcontainers PostgreSQL + Redis behind the in-memory <c>TestServer</c> host — with no new fixture,
    /// factory, container, or Stripe stub, and no SQLite/InMemory/mocks. A running Docker daemon is
    /// required.
    /// </para>
    ///
    /// <para>
    /// <b>Transport (critical).</b> The in-memory <c>TestServer</c> cannot negotiate WebSockets, so the
    /// connection is forced to <see cref="HttpTransportType.LongPolling"/> and routed through the server's
    /// in-memory handler via <c>options.HttpMessageHandlerFactory = _ =&gt; Factory.Server.CreateHandler()</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Determinism (no fixed delays).</b> There is no <c>Thread.Sleep</c> anywhere. The 2000 ms SLA is
    /// asserted with a <see cref="TaskCompletionSource{TResult}"/> raced against
    /// <c>Task.Delay(2000)</c> via <c>Task.WhenAny</c>, and the only other wait is a short, bounded drain
    /// loop for the initial subscribe event. Because <c>SubscribeToProduct</c> also pushes an INITIAL
    /// <c>StockChanged</c> (carrying the current stock <c>N</c>) to the caller, and a 30 s
    /// <c>StockReconciliationService</c> periodically reseeds/republishes, the completion signal is
    /// <b>value-gated</b>: it only fires when the received stock equals the post-mutation value
    /// (<c>N-1</c>). That makes a stray republish of the pre-mutation value (<c>N</c>) unable to satisfy
    /// the wait early. The client handler binds the stock argument as <see cref="long"/> so it accepts
    /// BOTH the hub's <c>int</c> caller-push and the bridge's <c>long</c> broadcast (JSON numbers widen
    /// cleanly).
    /// </para>
    ///
    /// <para>
    /// <b>Isolation.</b> The fixture does NOT seed stock, so the arrange step sets
    /// <c>Products.StockQuantity</c> to a known value and seeds the Redis counters. State is restored
    /// deterministically in a <c>finally</c> block: the connection is stopped and disposed, reservation
    /// rows for the product are deleted, the Redis counter key is removed, and <c>StockQuantity</c> is
    /// reset to its original value.
    /// </para>
    /// </summary>
    public class StockPropagationTests : IClassFixture<ContainerFixture>
    {
        private readonly ContainerFixture _fixture;

        /// <summary>
        /// xUnit injects this class's dedicated <see cref="ContainerFixture"/> (a per-class
        /// <c>IClassFixture&lt;ContainerFixture&gt;</c>, started once for THIS class), which exposes the
        /// wired <c>CustomWebApplicationFactory</c> pointing at the isolated Testcontainers
        /// PostgreSQL + Redis instances.
        /// </summary>
        /// <param name="fixture">This class's isolated, already-initialised container fixture.</param>
        public StockPropagationTests(ContainerFixture fixture)
        {
            _fixture = fixture;
        }

        /// <summary>
        /// Subscribes to a product over a real SignalR <see cref="HubConnection"/>, reserves one unit
        /// through <see cref="IInventoryService.CreateReservationAsync"/> (a genuine end-to-end stock
        /// mutation), and asserts the resulting <c>StockChanged</c> broadcast for that product is delivered
        /// to the subscribed client in under 2000 ms (AAP §0.1.3 / §0.5.2).
        /// </summary>
        [Fact]
        public async Task SubscribeToProduct_WhenStockMutated_ReceivesStockChangedWithinTwoSeconds()
        {
            // Arrange -----------------------------------------------------------------------------------
            const int initialStockValue = 5;
            const int expectedAfterMutation = initialStockValue - 1; // one unit reserved => N-1

            var productId = await GetFirstSeededProductIdAsync();
            var originalStock = await GetProductStockQuantityAsync(productId);

            // The fixture does NOT seed stock (all products start StockQuantity = 0 with unseeded
            // counters), so establish a known, positive baseline: set StockQuantity = 5, clear any stale
            // reservations for the product, then seed the Redis counter stock:product:{id} = 5.
            await SetProductStockQuantityAsync(productId, initialStockValue);
            await DeleteReservationsForProductAsync(productId);
            await SeedStockCountersAsync();

            var received = new ConcurrentQueue<(int productId, long stock)>();
            var mutationSignal =
                new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Build the HubConnection over LongPolling routed through the in-memory TestServer handler.
            // WebSockets are NOT supported by TestServer, so LongPolling via CreateHandler() is mandatory.
            var connection = new HubConnectionBuilder()
                .WithUrl("http://localhost/hubs/stock", options =>
                {
                    options.HttpMessageHandlerFactory = _ => _fixture.Factory.Server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                })
                .Build();

            // Register the handler BEFORE StartAsync so no event is missed. Bind the stock argument as
            // long to accept BOTH the hub's int caller-push and the bridge's long broadcast. Value-gate
            // the completion signal on the post-mutation value (N-1) so neither the initial subscribe
            // event (N) nor a background reconciliation republish (N) completes the wait prematurely.
            connection.On<int, long>("StockChanged", (pid, stock) =>
            {
                if (pid != productId)
                {
                    return;
                }

                received.Enqueue((pid, stock));

                if (stock == expectedAfterMutation)
                {
                    mutationSignal.TrySetResult(stock);
                }
            });

            try
            {
                // Act ---------------------------------------------------------------------------------
                await connection.StartAsync();

                // Join the per-product group and read the current stock. This ALSO fires the INITIAL
                // StockChanged (= initialStockValue) to the caller, which the value-gate ignores.
                var initialStock = await connection.InvokeAsync<int>("SubscribeToProduct", productId);
                initialStock.Should().Be(initialStockValue,
                    "SubscribeToProduct returns the current available stock seeded during arrange");

                // Drain the initial subscribe event (bounded, awaited — never a fixed Thread.Sleep) so the
                // mutation that follows is the event that satisfies the wait.
                var drainDeadline = DateTime.UtcNow.AddSeconds(2);
                while (received.IsEmpty && DateTime.UtcNow < drainDeadline)
                {
                    await Task.Delay(50);
                }

                // Trigger a REAL end-to-end mutation: reserve 1 unit -> InventoryService DECRs the counter
                // 5 -> 4 and PUBLISHes to stock-updates -> StockBroadcastBackgroundService rebroadcasts
                // StockChanged(= 4) to the product group. A fresh mut-{guid} basket is used so its Redis
                // hold key carries its own native TTL and expires on its own.
                var stopwatch = Stopwatch.StartNew();
                bool granted;
                using (var scope = _fixture.Factory.Services.CreateScope())
                {
                    var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
                    granted = await inventory.CreateReservationAsync(
                        $"mut-{Guid.NewGuid():N}", productId, 1);
                }

                granted.Should().BeTrue("stock 5 is sufficient to reserve 1 unit");

                var completed = await Task.WhenAny(mutationSignal.Task, Task.Delay(2000));
                stopwatch.Stop();

                // Assert ------------------------------------------------------------------------------
                completed.Should().Be(mutationSignal.Task,
                    "StockChanged must propagate to the subscribed client within 2000 ms (AAP §0.1.3)");
                mutationSignal.Task.IsCompletedSuccessfully.Should().BeTrue(
                    "the value-gated propagation signal must complete successfully, not time out");
                stopwatch.ElapsedMilliseconds.Should().BeLessThan(2000,
                    "the 2-second SLA is the propagation assertion itself");
                (await mutationSignal.Task).Should().Be(expectedAfterMutation,
                    "the broadcast must carry the post-mutation available stock (N-1)");
            }
            finally
            {
                // Deterministic cleanup (AAP §0.7): stop + dispose the connection, remove the reservation
                // rows and the Redis counter key for this product, and reset StockQuantity so the next
                // test starts from a clean slate. The mut-{guid} hold key expires via its own Redis TTL.
                await connection.StopAsync();
                await connection.DisposeAsync();

                await DeleteReservationsForProductAsync(productId);

                var redis = _fixture.Factory.Services.GetRequiredService<IConnectionMultiplexer>();
                var db = redis.GetDatabase();
                await db.KeyDeleteAsync($"stock:product:{productId}");

                await SetProductStockQuantityAsync(productId, originalStock);
            }
        }

        // -----------------------------------------------------------------------------------------------
        // Private helpers — fresh-scope reads/writes mirrored from OrderConcurrencyTests so each operation
        // reflects committed PostgreSQL state and keeps the test independent on the shared database.
        // -----------------------------------------------------------------------------------------------

        /// <summary>
        /// Returns the id of the first seeded product (lowest <c>Id</c>), read through a fresh no-tracking
        /// <see cref="StoreContext"/> scope. Deriving the id from the database (rather than hardcoding a
        /// seed id) keeps the test robust to seed-ordering changes.
        /// </summary>
        private async Task<int> GetFirstSeededProductIdAsync()
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();

            var product = await ctx.Products.AsNoTracking().OrderBy(p => p.Id).FirstAsync();
            return product.Id;
        }

        /// <summary>
        /// Reads the current <c>StockQuantity</c> for a product through a fresh no-tracking scope so the
        /// original value can be captured for restoration in the test's <c>finally</c> block.
        /// </summary>
        private async Task<int> GetProductStockQuantityAsync(int productId)
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();

            var product = await ctx.Products.AsNoTracking().FirstAsync(p => p.Id == productId);
            return product.StockQuantity;
        }

        /// <summary>
        /// Sets a product's authoritative <c>StockQuantity</c> in PostgreSQL through a fresh write scope.
        /// Used both to establish the positive baseline during arrange and to restore the original value
        /// during cleanup.
        /// </summary>
        private async Task SetProductStockQuantityAsync(int productId, int stockQuantity)
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();

            var product = await ctx.Products.FirstAsync(p => p.Id == productId);
            product.StockQuantity = stockQuantity;
            await ctx.SaveChangesAsync();
        }

        /// <summary>
        /// Removes every <c>Reservations</c> row for the given product through a fresh write scope, so a
        /// stale hold from a previous run cannot skew the available-stock computation.
        /// </summary>
        private async Task DeleteReservationsForProductAsync(int productId)
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();

            var reservations = await ctx.Reservations.Where(r => r.ProductId == productId).ToListAsync();
            if (reservations.Count == 0)
            {
                return;
            }

            ctx.Reservations.RemoveRange(reservations);
            await ctx.SaveChangesAsync();
        }

        /// <summary>
        /// Seeds the Redis stock counters (<c>stock:product:{id}</c>) from committed PostgreSQL stock via
        /// the application's own <see cref="IInventoryService"/>, mirroring the startup seeding so the hot
        /// path is warm before the connection subscribes.
        /// </summary>
        private async Task SeedStockCountersAsync()
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();

            await inventory.SeedStockCountersAsync();
        }
    }
}
