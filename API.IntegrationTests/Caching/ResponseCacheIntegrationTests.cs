using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using API.IntegrationTests.Infrastructure;
using FluentAssertions;
using StackExchange.Redis;
using Xunit;

namespace API.IntegrationTests.Caching
{
    /// <summary>
    /// End-to-end integration tests for the response-caching behaviour of
    /// <c>GET api/products</c> (decorated <c>[Cached(600)]</c>), exercised through the full ASP.NET Core
    /// HTTP pipeline via the shared <see cref="CustomWebApplicationFactory"/> and asserted against a
    /// <b>real</b> Redis instance provisioned by Testcontainers (AAP §0.5.1 / §0.10.1).
    ///
    /// <para>
    /// These tests prove three distinct guarantees of the caching stack — verified against the actual
    /// source of <c>API/Helpers/CachedAttribute.cs</c>, <c>Infrastructure/Services/ResponseCacheService.cs</c>
    /// and <c>API/Controllers/ProductsController.cs</c>:
    /// <list type="number">
    ///   <item><b>Miss-then-hit.</b> The first request populates Redis; the identical second request is
    ///         short-circuited by the filter and served byte-for-byte from the cached
    ///         <c>ContentResult</c>.</item>
    ///   <item><b>Deterministic cache key.</b> Query parameters supplied in a different order resolve to the
    ///         SAME cache entry (the key sorts params by name), whereas genuinely different parameters
    ///         resolve to DIFFERENT entries.</item>
    ///   <item><b>Non-200 responses are not cached.</b> A <c>404</c> from
    ///         <c>GET api/products/{invalidId}</c> writes nothing to Redis because the filter only caches an
    ///         <c>OkObjectResult</c>.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Real infrastructure only (AAP §0.10.1).</b> Nothing here is mocked or stubbed: Redis, PostgreSQL
    /// and the HTTP transport are all genuine. The test connects a second
    /// <see cref="IConnectionMultiplexer"/> to the SAME Redis container the application uses (via
    /// <see cref="ContainerFixture.RedisConnectionString"/>) purely to <i>inspect</i> the keys that the
    /// <c>[Cached]</c> filter writes.
    /// </para>
    ///
    /// <para>
    /// <b>No waiting, no flakiness.</b> <c>ResponseCacheService.CacheResponseAsync</c> is <c>await</c>ed
    /// inside the action filter <i>before</i> the HTTP response is returned to the client, so the cache key
    /// is guaranteed to exist the instant <c>GetAsync</c> completes. There is therefore no
    /// <c>Thread.Sleep</c>, no polling and no retry loop anywhere in this class (AAP §0.7.2, §0.10.2).
    /// </para>
    ///
    /// <para>
    /// <b>Isolation across the shared, sequential collection.</b> Every integration test class shares ONE
    /// <c>[Collection("Integration")]</c> and therefore ONE Redis instance, running sequentially. To ensure
    /// each test owns cache keys that no other test (here or in sibling classes) can collide with, every
    /// test mints a fresh <c>marker = Guid.NewGuid().ToString("N")</c> and passes it as an (unbound, hence
    /// data-neutral) <c>marker</c> query parameter. Because the cache key incorporates every sorted query
    /// parameter, the marker makes the whole key unique while leaving the returned product data unchanged,
    /// letting the test compute the expected key literal and assert on it directly with
    /// <see cref="IDatabase.KeyExists(RedisKey, CommandFlags)"/> / <see cref="IDatabase.StringGet(RedisKey, CommandFlags)"/>
    /// (no <c>IServer.Keys()</c> admin mode required).
    /// </para>
    ///
    /// <para>
    /// xUnit constructs a fresh instance of this class per <c>[Fact]</c>, so the constructor opens exactly
    /// one Redis multiplexer per test and <see cref="Dispose"/> closes it — keeping tests isolated.
    /// </para>
    /// </summary>
    [Collection("Integration")]
    public class ResponseCacheIntegrationTests : IDisposable
    {
        private readonly ContainerFixture _fixture;
        private readonly IConnectionMultiplexer _redis;
        private readonly IDatabase _db;

        /// <summary>
        /// Receives the shared <see cref="ContainerFixture"/> (injected by xUnit because the class joins the
        /// <c>"Integration"</c> collection) and opens a direct connection to the SAME real Redis container
        /// the application under test uses. By the time this constructor runs, the fixture's
        /// <c>InitializeAsync</c> has already started the containers, applied migrations and seeded the
        /// documented reference data (6 brands / 4 types / 18 products / 4 delivery methods), so
        /// <c>GET api/products</c> returns real, non-empty data.
        /// </summary>
        /// <param name="fixture">The shared Testcontainers-backed fixture supplied by the collection.</param>
        public ResponseCacheIntegrationTests(ContainerFixture fixture)
        {
            _fixture = fixture;

            // Connect directly to the SAME real Redis container the app uses, so we can inspect the keys the
            // [Cached] filter writes. No mocking/stubbing of Redis (AAP §0.10.1); RedisConnectionString is a
            // host:port string parseable by ConnectionMultiplexer.Connect.
            _redis = ConnectionMultiplexer.Connect(_fixture.RedisConnectionString);
            _db = _redis.GetDatabase();
        }

        /// <summary>
        /// Proves the <b>miss-then-hit</b> path: the first request computes the payload and awaits the Redis
        /// write (miss), and the identical second request is short-circuited by the <c>[Cached]</c> filter
        /// and served byte-for-byte from the stored Redis string (hit).
        /// </summary>
        [Fact]
        public async Task GetProducts_WhenRequestedTwiceWithSameKey_SecondResponseIsServedFromRedisCache()
        {
            // Arrange
            var marker = Guid.NewGuid().ToString("N");
            var url = $"api/products?marker={marker}";
            // Key format (CachedAttribute.GenerateCacheKeyFromRequest): request.Path then, for each query
            // param sorted by key, "|{key}-{value}". A single "marker" param yields "/api/products|marker-{m}".
            var expectedKey = $"/api/products|marker-{marker}";
            var client = _fixture.CreateClient();

            // Pre-condition: the unique marker guarantees the key does not yet exist (a genuine miss).
            _db.KeyExists(expectedKey).Should()
                .BeFalse("the per-test unique marker guarantees a genuine cache miss on the first request");

            // Act (miss): the controller action runs and the filter awaits the Redis write before returning.
            var resp1 = await client.GetAsync(url);
            var body1 = await resp1.Content.ReadAsStringAsync();

            // Assert: the miss returned 200 and populated Redis (write is awaited — no sleep needed).
            resp1.StatusCode.Should().Be(HttpStatusCode.OK);
            _db.KeyExists(expectedKey).Should()
                .BeTrue("the [Cached] filter awaits CacheResponseAsync before the HTTP response returns");
            string cached = _db.StringGet(expectedKey);
            cached.Should().NotBeNullOrEmpty();

            // Act (hit): the identical request resolves to the SAME key and is served from Redis.
            var resp2 = await client.GetAsync(url);
            var body2 = await resp2.Content.ReadAsStringAsync();

            // Assert
            resp2.StatusCode.Should().Be(HttpStatusCode.OK);
            // On a hit the filter returns a ContentResult with ContentType "application/json".
            resp2.Content.Headers.ContentType.MediaType.Should().Be("application/json");
            // The hit body is EXACTLY the string stored in Redis (proves it was served from cache, not
            // recomputed by the controller/output formatter).
            body2.Should().Be(cached, "a cache hit returns the stored Redis string verbatim");

            // Structural equivalence of miss vs hit: the miss body (MVC output formatter) and the hit body
            // (cached string) are compared structurally on the paginated `count`, NOT byte-for-byte.
            using var d1 = JsonDocument.Parse(body1);
            using var d2 = JsonDocument.Parse(body2);
            d2.RootElement.GetProperty("count").GetInt32()
                .Should().Be(d1.RootElement.GetProperty("count").GetInt32());
        }

        /// <summary>
        /// Proves the cache key is <b>deterministic and order-independent</b>: two requests whose query
        /// parameters differ only in ordering (<c>sort</c> before/after <c>marker</c>) resolve to the SAME
        /// cache entry, so the reordered second request is a hit that returns the stored Redis string.
        /// </summary>
        [Fact]
        public async Task GetProducts_WhenQueryParamsReorderedButEquivalent_ResolveToSameCacheEntry()
        {
            // Arrange
            var marker = Guid.NewGuid().ToString("N");
            var urlA = $"api/products?sort=priceAsc&marker={marker}";
            var urlB = $"api/products?marker={marker}&sort=priceAsc";
            // Params are sorted alphabetically by key when building the cache key: "marker" < "sort".
            var expectedKey = $"/api/products|marker-{marker}|sort-priceAsc";
            var client = _fixture.CreateClient();

            // Pre-condition: genuine miss for this unique key.
            _db.KeyExists(expectedKey).Should()
                .BeFalse("the per-test unique marker guarantees a genuine cache miss on the first request");

            // Act (miss → writes cache).
            var respA = await client.GetAsync(urlA);

            // Assert the first request populated the deterministic, order-independent key.
            respA.StatusCode.Should().Be(HttpStatusCode.OK);
            _db.KeyExists(expectedKey).Should()
                .BeTrue("the cache key sorts query params by name, so param order does not change the key");
            string cached = _db.StringGet(expectedKey);
            cached.Should().NotBeNullOrEmpty();

            // Act (reordered params → SAME key → hit).
            var respB = await client.GetAsync(urlB);
            var bodyB = await respB.Content.ReadAsStringAsync();

            // Assert: the reordered request hit the SAME cache entry and returned the stored string.
            respB.StatusCode.Should().Be(HttpStatusCode.OK);
            bodyB.Should().Be(cached,
                "reordered-but-equivalent query params resolve to the same deterministic cache key");
        }

        /// <summary>
        /// Proves that <b>genuinely different query parameters resolve to different cache entries</b>: two
        /// requests differing only by <c>pageSize</c> produce two distinct keys, each holding a payload whose
        /// <c>data</c> array length reflects its own <c>pageSize</c> (2 vs 4), confirming they are separate
        /// cache entries rather than aliases.
        /// </summary>
        [Fact]
        public async Task GetProducts_WhenQueryParamsDiffer_ResolveToDifferentCacheEntries()
        {
            // Arrange
            var marker = Guid.NewGuid().ToString("N");
            var urlSmall = $"api/products?marker={marker}&pageSize=2";
            var urlLarge = $"api/products?marker={marker}&pageSize=4";
            // Params sorted by key: "marker" < "pageSize".
            var keySmall = $"/api/products|marker-{marker}|pageSize-2";
            var keyLarge = $"/api/products|marker-{marker}|pageSize-4";
            var client = _fixture.CreateClient();

            // Act: both requests are misses that each populate their own cache entry.
            (await client.GetAsync(urlSmall)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.GetAsync(urlLarge)).StatusCode.Should().Be(HttpStatusCode.OK);

            // Assert
            keySmall.Should().NotBe(keyLarge, "differing pageSize values produce distinct cache keys");
            _db.KeyExists(keySmall).Should().BeTrue();
            _db.KeyExists(keyLarge).Should().BeTrue();

            // Confirm the two entries hold genuinely different payloads (seeded total is 18 products, so
            // pageSize=2 → 2 items and pageSize=4 → 4 items in the paginated `data` array).
            using var small = JsonDocument.Parse((string)_db.StringGet(keySmall));
            using var large = JsonDocument.Parse((string)_db.StringGet(keyLarge));
            small.RootElement.GetProperty("data").GetArrayLength().Should().Be(2);
            large.RootElement.GetProperty("data").GetArrayLength().Should().Be(4);
        }

        /// <summary>
        /// Proves that a <b>non-200 response is not cached</b>: <c>GET api/products/{invalidId}</c> returns a
        /// <c>404</c> via <c>NotFound(new ApiResponse(404))</c> (a <c>NotFoundObjectResult</c>, not an
        /// <c>OkObjectResult</c>), so the <c>[Cached]</c> filter writes nothing to Redis and a repeat request
        /// re-reaches the controller and again returns <c>404</c>.
        /// </summary>
        [Fact]
        public async Task GetProductById_WhenProductNotFound_ResponseIsNotCached()
        {
            // Arrange
            var marker = Guid.NewGuid().ToString("N");
            var invalidId = 999999; // large id guaranteed absent from the 18 seeded products
            var url = $"api/products/{invalidId}?marker={marker}";
            var expectedKey = $"/api/products/{invalidId}|marker-{marker}";
            var client = _fixture.CreateClient();

            // Pre-condition: genuine miss for this unique key.
            _db.KeyExists(expectedKey).Should()
                .BeFalse("the per-test unique marker guarantees the key does not yet exist");

            // Act
            var resp = await client.GetAsync(url);

            // Assert
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
            // Core assertion: the filter caches only an OkObjectResult, so the 404 wrote nothing to Redis.
            _db.KeyExists(expectedKey).Should()
                .BeFalse("only an OkObjectResult is cached; a 404 NotFoundObjectResult is never written");

            // Reinforcement: because nothing was cached, a second identical request re-reaches the controller
            // and again returns 404 (it is not short-circuited by a stale cache entry).
            var resp2 = await client.GetAsync(url);
            resp2.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        /// <summary>
        /// Disposes the inspection-only Redis multiplexer opened in the constructor. xUnit builds a new
        /// instance of this class per <c>[Fact]</c>, so exactly one multiplexer is opened and closed per test.
        /// </summary>
        public void Dispose() => _redis?.Dispose();
    }
}
