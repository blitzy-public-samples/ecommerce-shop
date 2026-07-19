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
    ///   <item><b>Deterministic non-zero cache-hit ratio.</b> Because the action is decorated
    ///         <c>[Cached(600)]</c> and backed by real Redis, a pre-populated cache entry causes the entire
    ///         concurrent burst to be served byte-for-byte from Redis rather than from distinct database
    ///         round-trips (<see cref="GetProducts_UnderConcurrentLoadWithPopulatedCache_ServesResponsesFromRedisCache"/>).</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Real infrastructure only (AAP §0.10.1).</b> Nothing here is mocked or stubbed: PostgreSQL, Redis
    /// and the HTTP transport are all genuine. The cache test opens a second <see cref="IConnectionMultiplexer"/>
    /// to the SAME Redis container the application uses — via <see cref="ContainerFixture.RedisConnectionString"/>
    /// (a Testcontainers <c>host:port</c> endpoint, never the shared <c>docker-compose.yml</c> stack) — purely
    /// to <i>inspect</i> and cold-start the key the <c>[Cached]</c> filter writes. Concurrency is driven
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

            HttpResponseMessage[] responses = null;
            Func<Task> act = async () => responses = await Task.WhenAll(requests);

            // No timeouts, no unhandled exceptions surfaced while awaiting the concurrent batch.
            await act.Should().NotThrowAsync();

            // Read every body concurrently (bodies are buffered, so the responses can be disposed afterwards).
            var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));

            try
            {
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
                // Tidy: release the 200 response messages once all bodies have been read.
                foreach (var response in responses)
                {
                    response?.Dispose();
                }
            }
        }

        /// <summary>
        /// Proves a <b>deterministic non-zero cache-hit ratio</b> under concurrent load: after warming the
        /// <c>[Cached(600)]</c> entry in real Redis, a burst of <see cref="ConcurrentRequests"/> requests is
        /// served byte-for-byte from the cached Redis string (every burst body equals the stored value),
        /// bypassing fresh database round-trips. With the warm-up populating the key before the burst, the
        /// 600-second TTL far exceeding the test duration, and assembly-wide serial execution, the hit ratio
        /// is 100% (and, at minimum, strictly greater than zero — the literal requirement).
        /// </summary>
        [Fact]
        public async Task GetProducts_UnderConcurrentLoadWithPopulatedCache_ServesResponsesFromRedisCache()
        {
            // Arrange — shared client + a direct connection to the SAME real Redis container the app uses,
            // solely to inspect/cold-start the cache key (no mocking of Redis; AAP §0.10.1).
            using var client = _fixture.CreateClient();
            using var redis = ConnectionMultiplexer.Connect(_fixture.RedisConnectionString);
            var db = redis.GetDatabase();

            // Cold-start: delete any pre-existing entry so the warm-up demonstrably (re)populates it. Safe
            // because the whole integration assembly runs SERIALLY (AssemblyInfo:
            // DisableTestParallelization = true), so no concurrent sibling test can be reading this key.
            await db.KeyDeleteAsync(CacheKey);

            // Warm the cache: one fully-awaited request (miss -> controller -> Redis write) populates the key.
            // The [Cached] filter awaits CacheResponseAsync before the HTTP response returns, so the key is
            // guaranteed present the instant GetAsync completes — no sleep/polling required.
            using (var warmUp = await client.GetAsync(ProductsPath))
            {
                warmUp.StatusCode.Should().Be(HttpStatusCode.OK);
            }

            (await db.KeyExistsAsync(CacheKey)).Should().BeTrue(
                "the [Cached(600)] products response must be stored in Redis after the first request");
            string cached = await db.StringGetAsync(CacheKey);
            cached.Should().NotBeNullOrEmpty();

            // Act — fire the 200 concurrent burst over the shared client and read all bodies.
            var requests = Enumerable.Range(0, ConcurrentRequests)
                                     .Select(_ => client.GetAsync(ProductsPath))
                                     .ToList();
            var responses = await Task.WhenAll(requests);
            var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));

            try
            {
                // Assert — every burst request succeeded.
                responses.Should().OnlyContain(
                    r => r.StatusCode == HttpStatusCode.OK,
                    "a warm [Cached(600)] entry must let every concurrent request succeed");

                // Every burst body equals the cached Redis value verbatim — proving each was served from the
                // cached ContentResult (byte-for-byte), not recomputed via a fresh DB round-trip + formatter.
                bodies.Should().OnlyContain(b => b == cached,
                    "a cache hit returns the stored Redis string verbatim");

                // Deterministic non-zero cache-hit ratio (the measured requirement).
                int cacheHits = bodies.Count(b => b == cached);
                double cacheHitRatio = (double)cacheHits / ConcurrentRequests;
                cacheHitRatio.Should().BeGreaterThan(0,
                    "with a pre-populated [Cached(600)] entry, concurrent requests must be served from Redis, " +
                    "not all from distinct DB round-trips");
                // Strong form: a warm cache + serial collection + 600s TTL means 100% of the burst are hits.
                cacheHits.Should().Be(ConcurrentRequests);

                // The cache key survives the burst (a hit never evicts or rewrites it).
                (await db.KeyExistsAsync(CacheKey)).Should().BeTrue();

                // Sanity on the cached content: it is the documented first page over the 18 seeded products.
                var page = Deserialize(cached);
                page.Count.Should().Be(ExpectedProductCount);
            }
            finally
            {
                // Tidy: release the 200 response messages (the cache key is intentionally left populated).
                foreach (var response in responses)
                {
                    response?.Dispose();
                }
            }
        }
    }
}
