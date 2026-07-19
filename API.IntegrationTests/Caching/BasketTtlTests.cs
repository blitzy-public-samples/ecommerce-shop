using System;
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
    /// <b>Isolation.</b> The shared <c>"Integration"</c> collection runs sequentially, but to guarantee
    /// keys never collide with sibling tests every test uses a fresh, unique basket id
    /// (<c>basket-ttl-{Guid:N}</c>) and asserts against that exact key. No <c>FLUSHDB</c> and no
    /// deletion of other keys is ever performed.
    /// </para>
    ///
    /// <para>
    /// Naming follows the repository convention <c>MethodName_StateUnderTest_ExpectedBehavior</c>,
    /// with an Arrange-Act-Assert structure and FluentAssertions (AAP §0.10.2).
    /// </para>
    /// </summary>
    [Collection("Integration")]
    public class BasketTtlTests : IDisposable
    {
        /// <summary>
        /// The shared, already-started/migrated/seeded Testcontainers fixture injected by xUnit for
        /// every class in the <c>"Integration"</c> collection. Provides the in-process HTTP client
        /// factory and the real Redis endpoint the application writes to.
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
        /// Constructs the test class. xUnit injects the shared <see cref="ContainerFixture"/> (the
        /// containers are already running, migrated and seeded before this constructor executes).
        /// </summary>
        /// <param name="fixture">The shared integration fixture supplying the HTTP client and Redis endpoint.</param>
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
            // Arrange — a unique key so this test never collides with siblings in the shared collection.
            var basketId = $"basket-ttl-{Guid.NewGuid():N}";
            using var content = BuildOneItemBasketContent(basketId);
            var client = _fixture.CreateClient();

            // Pre-condition: the key must not exist before the POST (a fresh, unique id in real Redis).
            _db.KeyExists(basketId).Should().BeFalse("a freshly-generated basket id must not exist in Redis yet");

            // Act — post the basket through the genuine ASP.NET Core pipeline. UpdateBasketAsync awaits
            // the Redis write before this response returns, so the key/TTL are queryable immediately.
            var response = await client.PostAsync("api/basket", content);

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
            using var content = BuildOneItemBasketContent(basketId);
            var client = _fixture.CreateClient();

            var postResponse = await client.PostAsync("api/basket", content);
            postResponse.StatusCode.Should().Be(HttpStatusCode.OK, "the one-item basket satisfies all model-validation rules");

            // Act — read the basket back through the pipeline; it is re-read from real Redis by the controller.
            var getResp = await client.GetAsync($"api/basket?id={basketId}");
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
        /// Disposes the dedicated Redis multiplexer opened by this test class so no connection leaks
        /// between tests. The shared containers themselves are owned and disposed by <see cref="ContainerFixture"/>.
        /// </summary>
        public void Dispose() => _redis?.Dispose();
    }
}
