using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;                             // HttpStatusCode
using System.Net.Http;                        // HttpClient, HttpResponseMessage
using System.Text.Json;                       // JsonSerializer, JsonSerializerOptions
using System.Threading.Tasks;                 // Task, Task.WhenAll
using API.Dtos;                               // ProductToReturnDto
using API.Helpers;                            // Pagination<T>
using API.IntegrationTests.Infrastructure;    // ContainerFixture
using FluentAssertions;
using StackExchange.Redis;                    // ConnectionMultiplexer, IDatabase
using Xunit;

namespace API.IntegrationTests.Load
{
    /// <summary>
    /// Concurrent-load integration tests for <c>GET api/products</c>, exercised end-to-end through the
    /// <b>real</b> ASP.NET Core HTTP pipeline (in-process <c>WebApplicationFactory&lt;Startup&gt;</c>) against
    /// a <b>real</b> PostgreSQL store and a <b>real</b> Redis cache provisioned by Testcontainers via the
    /// shared <see cref="ContainerFixture"/> (AAP §0.4.1 / §0.4.2 "200-request load scenario"; §0.5.1
    /// <c>Load/ProductsLoadTests.cs</c>; source subject <c>API/Controllers/ProductsController.cs</c>).
    ///
    /// <para>
    /// The class fires <see cref="ConcurrentRequests"/> (200) simultaneous requests through a single shared
    /// <see cref="HttpClient"/> and proves three guarantees verified against the actual production source:
    /// <list type="number">
    ///   <item><b>Throughput correctness.</b> No timeouts, no unhandled exceptions and no HTTP <c>500</c> —
    ///         every one of the 200 responses is <c>200 OK</c> (<see cref="GetProducts_When200RequestsSentConcurrently_AllSucceedWithConsistentPaginatedResults"/>).</item>
    ///   <item><b>Pagination correctness &amp; consistency.</b> Each response is a
    ///         <see cref="Pagination{T}"/> of <see cref="ProductToReturnDto"/> with the documented defaults
    ///         (<c>Count = 18</c> seeded products, <c>PageIndex = 1</c>, <c>PageSize = 6</c>, first page of
    ///         exactly 6 items), and every concurrent response returns the SAME product-id sequence — which
    ///         is deterministic because the default specification applies <c>AddOrderBy(x =&gt; x.Name)</c>
    ///         over 18 distinctly-named seed products.</item>
    ///   <item><b>Deterministic non-zero cache-hit ratio, independently verified.</b> Because the action is
    ///         decorated <c>[Cached(600)]</c> and backed by real Redis, the cache entry the warm-up populates
    ///         is deliberately OVERWRITTEN with a <i>controller-impossible sentinel</i> — an otherwise
    ///         well-formed <see cref="Pagination{T}"/> page whose <c>Count</c> (<see cref="SentinelProductCount"/>)
    ///         the unfiltered <c>GetProducts</c> action can NEVER emit (its <c>totalItems</c> for the seeded
    ///         catalogue is always exactly 18). A burst body that echoes the sentinel therefore PROVES the
    ///         response was served verbatim from Redis, whereas a bypassed/disabled cache re-querying
    ///         PostgreSQL would serialise the real <c>Count = 18</c> payload and FAIL the oracle. This makes the
    ///         hit measurement independent of mere body-equality against a value the database could also
    ///         produce (<see cref="GetProducts_UnderConcurrentLoadWithPopulatedCache_ServesResponsesFromRedisCache"/>).</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Real infrastructure only (AAP §0.10.1).</b> Nothing here is mocked or stubbed: PostgreSQL, Redis
    /// and the HTTP transport are all genuine. The cache test opens a second <see cref="IConnectionMultiplexer"/>
    /// to the SAME Redis container the application uses — via <see cref="ContainerFixture.RedisConnectionString"/>
    /// (a Testcontainers <c>host:port</c> endpoint, never the shared <c>docker-compose.yml</c> stack) — purely
    /// to <i>inspect</i>, cold-start and overwrite-with-a-sentinel the key the <c>[Cached]</c> filter writes
    /// (the sentinel is a genuine Redis value, so no infrastructure is mocked). Concurrency is driven
    /// exclusively through a single <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/>;
    /// there is no <c>Thread.Sleep</c>, no polling and no retry loop anywhere in this class (AAP §0.7.2,
    /// §0.10.2). The fixture's wait strategies guarantee container readiness before any test runs.
    /// </para>
    ///
    /// <para>
    /// <b>Fixture wiring — as-built harness contract (CR-01).</b> The shared harness was refactored so that
    /// <see cref="ContainerFixture"/> is consumed as a PER-CLASS <c>IClassFixture&lt;ContainerFixture&gt;</c>;
    /// the project defines no <c>ICollectionFixture</c> / <c>[CollectionDefinition("Integration")]</c> (the
    /// former <c>IntegrationTestCollection.cs</c> was removed). Every sibling integration class
    /// (<c>ResponseCacheIntegrationTests</c>, <c>BasketConcurrencyTests</c>,
    /// <c>EndpointContractRegressionTests</c>, …) follows this contract, and
    /// <c>Infrastructure/AssemblyInfo.cs</c> sets
    /// <c>[assembly: CollectionBehavior(DisableTestParallelization = true)]</c> so classes still run
    /// SEQUENTIALLY. This class therefore uses <c>IClassFixture&lt;ContainerFixture&gt;</c> (not a
    /// <c>[Collection]</c>): a <c>[Collection("Integration")]</c> annotation would fail at runtime with
    /// "constructor parameters did not have matching fixture data" because no such collection fixture exists.
    /// The assembly-level serial-execution guarantee is what makes the Test-2 cold-start
    /// <see cref="IDatabaseAsync.KeyDeleteAsync(RedisKey, CommandFlags)"/> safe (no concurrent sibling can be
    /// reading the shared cache key).
    /// </para>
    ///
    /// <para>
    /// Naming follows the repository convention <c>MethodName_StateUnderTest_ExpectedBehavior</c>, with an
    /// Arrange-Act-Assert structure and FluentAssertions throughout.
    /// </para>
    /// </summary>
    public class ProductsLoadTests : IClassFixture<ContainerFixture>
    {
        // --- Load / contract constants (exact literals per the endpoint + seed contract) ----------------

        /// <summary>Number of simultaneous <c>GET api/products</c> requests fired in each load burst.</summary>
        private const int ConcurrentRequests = 200;

        /// <summary>
        /// Relative request path (lowercase, no query string). Resolved against the TestServer base
        /// <c>http://localhost/</c> to <c>http://localhost/api/products</c>.
        /// </summary>
        private const string ProductsPath = "api/products";

        /// <summary>
        /// The exact Redis key the <c>[Cached]</c> filter writes for <c>GET api/products</c> with no query
        /// parameters: <c>CachedAttribute.GenerateCacheKeyFromRequest</c> emits <c>request.Path</c>
        /// (<c>"/api/products"</c>) with no trailing pipe when there are no query parameters. Casing matches
        /// the request path.
        /// </summary>
        private const string CacheKey = "/api/products";

        /// <summary>Total seeded products (6 brands / 4 types / <b>18 products</b> / 4 delivery methods).</summary>
        private const int ExpectedProductCount = 18;

        /// <summary><c>ProductSpecParams.PageIndex</c> default.</summary>
        private const int ExpectedPageIndex = 1;

        /// <summary><c>ProductSpecParams.PageSize</c> default.</summary>
        private const int ExpectedPageSize = 6;

        /// <summary>First-page item count (min of <see cref="ExpectedPageSize"/> and the seeded total).</summary>
        private const int ExpectedPageDataCount = 6;

        /// <summary>
        /// Shared, case-insensitive options so the camelCase response body (<c>pageIndex</c>, <c>pageSize</c>,
        /// <c>count</c>, <c>data</c>) binds to the PascalCase members of <see cref="Pagination{T}"/> /
        /// <see cref="ProductToReturnDto"/>. System.Text.Json on .NET 5 supports the single parameterized
        /// <see cref="Pagination{T}"/> constructor and materialises its <c>IReadOnlyList&lt;T&gt;</c> data.
        /// </summary>
        private static readonly JsonSerializerOptions JsonOpts =
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // --- Controller-impossible cache-hit sentinel (independent hit oracle — CR finding F8) -----------
        //
        // The naive oracle "burst body == warmed Redis value" is a FALSE POSITIVE: the default specification
        // orders products by name, so GetProducts is byte-for-byte deterministic — a DISABLED/bypassed cache
        // that re-queries PostgreSQL and re-serialises would produce JSON identical to the warmed value, and
        // body-equality could not tell a genuine Redis hit apart from a fresh DB round-trip. To measure hits
        // INDEPENDENTLY, Test-2 overwrites the [Cached] entry with a SENTINEL page whose Count is a value the
        // production action can never emit (its totalItems for the unfiltered, 18-product catalogue is always
        // exactly 18). A burst response reporting the sentinel Count therefore PROVES it was served from Redis
        // (the controller/DB path is physically incapable of producing it); a bypassed cache would report the
        // real Count = 18 and fail the oracle.

        /// <summary>
        /// A <see cref="Pagination{T}.Count"/> value the production <c>GetProducts</c> action can NEVER emit for
        /// <c>GET api/products</c>: <c>totalItems</c> for the unfiltered catalogue is the seeded product total
        /// (exactly 18, fail-loud-verified by <see cref="ContainerFixture"/>). Any response echoing this count
        /// can only have come verbatim from the Redis cache — the independent cache-hit oracle.
        /// </summary>
        private const int SentinelProductCount = 4242;

        /// <summary>A negative id — impossible for a seeded identity primary key (which starts at 1) — used on
        /// the sentinel's single data row so the payload is unmistakably synthetic.</summary>
        private const int SentinelProductId = -424242;

        /// <summary>A product name absent from the seed set, marking the sentinel row as synthetic.</summary>
        private const string SentinelProductName = "__INTEGRATION_CACHE_SENTINEL_PRODUCT__";

        /// <summary>
        /// TTL applied to the injected sentinel entry, matching the production <c>[Cached(600)]</c> lifetime so
        /// it comfortably outlives the burst; Test-2 nonetheless deletes the key in its <c>finally</c> so the
        /// synthetic value never leaks to a sibling test.
        /// </summary>
        private const int SentinelTtlSeconds = 600;

        /// <summary>
        /// camelCase serializer options mirroring <c>ResponseCacheService.CacheResponseAsync</c>
        /// (<see cref="JsonNamingPolicy.CamelCase"/>), so the injected sentinel is byte-compatible with a
        /// genuine <c>[Cached]</c> entry and is served back verbatim by the <c>[Cached]</c> filter.
        /// </summary>
        private static readonly JsonSerializerOptions CamelCaseWriteOpts =
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        /// <summary>This class's dedicated Testcontainers-backed fixture (real PostgreSQL + Redis + host).</summary>
        private readonly ContainerFixture _fixture;

        /// <summary>
        /// Receives this class's isolated <see cref="ContainerFixture"/> from xUnit's class-fixture machinery.
        /// By the time this constructor runs the fixture has already started the containers, applied the EF
        /// Core migrations and seeded the documented reference data, so <c>GET api/products</c> returns real,
        /// non-empty, deterministically-ordered data.
        /// </summary>
        /// <param name="fixture">This class's isolated container fixture.</param>
        public ProductsLoadTests(ContainerFixture fixture) => _fixture = fixture;

        /// <summary>
        /// Deserialises a <c>GET api/products</c> response body into the REAL
        /// <see cref="Pagination{T}"/> of <see cref="ProductToReturnDto"/> (satisfying the contract literally).
        /// </summary>
        /// <param name="body">The raw camelCase JSON response body.</param>
        /// <returns>The deserialised paginated payload.</returns>
        private static Pagination<ProductToReturnDto> Deserialize(string body) =>
            JsonSerializer.Deserialize<Pagination<ProductToReturnDto>>(body, JsonOpts);

        /// <summary>
        /// Disposes every <see cref="HttpResponseMessage"/> produced by a batch of concurrent request tasks —
        /// including on the exceptional path where <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/>
        /// threw after only SOME requests completed (so the aggregated <c>responses</c> array was never
        /// assigned). Iterating the request TASK list rather than a materialised response array guarantees no
        /// completed response leaks until fixture teardown (CR finding F12). Only tasks that
        /// <see cref="TaskStatus.RanToCompletion"/> produced a response to dispose; faulted/cancelled tasks are
        /// skipped (reading their <c>Result</c> would rethrow and there is nothing to release).
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
        /// Builds the <b>controller-impossible sentinel</b> written to the <c>[Cached]</c> Redis key so Test-2
        /// can measure cache hits INDEPENDENTLY of body-equality against a value the database could also
        /// produce (CR finding F8). The payload is a structurally-valid <see cref="Pagination{T}"/> of
        /// <see cref="ProductToReturnDto"/> whose <c>Count</c> (<see cref="SentinelProductCount"/>) the
        /// unfiltered <c>GetProducts</c> action can never emit, carrying a single synthetic row
        /// (<see cref="SentinelProductId"/> / <see cref="SentinelProductName"/>) absent from the seed set. It
        /// is serialised with the SAME camelCase policy the production <c>ResponseCacheService</c> uses, so the
        /// <c>[Cached]</c> filter returns it verbatim exactly as it would a genuine cache entry.
        /// </summary>
        /// <returns>The camelCase JSON string to store at <see cref="CacheKey"/>.</returns>
        private static string BuildControllerImpossibleSentinel()
        {
            var sentinelPage = new Pagination<ProductToReturnDto>(
                ExpectedPageIndex,
                ExpectedPageSize,
                SentinelProductCount,
                new List<ProductToReturnDto>
                {
                    new ProductToReturnDto
                    {
                        Id = SentinelProductId,
                        Name = SentinelProductName,
                        Description = "controller-impossible cache sentinel injected by ProductsLoadTests",
                        Price = 0m,
                        PictureUrl = string.Empty,
                        ProductType = "sentinel",
                        ProductBrand = "sentinel"
                    }
                });

            return JsonSerializer.Serialize(sentinelPage, CamelCaseWriteOpts);
        }

        /// <summary>
        /// Fires <see cref="ConcurrentRequests"/> concurrent <c>GET api/products</c> requests over a single
        /// shared client and asserts (1) the burst completes without any timeout or unhandled exception,
        /// (2) every response is <c>200 OK</c> (never a <c>500</c>), and (3) every response carries the same,
        /// correct first page — <c>Count = 18</c>, <c>PageIndex = 1</c>, <c>PageSize = 6</c>, exactly 6 items,
        /// and an identical product-id sequence across all 200 responses (the default
        /// <c>AddOrderBy(x =&gt; x.Name)</c> over 18 distinctly-named products makes the order deterministic).
        ///
        /// <para>
        /// The <c>[Cached(600)]</c> entry is warmed with one fully-awaited request before the burst — the
        /// warm-cache load profile the AAP's load scenario targets (§0.4.1 / §0.4.2 explicitly expect a
        /// "non-zero cache-hit ratio"). This is also what makes "all 200 succeed" deterministic: the seeded
        /// PostgreSQL server's <c>max_connections</c> (100) is below the 200-way fan-out, so a purely COLD
        /// burst would stampede the database and surface transient <c>53300: too many clients already</c>
        /// errors; a warm cache lets the filter short-circuit every request from Redis (the thread-safe
        /// singleton multiplexer) without opening a single DB connection.
        /// </para>
        /// </summary>
        [Fact]
        public async Task GetProducts_When200RequestsSentConcurrently_AllSucceedWithConsistentPaginatedResults()
        {
            // Arrange — one shared client bound to the in-process TestServer (AllowAutoRedirect=false, so any
            // UseHttpsRedirection 307 would surface rather than be silently followed; under TestServer no
            // HTTPS port exists, so api/products answers 200 directly over HTTP).
            using var client = _fixture.CreateClient();

            // Warm the [Cached(600)] entry with ONE fully-awaited request before the burst. This mirrors the
            // realistic warm-cache load the AAP's load scenario targets (§0.4.1 / §0.4.2 explicitly expect a
            // "non-zero cache-hit ratio") and is REQUIRED for determinism: PostgreSQL's max_connections (100)
            // is below the 200-way fan-out, so a purely COLD burst would stampede the DB and surface transient
            // "53300: too many clients already" 500s. With the cache warm, the [Cached] filter short-circuits
            // every concurrent request from Redis (the thread-safe singleton multiplexer) without opening any
            // DB connection, so all 200 succeed. The filter awaits the Redis write before returning, so the
            // entry is guaranteed present the instant this GET completes — no Thread.Sleep / polling needed.
            using (var warmUp = await client.GetAsync(ProductsPath))
            {
                warmUp.StatusCode.Should().Be(HttpStatusCode.OK,
                    "the initial cache-populating request must succeed before the concurrent burst");
            }

            // Act — start all 200 GETs, then await them through a SINGLE Task.WhenAll (no Thread.Sleep). The
            // requests are materialised (ToList) so they are genuinely in flight before we await the batch.
            var requests = Enumerable.Range(0, ConcurrentRequests)
                                     .Select(_ => client.GetAsync(ProductsPath))
                                     .ToList();

            try
            {
                HttpResponseMessage[] responses = null;
                Func<Task> act = async () => responses = await Task.WhenAll(requests);

                // No timeouts, no unhandled exceptions surfaced while awaiting the concurrent batch.
                await act.Should().NotThrowAsync();

                // Read every body concurrently (bodies are buffered, so the responses can be disposed afterwards).
                var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));

                // Assert — throughput correctness.
                responses.Should().HaveCount(ConcurrentRequests);
                responses.Should().OnlyContain(
                    r => r.StatusCode == HttpStatusCode.OK,
                    "every concurrent GET api/products must succeed through the real pipeline against real " +
                    "PostgreSQL + Redis");
                responses.Should().NotContain(
                    r => (int)r.StatusCode >= 500,
                    "no request may degrade into a server error / structured 500 under concurrent load");

                // Assert — pagination correctness for every response.
                var pages = bodies.Select(Deserialize).ToList();
                pages.Should().OnlyContain(p => p != null,
                    "each response body must deserialise into a Pagination<ProductToReturnDto>");
                pages.Should().OnlyContain(p => p.Count == ExpectedProductCount,
                    "the seeded catalogue holds exactly 18 products, so every response reports Count = 18");
                pages.Should().OnlyContain(p => p.PageIndex == ExpectedPageIndex,
                    "the default ProductSpecParams.PageIndex is 1");
                pages.Should().OnlyContain(p => p.PageSize == ExpectedPageSize,
                    "the default ProductSpecParams.PageSize is 6");
                pages.Should().OnlyContain(p => p.Data != null && p.Data.Count == ExpectedPageDataCount,
                    "the first page returns exactly 6 items");

                // Assert — results are stable/consistent across every concurrent response: the product-id
                // sequence of each page equals the first page's (deterministic ORDER BY Name over 18 distinct
                // names, so concurrent DB reads and cache hits all yield the same ordering).
                var referenceIds = pages[0].Data.Select(d => d.Id).ToArray();
                pages.Should().OnlyContain(
                    p => p.Data.Select(d => d.Id).SequenceEqual(referenceIds),
                    "every concurrent response must return an identical, deterministically-ordered page");
            }
            finally
            {
                // F12: dispose EVERY response that actually completed — even if Task.WhenAll threw after only
                // SOME requests finished (so the aggregated `responses` array was never assigned). Iterating
                // the request TASK list guarantees no HttpResponseMessage leaks until fixture teardown.
                DisposeCompletedResponses(requests);
            }
        }

        /// <summary>
        /// Proves a <b>deterministic non-zero cache-hit ratio</b> under concurrent load, measured with an
        /// oracle that is <b>independent</b> of body-equality against a value the database could also produce
        /// (CR finding F8).
        ///
        /// <para>
        /// The naive check "burst body == warmed Redis value" is a false positive: <c>GET api/products</c> is
        /// byte-for-byte deterministic (the default specification orders by name over 18 distinctly-named
        /// products), so a DISABLED or bypassed cache that re-queried PostgreSQL and re-serialised would emit
        /// JSON identical to the warmed value — body-equality alone cannot distinguish a genuine Redis hit from
        /// a fresh DB round-trip. This test therefore warms the <c>[Cached(600)]</c> entry (proving the write
        /// path and capturing the real <c>Count = 18</c> baseline), then <b>overwrites</b> the key with a
        /// <i>controller-impossible sentinel</i> whose <c>Count</c> (<see cref="SentinelProductCount"/>) the
        /// unfiltered action can never emit. Every burst response is then asserted to echo that sentinel count,
        /// which PROVES it was served verbatim from Redis; the test additionally asserts that NO response
        /// carries the real DB-computed value, so a cache bypass would fail the oracle rather than silently
        /// pass. With the sentinel populated before the burst, its 600-second TTL far exceeding the test
        /// duration, and assembly-wide serial execution, the hit ratio is 100% (and, at minimum, strictly
        /// greater than zero — the literal requirement). The sentinel key is deleted in the <c>finally</c> so
        /// the synthetic value never leaks to a sibling test (CR finding F12).
        /// </para>
        /// </summary>
        [Fact]
        public async Task GetProducts_UnderConcurrentLoadWithPopulatedCache_ServesResponsesFromRedisCache()
        {
            // Arrange — shared client + a direct connection to the SAME real Redis container the app uses,
            // solely to inspect/cold-start/overwrite the cache key (no mocking of Redis; AAP §0.10.1).
            using var client = _fixture.CreateClient();
            using var redis = ConnectionMultiplexer.Connect(_fixture.RedisConnectionString);
            var db = redis.GetDatabase();

            // Declared before the try so the finally can dispose completed responses even if the burst throws
            // partway (null until the burst is fired — DisposeCompletedResponses treats null as a no-op).
            List<Task<HttpResponseMessage>> requests = null;

            try
            {
                // Cold-start: delete any pre-existing entry so the warm-up demonstrably (re)populates it. Safe
                // because the whole integration assembly runs SERIALLY (AssemblyInfo:
                // DisableTestParallelization = true), so no concurrent sibling test can be reading this key.
                await db.KeyDeleteAsync(CacheKey);

                // Warm the cache: one fully-awaited request (miss -> controller -> Redis write) populates the
                // key. The [Cached] filter awaits CacheResponseAsync before the HTTP response returns, so the
                // key is guaranteed present the instant GetAsync completes — no sleep/polling required.
                using (var warmUp = await client.GetAsync(ProductsPath))
                {
                    warmUp.StatusCode.Should().Be(HttpStatusCode.OK);
                }

                (await db.KeyExistsAsync(CacheKey)).Should().BeTrue(
                    "the [Cached(600)] products response must be stored in Redis after the first request");

                // Capture the genuine controller output as an INDEPENDENT baseline: it reports the real seeded
                // product count (18). Any burst body equal to this value would indicate the cache was bypassed.
                string realCachedValue = await db.StringGetAsync(CacheKey);
                realCachedValue.Should().NotBeNullOrEmpty();
                Deserialize(realCachedValue).Count.Should().Be(ExpectedProductCount,
                    "the genuinely cached controller output reports the real seeded product count (18)");

                // Overwrite the entry with a CONTROLLER-IMPOSSIBLE sentinel. Its Count (SentinelProductCount =
                // 4242) is a value GetProducts can never emit for the unfiltered 18-product catalogue, so any
                // burst response echoing it can ONLY have been served verbatim from Redis — the independent
                // hit oracle (F8). Serialised with the same camelCase policy the app uses, so the [Cached]
                // filter returns it byte-for-byte exactly as it would a genuine cache entry.
                string sentinelValue = BuildControllerImpossibleSentinel();
                sentinelValue.Should().NotBe(realCachedValue,
                    "the sentinel must differ from the real controller output for body-equality to be a " +
                    "meaningful, bypass-detecting oracle");
                await db.StringSetAsync(CacheKey, sentinelValue, TimeSpan.FromSeconds(SentinelTtlSeconds));

                // Act — fire the 200 concurrent burst over the shared client and read all bodies. Every request
                // must be a cache hit returning the sentinel (no controller/DB round-trip).
                requests = Enumerable.Range(0, ConcurrentRequests)
                                     .Select(_ => client.GetAsync(ProductsPath))
                                     .ToList();
                var responses = await Task.WhenAll(requests);
                var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));

                // Assert — every burst request succeeded.
                responses.Should().OnlyContain(
                    r => r.StatusCode == HttpStatusCode.OK,
                    "a warm [Cached(600)] entry must let every concurrent request succeed");

                // Independent, controller-impossible hit oracle: every response reports the sentinel Count that
                // the production action can never produce — proving it was served from Redis, not recomputed
                // via a fresh DB round-trip + formatter (defeats the F8 false positive).
                var pages = bodies.Select(Deserialize).ToList();
                pages.Should().OnlyContain(p => p != null && p.Count == SentinelProductCount,
                    "each burst response must echo the controller-impossible sentinel Count (4242) — a value " +
                    "GetProducts can never serialise from the 18-product catalogue — proving a genuine Redis hit");

                // Anti-bypass: no response may carry the real DB-computed value. If the cache were bypassed,
                // requests would re-query PostgreSQL and return Count = 18, which this assertion rejects.
                bodies.Should().NotContain(b => b == realCachedValue,
                    "no burst response may echo the real DB-computed payload — that would prove a cache bypass");

                // Secondary signal: a cache hit returns the stored Redis string verbatim (byte-for-byte).
                bodies.Should().OnlyContain(b => b == sentinelValue,
                    "a cache hit returns the stored Redis string verbatim");

                // Deterministic non-zero cache-hit ratio (the measured requirement), counted via the
                // impossible-count oracle rather than equality with a database-reproducible value.
                int cacheHits = pages.Count(p => p.Count == SentinelProductCount);
                double cacheHitRatio = (double)cacheHits / ConcurrentRequests;
                cacheHitRatio.Should().BeGreaterThan(0,
                    "with a pre-populated [Cached(600)] entry, concurrent requests must be served from Redis, " +
                    "not all from distinct DB round-trips");
                // Strong form: a warm cache + serial collection + 600s TTL means 100% of the burst are hits.
                cacheHits.Should().Be(ConcurrentRequests);

                // The cache key survives the burst (a hit never evicts or rewrites it).
                (await db.KeyExistsAsync(CacheKey)).Should().BeTrue();
            }
            finally
            {
                // F12: dispose EVERY response that completed, even if Task.WhenAll threw after only some
                // requests finished, then delete the sentinel-polluted key so the controller-impossible value
                // never leaks to a sibling test (or to Test-1, which warms its own cache regardless).
                DisposeCompletedResponses(requests);
                await db.KeyDeleteAsync(CacheKey);
            }
        }
    }
}
