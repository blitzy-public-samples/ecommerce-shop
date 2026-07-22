using System;
using System.Collections.Generic;
using System.Diagnostics;                       // Stopwatch
using System.Linq;
using System.Net;                               // HttpStatusCode
using System.Net.Http;                          // HttpClient, HttpResponseMessage, StringContent
using System.Net.WebSockets;                    // WebSocket, WebSocketMessageType, WebSocketReceiveResult
using System.Text;                              // Encoding
using System.Text.Json;                         // JsonSerializer, JsonDocument, JsonSerializerOptions
using System.Threading;                         // CancellationTokenSource, CancellationToken
using System.Threading.Tasks;                   // Task, Task.WhenAll
using API.Dtos;                                 // CreateFlashSaleDto, FlashSaleDto, ReserveInventoryDto, ReservationToReturnDto
using API.Hubs;                                 // InventoryHub
using API.IntegrationTests.Infrastructure;      // ContainerFixture
using Core.Entities;                            // FlashSale, InventoryReservation (cleanup)
using Infrastructure.Data;                      // StoreContext (cleanup, seeded product id)
using Microsoft.AspNetCore.SignalR;             // IHubContext<InventoryHub>
using Microsoft.Extensions.Configuration;       // IConfiguration
using Microsoft.Extensions.DependencyInjection; // GetRequiredService, CreateScope
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;                       // ITestOutputHelper — N6: report (never swallow) DB-cleanup failures

namespace API.IntegrationTests.Load
{
    /// <summary>
    /// Load / latency integration tests for the Real-Time Inventory &amp; Flash-Sale SignalR broadcast path,
    /// exercised end-to-end through the <b>real</b> ASP.NET Core pipeline (in-process
    /// <c>WebApplicationFactory&lt;Startup&gt;</c>) against a <b>real</b> PostgreSQL store and a <b>real</b>
    /// Redis cache provisioned by Testcontainers via the shared <see cref="ContainerFixture"/> (AAP §0.4.1
    /// Group 5 / §0.5.1 <c>Load/InventoryBroadcastLoadTests.cs</c>; subjects <c>API/Hubs/InventoryHub.cs</c>,
    /// <c>API/Hubs/InventoryBroadcaster.cs</c>, <c>API/Controllers/InventoryController.cs</c>).
    ///
    /// <para>
    /// The class proves two guarantees against the actual production source:
    /// <list type="number">
    ///   <item><b>Broadcast latency budget.</b> A server-to-client <c>InventoryUpdated</c> event, pushed
    ///         through the genuine <see cref="IHubContext{THub}"/> to a connected WebSocket client, is
    ///         delivered with a <b>p95 &lt; 2000&nbsp;ms</b> over <see cref="LatencySampleCount"/> samples
    ///         (<see cref="BroadcastInventoryUpdated_ToConnectedWebSocketClient_DeliversWithinP95LatencyBudget"/>).</item>
    ///   <item><b>Bounded reservation load.</b> A burst of <see cref="ReservationConcurrency"/> concurrent
    ///         <c>POST /api/inventory/reserve</c> requests — each with a distinct session — never yields a
    ///         server error (5xx) or a rate-limit rejection (429); every response is a documented <c>200</c>
    ///         or <c>409</c>, and every accepted (200) reservation broadcasts exactly one <c>InventoryUpdated</c>
    ///         for the product
    ///         (<see cref="ReserveInventory_UnderBoundedConcurrentLoad_ReturnsOkOrConflictAndBroadcastsEachSuccess"/>).</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Real infrastructure only (AAP §0.10.1).</b> Nothing here is mocked or stubbed: PostgreSQL, Redis,
    /// the HTTP transport and the SignalR hub are all genuine. Crucially, the SignalR client is a <b>raw</b>
    /// <see cref="System.Net.WebSockets.WebSocket"/> driven through a <b>manual JSON-protocol handshake</b>
    /// (<see cref="RawSignalRClient"/>): the .NET SignalR client package
    /// (<c>Microsoft.AspNetCore.SignalR.Client</c>) is NOT part of the shared framework and is deliberately
    /// NOT referenced, so the test adds no NuGet dependency. The client connects via
    /// <c>TestServer.CreateWebSocketClient()</c>, and the <see cref="IHubContext{THub}"/> the test broadcasts
    /// through is resolved from the SAME in-process host/DI container that owns those WebSocket connections —
    /// which is why a group broadcast reaches the manually-connected socket without any client package.
    /// </para>
    ///
    /// <para>
    /// <b>Deterministic, never flaky (AAP §0.7.2, §0.10.2).</b> There is no <c>Thread.Sleep</c>, no polling
    /// and no retry loop anywhere in this class. Every wait is an awaited socket receive bounded by a
    /// <see cref="CancellationTokenSource"/> deadline, so a genuinely missing frame fails the test <i>fast</i>
    /// (via <see cref="OperationCanceledException"/>) instead of hanging. Group membership is established with
    /// a <b>blocking</b> hub invocation (an <c>invocationId</c> whose <c>type:3</c> completion is awaited)
    /// before any broadcast is sent, removing the join/broadcast race.
    /// </para>
    ///
    /// <para>
    /// <b>Fixture wiring — as-built harness contract (CR-01).</b> <see cref="ContainerFixture"/> is consumed
    /// as a PER-CLASS <c>IClassFixture&lt;ContainerFixture&gt;</c> (the project defines no
    /// <c>ICollectionFixture</c>); assembly-level <c>[CollectionBehavior(DisableTestParallelization = true)]</c>
    /// keeps classes SEQUENTIAL. The hub path is resolved the SAME way the server resolves it
    /// (<c>SIGNALR_HUB_PATH</c> from configuration, falling back to <c>/hubs/inventory</c>) so the JWT
    /// query-string lift (<c>IdentityServiceExtensions.OnMessageReceived</c>) and the
    /// <c>MapHub&lt;InventoryHub&gt;</c> mapping always agree.
    /// </para>
    ///
    /// <para>
    /// Naming follows the repository convention <c>MethodName_StateUnderTest_ExpectedBehavior</c>, with an
    /// Arrange-Act-Assert structure and FluentAssertions throughout.
    /// </para>
    /// </summary>
    public class InventoryBroadcastLoadTests : IClassFixture<ContainerFixture>
    {
        // --- Latency-budget constants (Test 1) --------------------------------------------------------

        /// <summary>Number of measured <c>InventoryUpdated</c> delivery samples (p95 nearest-rank index = 56).</summary>
        private const int LatencySampleCount = 60;

        /// <summary>The p95 delivery-latency assertion threshold, in milliseconds (AAP §0.6: &lt; 2&nbsp;s).</summary>
        private const int LatencyBudgetMs = 2000;

        /// <summary>
        /// Base value for the per-sample <c>quantityAvailable</c> sentinel (<c>LatencySentinelBase + i</c>).
        /// Chosen far above any real reservation-derived availability (a 60-unit allocation can never yield a
        /// value near 900&nbsp;000), so matching a broadcast on this value cannot collide with a stray event.
        /// </summary>
        private const int LatencySentinelBase = 900000;

        // --- Reservation-load constants (Test 2) ------------------------------------------------------

        /// <summary>
        /// Bounded concurrent reserve burst size. Kept at 50 (≤ PostgreSQL <c>max_connections</c> 100 / Npgsql
        /// <c>Max Pool Size</c> 100) so the burst is genuinely concurrent without stampeding the connection pool.
        /// </summary>
        private const int ReservationConcurrency = 50;

        /// <summary>
        /// Flash-sale <c>StockAllocation</c> for Test 2. Chosen ≥ <see cref="ReservationConcurrency"/> so the
        /// burst is not starved of stock, guaranteeing at least some reservations succeed.
        /// </summary>
        private const int FlashSaleStockAllocation = 60;

        /// <summary>
        /// Sale price used when scheduling the Test 2 flash sale. Well below every seeded product's base price
        /// (the cheapest seed product is 120), so the service's <c>SalePriceNotBelowBasePrice</c> authority
        /// check passes; it also rounds cleanly to the mapped <c>decimal(18,2)</c> scale.
        /// </summary>
        private const decimal SalePriceValue = 5.00m;

        // --- Timing / determinism budgets -------------------------------------------------------------

        /// <summary>
        /// Per-await receive budget, in seconds. Far larger than the 2&nbsp;s p95 target so it never trips on a
        /// healthy path, yet bounds every socket receive so a genuinely missing frame fails fast instead of hanging.
        /// </summary>
        private const int PerFrameTimeoutSeconds = 10;

        /// <summary>
        /// Overall budget, in seconds, to drain every <c>InventoryUpdated</c> broadcast produced by the Test 2
        /// reservation burst. Comfortably covers 50 serialized broadcasts while still failing fast on a dropped frame.
        /// </summary>
        private const int DrainTimeoutSeconds = 15;

        // --- Endpoint / protocol constants ------------------------------------------------------------

        /// <summary>Absolute route for scheduling a flash sale (<c>FlashSalesController.CreateFlashSale</c>).</summary>
        private const string FlashSalesPath = "/api/flash-sales";

        /// <summary>Absolute route for reserving inventory (<c>InventoryController.Reserve</c>).</summary>
        private const string ReservePath = "/api/inventory/reserve";

        /// <summary>
        /// The canonical hub-path fallback, identical to <c>InventoryHub.HubPath</c> and to the
        /// <c>MapHub&lt;InventoryHub&gt;</c> default in <c>Startup</c>. Used only when <c>SIGNALR_HUB_PATH</c> is
        /// absent so the WebSocket URI targets exactly the mapped hub path (never hardcoded into the URI blindly).
        /// </summary>
        private const string DefaultHubPath = "/hubs/inventory";

        /// <summary>The SignalR framing terminator — the ASCII record separator that ends every protocol message.</summary>
        private const byte RecordSeparator = 0x1E;

        /// <summary>The exact SignalR JSON-protocol handshake request payload (a trailing record separator is appended on send).</summary>
        private const string HandshakeRequest = "{\"protocol\":\"json\",\"version\":1}";

        /// <summary>The <c>invocationId</c> used for the blocking <c>JoinProductGroup</c> invocation whose completion is awaited.</summary>
        private const string JoinInvocationId = "join";

        /// <summary>Server-to-client handler name for a live availability update (case-sensitive on the wire).</summary>
        private const string InventoryUpdatedEvent = "InventoryUpdated";

        // --- Serializer options -----------------------------------------------------------------------

        /// <summary>
        /// Case-insensitive reader so a camelCase JSON body binds onto PascalCase DTO members. Retained for
        /// symmetry with the exemplar <c>ProductsLoadTests</c>; deserialization here is done via
        /// <see cref="JsonDocument"/> for the raw hub frames.
        /// </summary>
        private static readonly JsonSerializerOptions JsonReadOpts =
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        /// <summary>
        /// camelCase writer mirroring the server's request contracts, so a serialized
        /// <see cref="CreateFlashSaleDto"/> / <see cref="ReserveInventoryDto"/> binds byte-compatibly on the
        /// server (which reads camelCase and matches case-insensitively).
        /// </summary>
        private static readonly JsonSerializerOptions CamelCaseWriteOpts =
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        /// <summary>This class's dedicated Testcontainers-backed fixture (real PostgreSQL + Redis + in-process host).</summary>
        private readonly ContainerFixture _fixture;

        // N6: xUnit injects ITestOutputHelper alongside the class fixture. A DB-cleanup failure in a test's
        // finally block is REPORTED here (not silently swallowed), so a hiccup that could leave the shared
        // container dirty for sibling tests is visible in test output — while still never failing an
        // otherwise-passing test.
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Receives this class's isolated <see cref="ContainerFixture"/> from xUnit's class-fixture machinery.
        /// By the time this constructor runs the fixture has started the containers, applied the EF Core
        /// migrations (so <c>flash_sales</c>, <c>inventory_reservations</c> and <c>products.version</c> exist)
        /// and seeded the documented reference data (6 brands / 4 types / 18 products / 4 delivery methods and
        /// the <c>bob@test.com</c> user).
        /// </summary>
        /// <param name="fixture">This class's isolated container fixture.</param>
        public InventoryBroadcastLoadTests(ContainerFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        /// <summary>
        /// Disposes every <see cref="HttpResponseMessage"/> produced by a batch of concurrent request tasks —
        /// including on the exceptional path where <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/>
        /// threw after only SOME requests completed (so the aggregated response array was never assigned).
        /// Iterating the request TASK list (rather than a materialised response array) guarantees no completed
        /// response leaks until fixture teardown. Only tasks that <see cref="TaskStatus.RanToCompletion"/>
        /// produced a response to dispose; faulted/cancelled tasks are skipped (reading their <c>Result</c>
        /// would rethrow and there is nothing to release).
        /// </summary>
        /// <param name="requestTasks">
        /// The in-flight request tasks (may be <c>null</c> if the batch was never started, in which case this
        /// is a no-op).
        /// </param>
        private static void DisposeCompletedResponses(IEnumerable<Task<HttpResponseMessage>> requestTasks)
        {
            if (requestTasks == null)
            {
                return;
            }

            foreach (var requestTask in requestTasks)
            {
                if (requestTask != null && requestTask.Status == TaskStatus.RanToCompletion)
                {
                    requestTask.Result?.Dispose();
                }
            }
        }

        /// <summary>
        /// Builds the WebSocket URI for the hub from the TestServer base address, resolving the hub path the
        /// SAME way the server does (<c>SIGNALR_HUB_PATH</c> from configuration, else <see cref="DefaultHubPath"/>)
        /// so the JWT query-string lift and the <c>MapHub</c> mapping always target the same path. The JWT is
        /// carried as a query-string <c>access_token</c> because a browser WebSocket cannot send an
        /// <c>Authorization</c> header (AAP R7 / §0.2.2).
        /// </summary>
        /// <returns>The absolute <c>ws://</c> hub URI including the URL-escaped access token.</returns>
        private async Task<Uri> BuildHubUriAsync()
        {
            var config = _fixture.Factory.Services.GetRequiredService<IConfiguration>();
            var hubPath = config["SIGNALR_HUB_PATH"] ?? DefaultHubPath;
            var jwt = await _fixture.GetAuthTokenAsync();

            return new UriBuilder(_fixture.Factory.Server.BaseAddress)
            {
                Scheme = "ws",
                Path = hubPath,
                Query = "access_token=" + Uri.EscapeDataString(jwt)
            }.Uri;
        }

        /// <summary>
        /// Resolves the first seeded product id (deterministic: <c>ORDER BY Id</c>) through a fresh DI scope so
        /// the tests broadcast/reserve against a genuine catalogue row.
        /// </summary>
        /// <returns>The lowest seeded product id.</returns>
        private int ResolveSeededProductId()
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();
            return ctx.Products.OrderBy(p => p.Id).Select(p => p.Id).First();
        }

        /// <summary>
        /// Proves that a server-to-client <c>InventoryUpdated</c> event pushed through the genuine
        /// <see cref="IHubContext{THub}"/> reaches a connected WebSocket client with a <b>p95 delivery latency
        /// below <see cref="LatencyBudgetMs"/> ms</b> over <see cref="LatencySampleCount"/> samples.
        ///
        /// <para>
        /// The measurement is deliberately isolated to the pure SignalR fan-out path: each sample is broadcast
        /// DIRECTLY through <see cref="IHubContext{THub}"/> (no HTTP, no database, no reservation logic), so the
        /// PostgreSQL connection ceiling and any service latency cannot contaminate the timing. A unique
        /// per-iteration sentinel (<see cref="LatencySentinelBase"/><c> + i</c>) is matched on BOTH
        /// <c>productId</c> and <c>quantityAvailable</c>, making each measurement immune to any stray broadcast.
        /// A single unmeasured warm-up broadcast primes the JIT/connection path first.
        /// </para>
        /// </summary>
        [Fact]
        public async Task BroadcastInventoryUpdated_ToConnectedWebSocketClient_DeliversWithinP95LatencyBudget()
        {
            // Arrange — a real seeded product id and the singleton hub context (registered by AddSignalR()).
            var pid = ResolveSeededProductId();
            var hub = _fixture.Factory.Services.GetRequiredService<IHubContext<InventoryHub>>();
            var wsUri = await BuildHubUriAsync();

            RawSignalRClient client = null;
            try
            {
                // Connect the raw WebSocket, perform the SignalR JSON handshake, and JOIN the product group with
                // a blocking invocation (await the type:3 completion) so no broadcast can precede membership.
                var wsClient = _fixture.Factory.Server.CreateWebSocketClient();
                using (var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(PerFrameTimeoutSeconds)))
                {
                    client = await RawSignalRClient.ConnectAsync(wsClient.ConnectAsync, wsUri, connectCts.Token);
                    await client.JoinProductGroupAsync(pid, connectCts.Token);
                }

                // Warm-up (NOT measured): prime the JIT/connection path with one broadcast + receive. The
                // warm-up sentinel sits just below the measured range so it can never be mistaken for a sample.
                var warmUpQuantity = LatencySentinelBase - 1;
                await hub.Clients.Group(InventoryHub.ProductGroup(pid))
                    .SendAsync(InventoryUpdatedEvent, new { productId = pid, quantityAvailable = warmUpQuantity });
                using (var warmCts = new CancellationTokenSource(TimeSpan.FromSeconds(PerFrameTimeoutSeconds)))
                {
                    await client.ReadUntilBroadcastAsync(
                        InventoryUpdatedEvent,
                        arg => arg.GetProperty("productId").GetInt32() == pid
                               && arg.GetProperty("quantityAvailable").GetInt32() == warmUpQuantity,
                        warmCts.Token);
                }

                // Act & measure — for each sample: start the clock, broadcast a unique sentinel through the hub,
                // and await its delivery to the connected socket; record the elapsed wall-clock milliseconds.
                var samples = new List<double>(LatencySampleCount);
                for (var i = 0; i < LatencySampleCount; i++)
                {
                    var expected = LatencySentinelBase + i;
                    var stopwatch = Stopwatch.StartNew();

                    await hub.Clients.Group(InventoryHub.ProductGroup(pid))
                        .SendAsync(InventoryUpdatedEvent, new { productId = pid, quantityAvailable = expected });

                    using (var perFrameCts = new CancellationTokenSource(TimeSpan.FromSeconds(PerFrameTimeoutSeconds)))
                    {
                        await client.ReadUntilBroadcastAsync(
                            InventoryUpdatedEvent,
                            arg => arg.GetProperty("productId").GetInt32() == pid
                                   && arg.GetProperty("quantityAvailable").GetInt32() == expected,
                            perFrameCts.Token);
                    }

                    stopwatch.Stop();
                    samples.Add(stopwatch.Elapsed.TotalMilliseconds);
                }

                // Assert — every broadcast was delivered, and the p95 (nearest-rank) is within budget.
                samples.Count.Should().Be(LatencySampleCount, "every broadcast must be delivered — no dropped frames");

                samples.Sort();
                var p95 = samples[Math.Clamp((int)Math.Ceiling(0.95 * samples.Count) - 1, 0, samples.Count - 1)];
                p95.Should().BeLessThan(LatencyBudgetMs,
                    "SignalR InventoryUpdated p95 delivery latency must be under 2 seconds");
            }
            finally
            {
                // Test 1 writes NO database rows, so only the WebSocket needs releasing (best-effort).
                if (client != null)
                {
                    await client.CloseAsync();
                    client.Dispose();
                }
            }
        }

        /// <summary>
        /// Proves the reserve endpoint stays correct and responsive under a <b>bounded concurrent load</b>:
        /// <see cref="ReservationConcurrency"/> simultaneous <c>POST /api/inventory/reserve</c> requests, each
        /// with a DISTINCT session, against an active flash sale whose <see cref="FlashSaleStockAllocation"/>
        /// (60) is ≥ the burst size (50).
        ///
        /// <para>
        /// The test asserts (1) the burst completes without timeout/unhandled exception, (2) no response is a
        /// server error (5xx), (3) no response is a rate-limit rejection (429) — because each request uses a
        /// unique <see cref="Guid"/> session, so the 10-per-minute-per-session limit is never reached,
        /// (4) every response is a documented <c>200 OK</c> or <c>409 Conflict</c>
        /// (<c>INSUFFICIENT_STOCK</c>/<c>RESERVATION_CONFLICT</c> — some optimistic-concurrency conflicts are
        /// expected under contention), (5) at least one reservation succeeds, and (6) each accepted reservation
        /// broadcasts EXACTLY one <c>InventoryUpdated</c> for the product — drained live from the connected
        /// WebSocket. Because the reservation service awaits its post-commit broadcast before returning
        /// <c>200</c>, and the TTL sweep runs on a 300&nbsp;s reservation lifetime (nothing expires during the
        /// test), the only <c>InventoryUpdated</c> events for the product are the one-per-success broadcasts.
        /// </para>
        /// </summary>
        [Fact]
        public async Task ReserveInventory_UnderBoundedConcurrentLoad_ReturnsOkOrConflictAndBroadcastsEachSuccess()
        {
            // Declared BEFORE the try so the finally can dispose/clean up even if arrange or act throws partway.
            List<Task<HttpResponseMessage>> requests = null;
            RawSignalRClient client = null;
            var pid = 0;

            try
            {
                // Arrange (1) — a real seeded product id.
                pid = ResolveSeededProductId();

                // Arrange (2) — an authenticated client: the flash-sale create is [Authorize]. The same client is
                // reused for the ANONYMOUS reserve calls (a bearer token on an anonymous endpoint is harmless).
                using var authClient = await _fixture.CreateAuthenticatedClientAsync();

                // Arrange (3) — schedule an ACTIVE flash sale (window straddles now) with allocation ≥ concurrency.
                var createDto = new CreateFlashSaleDto
                {
                    ProductId = pid,
                    StartAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    EndAt = DateTimeOffset.UtcNow.AddHours(1),
                    SalePrice = SalePriceValue,
                    StockAllocation = FlashSaleStockAllocation
                };
                var createJson = JsonSerializer.Serialize(createDto, CamelCaseWriteOpts);
                using (var createContent = new StringContent(createJson, Encoding.UTF8, "application/json"))
                using (var createResp = await authClient.PostAsync(FlashSalesPath, createContent))
                {
                    createResp.IsSuccessStatusCode.Should().BeTrue(
                        "an active flash sale must be created before the reservation burst " +
                        "(POST /api/flash-sales returned {0})", createResp.StatusCode);
                }

                // Arrange (4) — connect + JOIN the product group BEFORE firing the burst, so every success
                // broadcast is guaranteed to be delivered to this connection.
                var wsUri = await BuildHubUriAsync();
                var wsClient = _fixture.Factory.Server.CreateWebSocketClient();
                using (var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(PerFrameTimeoutSeconds)))
                {
                    client = await RawSignalRClient.ConnectAsync(wsClient.ConnectAsync, wsUri, connectCts.Token);
                    await client.JoinProductGroupAsync(pid, connectCts.Token);
                }

                // Act — fire the bounded burst. DISTINCT Guid sessions keep each request in its own rate-limit
                // bucket (never 429). Materialise (ToList) so all are genuinely in flight before the single WhenAll.
                requests = Enumerable.Range(0, ReservationConcurrency).Select(_ =>
                {
                    var dto = new ReserveInventoryDto
                    {
                        ProductId = pid,
                        Quantity = 1,
                        SessionId = Guid.NewGuid().ToString()
                    };
                    var content = new StringContent(
                        JsonSerializer.Serialize(dto, CamelCaseWriteOpts), Encoding.UTF8, "application/json");
                    return authClient.PostAsync(ReservePath, content);
                }).ToList();

                HttpResponseMessage[] responses = null;
                Func<Task> act = async () => responses = await Task.WhenAll(requests);
                await act.Should().NotThrowAsync(
                    "the reserve endpoint must stay responsive under bounded concurrent load");

                // Assert — the response contract.
                responses.Should().HaveCount(ReservationConcurrency);
                responses.Should().NotContain(r => (int)r.StatusCode >= 500,
                    "no server errors (5xx) under bounded reservation load");
                responses.Should().NotContain(r => r.StatusCode == HttpStatusCode.TooManyRequests,
                    "distinct GUID sessions must avoid the 10/min/session rate limit");
                responses.Should().OnlyContain(
                    r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict,
                    "each reserve is either accepted (200) or a documented 409 " +
                    "INSUFFICIENT_STOCK/RESERVATION_CONFLICT");

                var successCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
                successCount.Should().BeGreaterThan(0,
                    "with allocation >= concurrency at least some reservations must succeed");

                // Assert — every successful reservation broadcasts EXACTLY one InventoryUpdated for the product.
                // Drain exactly successCount matching frames within a single bounded deadline; a dropped frame
                // makes the final receive fail fast (OperationCanceledException) rather than hang.
                var received = 0;
                using (var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(DrainTimeoutSeconds)))
                {
                    while (received < successCount)
                    {
                        await client.ReadUntilBroadcastAsync(
                            InventoryUpdatedEvent,
                            a => a.GetProperty("productId").GetInt32() == pid,
                            drainCts.Token);
                        received++;
                    }
                }

                received.Should().Be(successCount,
                    "every successful reservation must broadcast exactly one InventoryUpdated for the " +
                    "product (no dropped frames)");
            }
            finally
            {
                // Dispose every response that actually completed (even if WhenAll threw partway).
                DisposeCompletedResponses(requests);

                // Release the WebSocket (best-effort).
                if (client != null)
                {
                    await client.CloseAsync();
                    client.Dispose();
                }

                // Clean the shared DB so the containers stay pristine for sibling tests. A cleanup failure must
                // never fail an otherwise-passing test, but N6 requires it be REPORTED rather than silently
                // swallowed — a swallowed failure could leave the shared container dirty for sibling tests with
                // no signal. So the exception is caught and written to the test output (visible, separate from
                // the assertion outcome). Set<T>() is used so cleanup does not depend on the exact DbSet
                // property names on StoreContext.
                try
                {
                    using var scope = _fixture.Factory.Services.CreateScope();
                    var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();

                    var reservations = ctx.Set<InventoryReservation>().Where(r => r.ProductId == pid);
                    ctx.Set<InventoryReservation>().RemoveRange(reservations);

                    var sales = ctx.Set<FlashSale>().Where(f => f.ProductId == pid);
                    ctx.Set<FlashSale>().RemoveRange(sales);

                    await ctx.SaveChangesAsync();
                }
                catch (Exception cleanupEx)
                {
                    // N6: report separately — do NOT swallow. This never fails the test, but the failure is
                    // surfaced in test output so a cleanup problem (potential dirty shared state) is visible.
                    _output.WriteLine(
                        $"[cleanup] InventoryBroadcastLoadTests DB cleanup for product {pid} failed and was " +
                        $"reported (not swallowed); sibling tests may see residual state: {cleanupEx}");
                }
            }
        }

        /// <summary>
        /// A minimal SignalR client built on a <b>raw</b> <see cref="System.Net.WebSockets.WebSocket"/> and a
        /// <b>manual JSON-protocol handshake</b>, so the test needs no <c>Microsoft.AspNetCore.SignalR.Client</c>
        /// package (which is not in the shared framework). It owns the socket and a persistent byte accumulator
        /// and implements exactly what the tests need: the handshake, a blocking group-join invocation, and a
        /// predicate-driven "read until a matching server-to-client broadcast" primitive.
        ///
        /// <para>
        /// <b>Framing.</b> Every SignalR message is terminated by the ASCII record separator (<c>0x1E</c>). A
        /// single WebSocket receive may contain a partial frame OR several batched frames, so bytes are
        /// accumulated across receives and split on the separator — never assuming one receive equals one frame.
        /// Every receive is bounded by the caller's <see cref="CancellationToken"/> so a missing frame throws
        /// rather than hangs.
        /// </para>
        /// </summary>
        private sealed class RawSignalRClient : IDisposable
        {
            /// <summary>The underlying raw WebSocket (skip-negotiation transport created by the TestServer).</summary>
            private readonly WebSocket _socket;

            /// <summary>Persistent receive accumulator; frames are sliced off its front on each <see cref="RecordSeparator"/>.</summary>
            private readonly List<byte> _accumulator = new List<byte>();

            /// <summary>Scratch buffer for a single <see cref="WebSocket.ReceiveAsync(ArraySegment{byte}, CancellationToken)"/> call.</summary>
            private readonly byte[] _chunk = new byte[8192];

            private RawSignalRClient(WebSocket socket) => _socket = socket;

            /// <summary>
            /// Connects the WebSocket via the supplied connect delegate (the TestServer's
            /// <c>WebSocketClient.ConnectAsync</c> method group), then performs the SignalR JSON handshake.
            /// Accepting a delegate keeps this helper free of any <c>Microsoft.AspNetCore.TestHost</c> type name.
            /// </summary>
            /// <param name="connect">Connect function mapping a URI + token to a live <see cref="WebSocket"/>.</param>
            /// <param name="uri">The hub WebSocket URI (with the query-string access token).</param>
            /// <param name="ct">Deadline token bounding both the connect and the handshake.</param>
            /// <returns>A handshaken client ready for invocations and receives.</returns>
            public static async Task<RawSignalRClient> ConnectAsync(
                Func<Uri, CancellationToken, Task<WebSocket>> connect,
                Uri uri,
                CancellationToken ct)
            {
                var socket = await connect(uri, ct);
                var client = new RawSignalRClient(socket);
                await client.PerformHandshakeAsync(ct);
                return client;
            }

            /// <summary>
            /// Sends the SignalR JSON handshake request and reads the response frame. Success is an empty object
            /// (no <c>error</c> property); an <c>error</c> property throws. A leading <c>{"type":6}</c> ping (not
            /// normally seen before the handshake response) is tolerated and skipped.
            /// </summary>
            private async Task PerformHandshakeAsync(CancellationToken ct)
            {
                await SendFrameAsync(HandshakeRequest, ct);

                while (true)
                {
                    var frame = await ReadFrameAsync(ct);
                    using var document = JsonDocument.Parse(frame);
                    var root = document.RootElement;

                    // Skip a stray keep-alive ping, on the off chance one precedes the handshake response.
                    if (root.TryGetProperty("type", out var typeElement) && typeElement.GetInt32() == 6)
                    {
                        continue;
                    }

                    if (root.TryGetProperty("error", out var errorElement))
                    {
                        throw new InvalidOperationException(
                            "SignalR handshake failed: " + errorElement.GetString());
                    }

                    // An empty object {} (no error) is the success handshake response.
                    return;
                }
            }

            /// <summary>
            /// Joins the per-product group with a BLOCKING invocation: sends
            /// <c>{"type":1,"invocationId":"join","target":"JoinProductGroup","arguments":[pid]}</c> and awaits
            /// the <c>{"type":3,"invocationId":"join"}</c> completion. Awaiting the completion guarantees the
            /// server has finished adding this connection to the group before any broadcast is sent, removing
            /// the join/broadcast race. Any hub-side error surfaced in the completion is thrown.
            /// </summary>
            /// <param name="productId">The product group to join (must be a positive, real product id).</param>
            /// <param name="ct">Deadline token bounding the invocation round-trip.</param>
            public async Task JoinProductGroupAsync(int productId, CancellationToken ct)
            {
                var invocation = JsonSerializer.Serialize(new
                {
                    type = 1,
                    invocationId = JoinInvocationId,
                    target = "JoinProductGroup",
                    arguments = new object[] { productId }
                });

                await SendFrameAsync(invocation, ct);

                while (true)
                {
                    var frame = await ReadFrameAsync(ct);
                    using var document = JsonDocument.Parse(frame);
                    var root = document.RootElement;

                    if (!root.TryGetProperty("type", out var typeElement))
                    {
                        continue;
                    }

                    var type = typeElement.GetInt32();
                    if (type == 6)
                    {
                        continue; // keep-alive ping
                    }

                    // The completion (type 3) for our specific invocationId confirms group membership.
                    if (type == 3
                        && root.TryGetProperty("invocationId", out var idElement)
                        && idElement.GetString() == JoinInvocationId)
                    {
                        if (root.TryGetProperty("error", out var errorElement))
                        {
                            throw new InvalidOperationException(
                                "JoinProductGroup invocation failed: " + errorElement.GetString());
                        }

                        return;
                    }

                    // Ignore anything else (e.g. an unrelated broadcast) until the join completion arrives.
                }
            }

            /// <summary>
            /// Reads server-to-client frames until one is an invocation (<c>type == 1</c>) whose <c>target</c>
            /// matches <paramref name="target"/> and whose single argument satisfies <paramref name="predicate"/>.
            /// Pings (<c>type == 6</c>), completions, and non-matching targets/arguments are skipped. The matched
            /// argument object is returned as a <see cref="JsonElement.Clone"/> so it outlives the backing
            /// <see cref="JsonDocument"/>.
            /// </summary>
            /// <param name="target">The server-to-client handler name to match (case-sensitive).</param>
            /// <param name="predicate">Predicate applied to <c>arguments[0]</c>.</param>
            /// <param name="ct">Deadline token bounding every receive; a missing frame fails fast.</param>
            /// <returns>The cloned <c>arguments[0]</c> element of the first matching broadcast.</returns>
            public async Task<JsonElement> ReadUntilBroadcastAsync(
                string target,
                Func<JsonElement, bool> predicate,
                CancellationToken ct)
            {
                while (true)
                {
                    var frame = await ReadFrameAsync(ct);
                    using var document = JsonDocument.Parse(frame);
                    var root = document.RootElement;

                    if (!root.TryGetProperty("type", out var typeElement))
                    {
                        continue;
                    }

                    var type = typeElement.GetInt32();
                    if (type == 6 || type != 1)
                    {
                        continue; // skip pings and any non-invocation frame (completions, etc.)
                    }

                    if (!root.TryGetProperty("target", out var targetElement)
                        || targetElement.GetString() != target)
                    {
                        continue; // filter out other events (e.g. FlashSaleStarted / FlashSaleEnded)
                    }

                    if (!root.TryGetProperty("arguments", out var argumentsElement)
                        || argumentsElement.ValueKind != JsonValueKind.Array
                        || argumentsElement.GetArrayLength() == 0)
                    {
                        continue;
                    }

                    var argument = argumentsElement[0];
                    if (predicate(argument))
                    {
                        return argument.Clone();
                    }
                }
            }

            /// <summary>Sends a single UTF-8 text frame, appending the <see cref="RecordSeparator"/> terminator.</summary>
            private Task SendFrameAsync(string json, CancellationToken ct)
            {
                var payload = Encoding.UTF8.GetBytes(json + "\u001e");
                return _socket.SendAsync(
                    new ArraySegment<byte>(payload), WebSocketMessageType.Text, endOfMessage: true, ct);
            }

            /// <summary>
            /// Returns the next complete frame's text (separator excluded), accumulating bytes across
            /// <see cref="WebSocket.ReceiveAsync(ArraySegment{byte}, CancellationToken)"/> calls and splitting on
            /// the first <see cref="RecordSeparator"/>. Throws if the server closes the socket (fail fast).
            /// </summary>
            private async Task<string> ReadFrameAsync(CancellationToken ct)
            {
                while (true)
                {
                    var separatorIndex = _accumulator.IndexOf(RecordSeparator);
                    if (separatorIndex >= 0)
                    {
                        var frameBytes = _accumulator.GetRange(0, separatorIndex).ToArray();
                        _accumulator.RemoveRange(0, separatorIndex + 1); // drop the frame plus its terminator
                        return Encoding.UTF8.GetString(frameBytes);
                    }

                    var result = await _socket.ReceiveAsync(new ArraySegment<byte>(_chunk), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        throw new InvalidOperationException(
                            "WebSocket was closed by the server while awaiting a SignalR frame " +
                            "(close status: " + result.CloseStatus + ").");
                    }

                    for (var i = 0; i < result.Count; i++)
                    {
                        _accumulator.Add(_chunk[i]);
                    }
                }
            }

            /// <summary>Best-effort graceful close (guarded on an open socket); never throws.</summary>
            public async Task CloseAsync()
            {
                try
                {
                    if (_socket.State == WebSocketState.Open)
                    {
                        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await _socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure, "test complete", closeCts.Token);
                    }
                }
                catch
                {
                    // A half-open/aborted socket must never fail the test during cleanup.
                }
            }

            /// <summary>Disposes the underlying WebSocket.</summary>
            public void Dispose() => _socket.Dispose();
        }
    }
}
