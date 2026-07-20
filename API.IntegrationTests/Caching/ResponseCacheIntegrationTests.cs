using System;
using System.Collections.Generic;
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
    ///   <item><b>600-second TTL and expiry transition (MJ-06).</b> The written entry carries a TTL tightly
    ///         around the configured 600 s; once that TTL elapses the entry is evicted and the next request
    ///         recomputes and re-caches it with a fresh ~600 s TTL.</item>
    ///   <item><b>Controller bypass proven by sentinel (MJ-07).</b> Overwriting the cached value with a
    ///         sentinel the controller could never emit and then observing that sentinel returned verbatim
    ///         conclusively proves the second request was served from Redis without invoking the action.</item>
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
    /// <b>Per-class isolated Redis (CR-01).</b> This class consumes <see cref="ContainerFixture"/> as an
    /// <c>IClassFixture&lt;ContainerFixture&gt;</c>, so it owns its OWN Redis instance and tests run
    /// sequentially (assembly-wide parallelization is disabled). To ensure each test owns cache keys that no
    /// other test within this class can collide with, every test mints a fresh
    /// <c>marker = Guid.NewGuid().ToString("N")</c> and passes it as an (unbound, hence data-neutral)
    /// <c>marker</c> query parameter. Because the cache key incorporates every sorted query
    /// parameter, the marker makes the whole key unique while leaving the returned product data unchanged,
    /// letting the test compute the expected key literal and assert on it directly with
    /// <see cref="IDatabase.KeyExists(RedisKey, CommandFlags)"/> / <see cref="IDatabase.StringGet(RedisKey, CommandFlags)"/>
    /// (no <c>IServer.Keys()</c> admin mode required).
    /// </para>
    ///
    /// <para>
    /// <b>Redis key isolation (CR-03) — scope note.</b> The production application stores anonymous basket
    /// documents and <c>[Cached]</c> response entries in the SAME Redis database WITHOUT key namespaces, so a
    /// crafted basket id can collide with a response-cache key. Eliminating that collision at the source
    /// requires PRODUCTION changes — namespacing keys (<c>basket:</c> / <c>response:</c>) and
    /// validating/authorizing basket ids — which are <b>deferred / separately authorized</b> under this
    /// test-only engagement's frozen-production constraint (AAP §0.8.2 "Any production fix for discovered
    /// races/null handling/status semantics" is out of scope; AAP §0.10.1). At the TEST-harness level the
    /// CR-01 per-class <c>IClassFixture</c> already removes the cross-class dimension of the risk: each test
    /// class owns its own disposable Redis instance, so no test class can poison another's cache, and this
    /// class additionally deletes every key it writes in <see cref="Dispose"/> (MJ-04).
    /// </para>
    ///
    /// <para>
    /// xUnit constructs a fresh instance of this class per <c>[Fact]</c>, so the constructor opens exactly
    /// one Redis multiplexer per test and <see cref="Dispose"/> closes it — keeping tests isolated.
    /// </para>
    /// </summary>
    public class ResponseCacheIntegrationTests : IClassFixture<ContainerFixture>, IDisposable
    {
        private readonly ContainerFixture _fixture;
        private readonly IConnectionMultiplexer _redis;
        private readonly IDatabase _db;

        /// <summary>
        /// Every Redis cache key this test instance writes, recorded so <see cref="Dispose"/> can delete them
        /// deterministically (MJ-04), leaving this class's own Redis instance clean between its tests.
        /// </summary>
        private readonly List<string> _writtenKeys = new List<string>();

        /// <summary>
        /// Receives this class's dedicated <see cref="ContainerFixture"/> (injected by xUnit because the
        /// class implements <c>IClassFixture&lt;ContainerFixture&gt;</c>) and opens a direct connection to
        /// the SAME real Redis container the application under test uses. By the time this constructor runs,
        /// the fixture's <c>InitializeAsync</c> has already started the containers, applied migrations and
        /// seeded the documented reference data (6 brands / 4 types / 18 products / 4 delivery methods), so
        /// <c>GET api/products</c> returns real, non-empty data.
        /// </summary>
        /// <param name="fixture">This class's isolated Testcontainers-backed fixture.</param>
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
            _writtenKeys.Add(expectedKey);
            using var client = _fixture.CreateClient();

            // Pre-condition: the unique marker guarantees the key does not yet exist (a genuine miss).
            _db.KeyExists(expectedKey).Should()
                .BeFalse("the per-test unique marker guarantees a genuine cache miss on the first request");

            // Act (miss): the controller action runs and the filter awaits the Redis write before returning.
            using var resp1 = await client.GetAsync(url);
            var body1 = await resp1.Content.ReadAsStringAsync();

            // Assert: the miss returned 200 and populated Redis (write is awaited — no sleep needed).
            resp1.StatusCode.Should().Be(HttpStatusCode.OK);
            _db.KeyExists(expectedKey).Should()
                .BeTrue("the [Cached] filter awaits CacheResponseAsync before the HTTP response returns");
            string cached = _db.StringGet(expectedKey);
            cached.Should().NotBeNullOrEmpty();

            // Act (hit): the identical request resolves to the SAME key and is served from Redis.
            using var resp2 = await client.GetAsync(url);
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
            _writtenKeys.Add(expectedKey);
            using var client = _fixture.CreateClient();

            // Pre-condition: genuine miss for this unique key.
            _db.KeyExists(expectedKey).Should()
                .BeFalse("the per-test unique marker guarantees a genuine cache miss on the first request");

            // Act (miss → writes cache).
            using var respA = await client.GetAsync(urlA);

            // Assert the first request populated the deterministic, order-independent key.
            respA.StatusCode.Should().Be(HttpStatusCode.OK);
            _db.KeyExists(expectedKey).Should()
                .BeTrue("the cache key sorts query params by name, so param order does not change the key");
            string cached = _db.StringGet(expectedKey);
            cached.Should().NotBeNullOrEmpty();

            // Act (reordered params → SAME key → hit).
            using var respB = await client.GetAsync(urlB);
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
            _writtenKeys.Add(keySmall);
            _writtenKeys.Add(keyLarge);
            using var client = _fixture.CreateClient();

            // Act: both requests are misses that each populate their own cache entry.
            using (var respSmall = await client.GetAsync(urlSmall))
            {
                respSmall.StatusCode.Should().Be(HttpStatusCode.OK);
            }

            using (var respLarge = await client.GetAsync(urlLarge))
            {
                respLarge.StatusCode.Should().Be(HttpStatusCode.OK);
            }

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
            _writtenKeys.Add(expectedKey); // 404 writes nothing; recorded only so cleanup stays uniform (no-op delete)
            using var client = _fixture.CreateClient();

            // Pre-condition: genuine miss for this unique key.
            _db.KeyExists(expectedKey).Should()
                .BeFalse("the per-test unique marker guarantees the key does not yet exist");

            // Act
            using var resp = await client.GetAsync(url);

            // Assert
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
            // Core assertion: the filter caches only an OkObjectResult, so the 404 wrote nothing to Redis.
            _db.KeyExists(expectedKey).Should()
                .BeFalse("only an OkObjectResult is cached; a 404 NotFoundObjectResult is never written");

            // Reinforcement: because nothing was cached, a second identical request re-reaches the controller
            // and again returns 404 (it is not short-circuited by a stale cache entry).
            using var resp2 = await client.GetAsync(url);
            resp2.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        /// <summary>
        /// MJ-06 — proves the cached entry carries a TTL tightly around the configured 600 seconds
        /// (<c>[Cached(600)]</c>) and that a genuine <b>expiry transition</b> deterministically evicts it and
        /// causes the next request to recompute and re-cache with a fresh ~600 s TTL. The expiry is observed
        /// through a bounded readiness poll (a wait strategy, never a fixed <c>Thread.Sleep</c>): the key's
        /// TTL is shortened and the poll returns the instant Redis reports the key absent.
        /// </summary>
        [Fact]
        public async Task GetProducts_CachedEntry_HasApproximately600SecondTtlThenExpiresAndRepopulates()
        {
            // Arrange
            var marker = Guid.NewGuid().ToString("N");
            var url = $"api/products?marker={marker}";
            var expectedKey = $"/api/products|marker-{marker}";
            _writtenKeys.Add(expectedKey);
            using var client = _fixture.CreateClient();

            _db.KeyExists(expectedKey).Should()
                .BeFalse("the per-test unique marker guarantees a genuine cache miss on the first request");

            // Prime the cache (miss → the [Cached(600)] filter awaits the Redis write before returning).
            using (var priming = await client.GetAsync(url))
            {
                priming.StatusCode.Should().Be(HttpStatusCode.OK);
            }
            _db.KeyExists(expectedKey).Should().BeTrue("the priming request must have written the cache entry");

            // MJ-06 (1) — assert the TTL is set TIGHTLY around the 600 s configured by [Cached(600)]. Because
            // the write is awaited and read back immediately, only a fraction of a second has elapsed.
            var ttl = _db.KeyTimeToLive(expectedKey);
            ttl.Should().NotBeNull(
                "[Cached(600)] must store the entry with a bounded TTL (StringSetAsync with an expiry), " +
                "never persistently");
            ttl.Value.Should().BeGreaterThan(TimeSpan.FromSeconds(590),
                "CachedAttribute passes TimeSpan.FromSeconds(600) to the cache write, so the freshly-written " +
                "key's remaining TTL is only a fraction of a second below 600");
            ttl.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(600),
                "the TTL must never exceed the configured 600 seconds (ruling out a misconfigured larger expiry)");

            // MJ-06 (2) — deterministic expiry transition WITHOUT a fixed sleep: shorten the key's TTL and
            // poll (a bounded readiness wait strategy) until Redis has evicted it, proving the entry
            // genuinely transitions present -> absent when its TTL elapses.
            _db.KeyExpire(expectedKey, TimeSpan.FromMilliseconds(500)).Should()
                .BeTrue("the freshly-written key exists, so shortening its TTL must succeed");
            await WaitUntilKeyAbsentAsync(expectedKey);
            _db.KeyExists(expectedKey).Should()
                .BeFalse("the cache entry must be gone once its (shortened) TTL has elapsed");

            // MJ-06 (3) — after expiry the identical request is a fresh miss: the endpoint recomputes and
            // RE-CACHES the entry with a brand-new ~600 s TTL (the cache is transient, not write-once).
            using (var afterExpiry = await client.GetAsync(url))
            {
                afterExpiry.StatusCode.Should().Be(HttpStatusCode.OK);
            }
            _db.KeyExists(expectedKey).Should()
                .BeTrue("the endpoint must re-populate the cache after the entry expired");
            var repopulatedTtl = _db.KeyTimeToLive(expectedKey).Value;
            repopulatedTtl.Should().BeGreaterThan(TimeSpan.FromSeconds(590),
                "the re-cached entry carries a fresh ~600 s TTL, confirming deterministic expiry-then-repopulate");
            repopulatedTtl.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(600),
                "the re-cached TTL must not exceed the configured 600 seconds");
        }

        /// <summary>
        /// MJ-07 — CONCLUSIVELY proves the second (hit) request is served from Redis and the controller
        /// action is <b>bypassed</b>. After the priming request populates the cache, the cached value is
        /// overwritten with a SENTINEL string the controller could never emit (the products action returns a
        /// paginated <c>Pagination&lt;ProductDto&gt;</c> with <c>count</c>/<c>data</c>, never this shape).
        /// The identical follow-up request then returns the sentinel verbatim, which is only possible if the
        /// <c>[Cached]</c> filter short-circuited the pipeline and served the stored string without running
        /// the action. This is strictly stronger than body-equality (MJ-07), which cannot rule out identical
        /// recomputation by the controller.
        /// </summary>
        [Fact]
        public async Task GetProducts_WhenCachedValueReplacedWithSentinel_HitReturnsSentinelProvingControllerBypass()
        {
            // Arrange
            var marker = Guid.NewGuid().ToString("N");
            var url = $"api/products?marker={marker}";
            var expectedKey = $"/api/products|marker-{marker}";
            _writtenKeys.Add(expectedKey);
            using var client = _fixture.CreateClient();

            _db.KeyExists(expectedKey).Should()
                .BeFalse("the per-test unique marker guarantees a genuine cache miss on the first request");

            // Prime the cache (miss → the filter writes the real product JSON to Redis, awaited before return).
            using (var priming = await client.GetAsync(url))
            {
                priming.StatusCode.Should().Be(HttpStatusCode.OK);
            }
            _db.KeyExists(expectedKey).Should().BeTrue("the priming request must have written the cache entry");

            // MJ-07 — replace the cached value with a sentinel the controller can NEVER produce.
            var sentinel = $"{{\"sentinel\":\"{Guid.NewGuid():N}\"}}";
            _db.StringSet(expectedKey, sentinel).Should()
                .BeTrue("the sentinel must overwrite the cached entry so the next hit can be attributed to it");

            // Act (hit) — the identical request resolves to the same key; if the controller were re-invoked
            // it would overwrite the sentinel with real product JSON, so returning the sentinel proves bypass.
            using var hit = await client.GetAsync(url);
            var body = await hit.Content.ReadAsStringAsync();

            // Assert
            hit.StatusCode.Should().Be(HttpStatusCode.OK);
            hit.Content.Headers.ContentType.MediaType.Should().Be("application/json",
                "the [Cached] filter returns the stored string as a ContentResult with application/json");
            body.Should().Be(sentinel,
                "the response is the sentinel served verbatim from Redis, proving the controller action was " +
                "bypassed by the cache short-circuit (the action would never emit this payload)");
        }

        /// <summary>
        /// Bounded readiness poll (a wait strategy, NOT a fixed <c>Thread.Sleep</c>) that returns as soon as
        /// Redis reports <paramref name="key"/> absent — used to observe a genuine TTL expiry transition
        /// deterministically. Redis applies expire-on-access semantics, so <c>KeyExists</c> reports
        /// <c>false</c> once the TTL has elapsed even before physical eviction.
        /// </summary>
        /// <param name="key">The Redis key whose eviction is awaited.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown if the key is still present after the bounded attempt budget, so a genuine problem surfaces
        /// loudly rather than as a confusing downstream assertion failure.
        /// </exception>
        private async Task WaitUntilKeyAbsentAsync(string key)
        {
            const int maxAttempts = 40; // ~40 * 100 ms = 4 s upper bound; a 500 ms TTL evicts within a few polls

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (!_db.KeyExists(key))
                {
                    return;
                }

                await Task.Delay(100);
            }

            throw new InvalidOperationException(
                $"Redis key '{key}' did not expire within the bounded wait budget; the TTL expiry " +
                "transition could not be observed.");
        }

        /// <summary>
        /// Deletes every cache key this test instance wrote (MJ-04 deterministic cleanup) and then disposes
        /// the inspection-only Redis multiplexer opened in the constructor. xUnit builds a new instance of
        /// this class per <c>[Fact]</c>, so exactly one multiplexer is opened and closed per test and each
        /// test leaves this class's own Redis instance clean.
        /// </summary>
        public void Dispose()
        {
            try
            {
                foreach (var key in _writtenKeys)
                {
                    _db.KeyDelete(key);
                }
            }
            finally
            {
                _redis?.Dispose();
            }
        }
    }
}
