using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using API.IntegrationTests.Infrastructure;
using FluentAssertions;
using StackExchange.Redis;
using Xunit;

namespace API.IntegrationTests.Caching
{
    /// <summary>
    /// Integration tests that prove the customer-basket <b>time-to-live (TTL)</b> semantics
    /// end-to-end against a <b>real</b> Redis instance provisioned by Testcontainers and driven
    /// through the genuine ASP.NET Core HTTP pipeline (AAP §0.4.1, §0.5.1, §0.10.1).
    ///
    /// <para>
    /// <b>What is under test.</b> The production repository
    /// <c>Infrastructure/Data/BasketRepository.UpdateBasketAsync</c> persists a basket with
    /// <c>await _database.StringSetAsync(basket.Id, JsonSerializer.Serialize(basket),
    /// TimeSpan.FromDays(30))</c>. Consequently, after a successful <c>POST api/basket</c>, the
    /// basket must exist in Redis under the <b>verbatim key <c>basket.Id</c></b> (no prefix /
    /// namespace) with an expiry of <b>~30 days</b>. These tests assert exactly that by inspecting
    /// the same real Redis container the application writes to.
    /// </para>
    ///
    /// <para>
    /// <b>Real infrastructure only (AAP §0.10.1).</b> Redis is never mocked or stubbed here — a
    /// direct <see cref="StackExchange.Redis.ConnectionMultiplexer"/> is opened against the exact
    /// dynamic endpoint the application under test uses (<see cref="ContainerFixture.RedisConnectionString"/>),
    /// so <c>KeyExists</c>/<c>KeyTimeToLive</c> observe the real key that <c>BasketRepository</c>
    /// wrote. Neither the shared <c>docker-compose.yml</c> Redis nor any live Stripe endpoint is used.
    /// </para>
    ///
    /// <para>
    /// <b>No sleeping, no waiting for real expiry (AAP §0.10.1).</b> The suite never waits 30 days
    /// (nor any fixed <see cref="System.Threading.Thread"/> sleep). <c>UpdateBasketAsync</c> awaits
    /// the Redis write <i>before</i> the HTTP response returns, so the key and its TTL are queryable
    /// immediately after the <c>POST</c> completes. The tests assert the <b>configured</b> ~30-day
    /// TTL is applied (a non-null, bounded expiry proves the key is volatile and will eventually
    /// expire rather than persist forever), which keeps them deterministic and flake-free.
    /// </para>
    ///
    /// <para>
    /// <b>Isolation (CR-01) + deterministic cleanup (MJ-04).</b> This class consumes
    /// <see cref="ContainerFixture"/> as an <c>IClassFixture&lt;ContainerFixture&gt;</c>, so it owns its own
    /// Redis instance and runs sequentially (assembly-wide parallelization is disabled). To guarantee keys
    /// never collide with sibling tests within this class, every test uses a fresh, unique basket id
    /// (<c>basket-ttl-{Guid:N}</c>) and asserts against that exact key. Each test records the key it writes
    /// and <see cref="Dispose"/> deletes ONLY those keys — never a <c>FLUSHDB</c> and never any other key.
    /// </para>
    ///
    /// <para>
    /// <b>Redis key isolation (CR-03) — scope note.</b> <c>BasketRepository</c> stores baskets under the
    /// verbatim, un-namespaced key <c>basket.Id</c>, which shares one Redis database with the <c>[Cached]</c>
    /// response entries, so a crafted basket id can collide with a response-cache key. Namespacing keys
    /// (<c>basket:</c> / <c>response:</c>) and validating/authorizing basket ids are PRODUCTION changes,
    /// <b>deferred / separately authorized</b> under this test-only engagement's frozen-production constraint
    /// (AAP §0.8.2, §0.10.1). At the harness level the CR-01 per-class fixture already removes the
    /// cross-class dimension (each class owns its own disposable Redis), and this class cleans up every key
    /// it writes (MJ-04).
    /// </para>
    ///
    /// <para>
    /// Naming follows the repository convention <c>MethodName_StateUnderTest_ExpectedBehavior</c>,
    /// with an Arrange-Act-Assert structure and FluentAssertions (AAP §0.10.2).
    /// </para>
    /// </summary>
    public class BasketTtlTests : IClassFixture<ContainerFixture>, IDisposable
    {
        /// <summary>
        /// This class's dedicated, already-started/migrated/seeded Testcontainers fixture injected by xUnit
        /// (via <c>IClassFixture&lt;ContainerFixture&gt;</c>). Provides the in-process HTTP client factory
        /// and the real Redis endpoint the application writes to.
        /// </summary>
        private readonly ContainerFixture _fixture;

        /// <summary>
        /// A dedicated <see cref="IConnectionMultiplexer"/> opened directly against the SAME real Redis
        /// container the application under test uses, so the tests can inspect the basket key and its
        /// TTL exactly as <c>BasketRepository</c> wrote them. Disposed in <see cref="Dispose"/>.
        /// </summary>
        private readonly IConnectionMultiplexer _redis;

        /// <summary>The default (index 0) Redis database — the same database the application uses.</summary>
        private readonly IDatabase _db;

        /// <summary>
        /// Every basket (Redis) key this test instance writes, recorded so <see cref="Dispose"/> can delete
        /// them deterministically (MJ-04), leaving this class's own Redis instance clean between its tests.
        /// </summary>
        private readonly List<string> _writtenKeys = new List<string>();

        /// <summary>
        /// Constructs the test class. xUnit injects this class's dedicated <see cref="ContainerFixture"/>
        /// (the containers are already running, migrated and seeded before this constructor executes).
        /// </summary>
        /// <param name="fixture">This class's isolated integration fixture supplying the HTTP client and Redis endpoint.</param>
        public BasketTtlTests(ContainerFixture fixture)
        {
            _fixture = fixture;

            // Connect directly to the SAME real Redis container the app uses so we can inspect the
            // basket key + its TTL that BasketRepository writes. No mocking (AAP §0.10.1). The fixture
            // has already awaited Redis readiness via a wait strategy, so this connect succeeds promptly.
            _redis = ConnectionMultiplexer.Connect(_fixture.RedisConnectionString);
            _db = _redis.GetDatabase();
        }

        /// <summary>
        /// Builds a fully-valid one-item <c>CustomerBasketDto</c> payload as <c>application/json</c>
        /// content for a <c>POST api/basket</c>. Every <c>BasketItemDto</c> field is populated and the
        /// <c>Price</c>/<c>Quantity</c> values satisfy the <c>[Range]</c> rules, so the <c>[ApiController]</c>
        /// automatic model validation passes (the request returns <c>200</c>, never a <c>400</c>). The
        /// camelCase body binds correctly because ASP.NET Core's JSON input formatter is case-insensitive.
        /// </summary>
        /// <param name="basketId">The unique basket id used as both the DTO id and the Redis key.</param>
        /// <returns>Disposable <see cref="HttpContent"/> carrying the serialized basket JSON.</returns>
        private static HttpContent BuildOneItemBasketContent(string basketId)
        {
            var payload = new
            {
                id = basketId,
                items = new[]
                {
                    new
                    {
                        id = 1,
                        productName = "Integration Test Product",
                        price = 12.50m,
                        quantity = 2,
                        pictureUrl = "https://localhost/images/products/test.png",
                        brand = "TestBrand",
                        type = "TestType"
                    }
                },
                shippingPrice = 0m
            };

            // System.Text.Json serializes decimal literals (12.50m, 0m) as JSON numbers — do NOT send
            // Price as a string, or [Range]/model binding would reject it.
            var json = JsonSerializer.Serialize(payload);
            return new StringContent(json, Encoding.UTF8, "application/json");
        }

        /// <summary>
        /// PRIMARY assertion: posting a basket via the real HTTP pipeline stores it in the real Redis
        /// container under the verbatim key <c>basket.Id</c> with the configured ~30-day TTL.
        /// <list type="bullet">
        ///   <item><c>POST api/basket</c> returns <c>200 OK</c>.</item>
        ///   <item>The key <c>basketId</c> now exists in real Redis (written by <c>StringSetAsync</c>).</item>
        ///   <item><c>KeyTimeToLive</c> is non-null and its <c>TotalDays</c> lies in <c>[29, 30]</c> —
        ///         i.e. the <c>TimeSpan.FromDays(30)</c> expiry is applied (with a small tolerance for the
        ///         few milliseconds elapsed since the write). This is the core requirement.</item>
        /// </list>
        /// </summary>
        [Fact]
        public async Task UpdateBasketAsync_WhenBasketPostedViaApi_StoresKeyWith30DayTtl()
        {
            // Arrange — a unique key so this test never collides with sibling tests in this class.
            var basketId = $"basket-ttl-{Guid.NewGuid():N}";
            _writtenKeys.Add(basketId);
            using var content = BuildOneItemBasketContent(basketId);
            using var client = _fixture.CreateClient();

            // Pre-condition: the key must not exist before the POST (a fresh, unique id in real Redis).
            _db.KeyExists(basketId).Should().BeFalse("a freshly-generated basket id must not exist in Redis yet");

            // Act — post the basket through the genuine ASP.NET Core pipeline. UpdateBasketAsync awaits
            // the Redis write before this response returns, so the key/TTL are queryable immediately.
            using var response = await client.PostAsync("api/basket", content);

            // Assert — the write succeeded and the ~30-day TTL was applied to the verbatim basket key.
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            _db.KeyExists(basketId).Should()
                .BeTrue("BasketRepository.UpdateBasketAsync stores the basket in real Redis under the key basket.Id");

            TimeSpan? ttl = _db.KeyTimeToLive(basketId);
            ttl.Should().NotBeNull("the basket key must carry an expiry, not be persistent");
            ttl.Value.TotalDays.Should()
                .BeInRange(29, 30, "UpdateBasketAsync applies TimeSpan.FromDays(30) (tolerance for elapsed time since the write)");
        }

        /// <summary>
        /// Round-trip + expiry-semantics: after posting a basket, <c>GET api/basket?id=</c> returns the
        /// same basket read back from real Redis, and the stored key is volatile (expiring), not persistent.
        /// <list type="bullet">
        ///   <item><c>POST api/basket</c> returns <c>200 OK</c>, then <c>GET api/basket?id={basketId}</c>
        ///         returns <c>200 OK</c>.</item>
        ///   <item>The (camelCase) response body echoes the same <c>id</c> and exactly one <c>items</c>
        ///         entry — proving the basket round-tripped through genuine Redis, not an empty fallback.</item>
        ///   <item><c>KeyTimeToLive</c> is non-null with <c>TotalDays</c> in <c>[29, 30]</c>, proving the
        ///         key will expire rather than persist forever — asserted WITHOUT waiting for real expiry.</item>
        /// </list>
        /// </summary>
        [Fact]
        public async Task PostBasketThenGetBasket_WhenBasketPersistedInRealRedis_ReturnsSameBasketWithExpiringKey()
        {
            // Arrange — unique key; post the one-item basket and confirm it was accepted.
            var basketId = $"basket-ttl-{Guid.NewGuid():N}";
            _writtenKeys.Add(basketId);
            using var content = BuildOneItemBasketContent(basketId);
            using var client = _fixture.CreateClient();

            using var postResponse = await client.PostAsync("api/basket", content);
            postResponse.StatusCode.Should().Be(HttpStatusCode.OK, "the one-item basket satisfies all model-validation rules");

            // Act — read the basket back through the pipeline; it is re-read from real Redis by the controller.
            using var getResp = await client.GetAsync($"api/basket?id={basketId}");
            var getBody = await getResp.Content.ReadAsStringAsync();

            // Assert (round-trip through real Redis) — the persisted basket comes back with its id + item.
            getResp.StatusCode.Should().Be(HttpStatusCode.OK);

            using var doc = JsonDocument.Parse(getBody);
            doc.RootElement.GetProperty("id").GetString().Should()
                .Be(basketId, "GET returns the SAME basket that was persisted in real Redis, not an empty fallback");
            doc.RootElement.GetProperty("items").GetArrayLength().Should()
                .Be(1, "the single posted item round-tripped through real Redis");

            // Assert (expiry semantics without sleeping) — a non-null, bounded TTL proves the key is
            // volatile (will expire) rather than persistent; we deliberately do NOT wait 30 days.
            TimeSpan? ttl = _db.KeyTimeToLive(basketId);
            ttl.Should().NotBeNull("the round-tripped basket key must be expiring, not persistent");
            ttl.Value.TotalDays.Should()
                .BeInRange(29, 30, "the configured ~30-day TTL is applied to the basket key in real Redis");
        }

        /// <summary>
        /// ACTUAL-expiry proof (w014 FINDING F): after a basket is stored with its real ~30-day TTL, this
        /// test externally <b>shortens</b> that key's TTL and, using a bounded readiness poll (never a fixed
        /// <see cref="System.Threading.Thread"/> sleep), proves the entry genuinely transitions
        /// present -&gt; absent AND that the real application observes the resulting <b>miss</b> end-to-end
        /// (a subsequent <c>GET api/basket</c> falls back to the empty <c>new CustomerBasket(id)</c>). The
        /// configured 30-day TTL is never waited out — only a deliberately shortened, dedicated test key is
        /// expired — so the test stays fast and deterministic (AAP §0.7.2, §0.10.1). It complements the two
        /// configured-TTL tests above (which assert the ~30-day value at write time) by proving the eventual
        /// eviction + application miss that the configured expiry implies.
        /// <list type="bullet">
        ///   <item><c>POST api/basket</c> stores the basket (present: <c>GET</c> returns its single item; the
        ///         key exists; its TTL is the configured ~30 days).</item>
        ///   <item>The key's TTL is shortened externally to ~1.5 s via <c>KeyExpire</c>.</item>
        ///   <item>Bounded polling of the real <c>GET</c> endpoint observes the miss (empty basket) once the
        ///         shortened TTL elapses.</item>
        ///   <item>Native Redis then confirms the key is gone (<c>KeyExists</c> false, <c>KeyTimeToLive</c>
        ///         null).</item>
        /// </list>
        /// </summary>
        [Fact]
        public async Task PostBasket_WhenKeyTtlExternallyShortened_ApplicationObservesExpiryMiss()
        {
            // Arrange — a unique key; post the one-item basket and confirm it was persisted with a real TTL.
            var basketId = $"basket-ttl-{Guid.NewGuid():N}";
            _writtenKeys.Add(basketId);
            using var content = BuildOneItemBasketContent(basketId);
            using var client = _fixture.CreateClient();

            using (var postResponse = await client.PostAsync("api/basket", content))
            {
                postResponse.StatusCode.Should()
                    .Be(HttpStatusCode.OK, "the one-item basket satisfies all model-validation rules");
            }

            // Pre-condition: the basket is present with the configured ~30-day TTL, so the eviction below is
            // a genuine expiry of a real, live key rather than a no-op on an already-absent key.
            _db.KeyExists(basketId).Should().BeTrue("the posted basket must exist in real Redis before expiry");
            _db.KeyTimeToLive(basketId).Value.TotalDays.Should()
                .BeInRange(29, 30, "the basket is first stored with the configured ~30-day TTL");

            using (var presentGet = await client.GetAsync($"api/basket?id={basketId}"))
            {
                var presentBody = await presentGet.Content.ReadAsStringAsync();
                using var presentDoc = JsonDocument.Parse(presentBody);
                presentDoc.RootElement.GetProperty("items").GetArrayLength().Should()
                    .Be(1, "before expiry the application returns the persisted basket with its single item");
            }

            // Act — externally shorten the dedicated test key's TTL, then poll the real GET endpoint (a
            // bounded wait strategy, never a fixed sleep) until the application observes the expiry miss.
            _db.KeyExpire(basketId, TimeSpan.FromMilliseconds(1500)).Should()
                .BeTrue("the live basket key exists, so shortening its TTL must succeed");
            await WaitUntilBasketMissedAsync(client, basketId);

            // Assert — the application observed the miss end-to-end AND native Redis confirms the eviction.
            using (var afterExpiryGet = await client.GetAsync($"api/basket?id={basketId}"))
            {
                afterExpiryGet.StatusCode.Should().Be(HttpStatusCode.OK,
                    "GET api/basket returns 200 with an empty fallback basket once the key has expired");

                var afterBody = await afterExpiryGet.Content.ReadAsStringAsync();
                using var afterDoc = JsonDocument.Parse(afterBody);
                afterDoc.RootElement.GetProperty("id").GetString().Should()
                    .Be(basketId, "the empty fallback basket echoes the requested id");
                afterDoc.RootElement.GetProperty("items").GetArrayLength().Should()
                    .Be(0, "after expiry the application returns the empty new CustomerBasket(id) fallback (a genuine miss)");
            }

            _db.KeyExists(basketId).Should()
                .BeFalse("the basket key must be gone from real Redis once its (shortened) TTL elapsed");
            _db.KeyTimeToLive(basketId).Should()
                .BeNull("an expired/absent key has no remaining TTL");
        }

        /// <summary>
        /// Bounded readiness wait strategy (never a fixed <see cref="System.Threading.Thread"/> sleep) that
        /// polls the REAL <c>GET api/basket?id=</c> endpoint through the genuine pipeline until the
        /// application observes a MISS for <paramref name="basketId"/> — i.e. the controller falls back to
        /// <c>new CustomerBasket(id)</c> with an empty <c>items</c> collection because the Redis key has
        /// expired. Returns as soon as the miss is observed; throws if it is not observed within the bounded
        /// attempt budget so a genuine failure surfaces loudly instead of hanging (AAP §0.10.1).
        /// </summary>
        /// <param name="client">An HTTP client bound to the in-process application.</param>
        /// <param name="basketId">The basket id / Redis key whose expiry-driven miss is awaited.</param>
        private static async Task WaitUntilBasketMissedAsync(HttpClient client, string basketId)
        {
            const int maxAttempts = 40; // ~40 * 100 ms = 4 s upper bound; a 1500 ms TTL expires within ~15 polls

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                using var response = await client.GetAsync($"api/basket?id={basketId}");
                response.StatusCode.Should().Be(HttpStatusCode.OK,
                    "GET api/basket always returns 200 (an empty basket for an unknown/expired id)");

                var body = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.GetProperty("items").GetArrayLength() == 0)
                {
                    return; // the application observed the miss (empty fallback basket) — expiry propagated
                }

                await Task.Delay(100);
            }

            throw new InvalidOperationException(
                $"The application never observed an expiry miss for basket '{basketId}' within the bounded " +
                "wait budget; the TTL expiry -> empty-basket transition could not be verified end-to-end.");
        }

        /// <summary>
        /// Deletes every basket key this test instance wrote (MJ-04 deterministic cleanup) and then disposes
        /// the dedicated Redis multiplexer opened by this test class so no connection leaks between tests. The
        /// containers themselves are owned and disposed by this class's <see cref="ContainerFixture"/>.
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
