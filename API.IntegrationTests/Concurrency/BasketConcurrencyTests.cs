using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using API.Dtos;
using API.IntegrationTests.Infrastructure;
using Core.Entities;
using FluentAssertions;
using Xunit;

namespace API.IntegrationTests.Concurrency
{
    /// <summary>
    /// Concurrency integration tests for the basket write path, proving that firing many simultaneous
    /// <c>POST api/basket</c> requests through the <b>real</b> ASP.NET Core pipeline against the
    /// <b>real</b> Redis instance never corrupts a basket, never surfaces an unhandled exception, and
    /// always yields well-formed responses and a coherent final persisted state (AAP §0.4.2 concurrency
    /// blueprint: "20 concurrent <c>POST api/basket</c> (no unhandled exception, non-corrupted JSON)").
    ///
    /// <para>
    /// <b>What is exercised end-to-end.</b> Each request flows through the genuine middleware/routing
    /// stack into <c>API/Controllers/BasketController.UpdateBasket</c> (which has <b>no</b>
    /// <c>[Authorize]</c> attribute — basket writes require no JWT), where AutoMapper maps the
    /// <see cref="CustomerBasketDto"/> to a <see cref="CustomerBasket"/> and
    /// <c>Infrastructure/Data/BasketRepository.UpdateBasketAsync</c> serialises it to JSON and stores it in
    /// Redis with a 30-day TTL, then re-reads and deserialises the stored value before returning it. The
    /// tests therefore round-trip real data through the real Redis multiplexer (a thread-safe singleton)
    /// under genuine concurrent load.
    /// </para>
    ///
    /// <para>
    /// <b>Real infrastructure only (AAP §0.10.1).</b> Nothing is mocked: the Testcontainers Redis instance
    /// supplied by the shared <see cref="ContainerFixture"/> and the in-process HTTP transport are both
    /// genuine, and the shared <c>docker-compose</c> stack is never reused (Testcontainers assigns dynamic
    /// ports). Concurrency is driven exclusively via a single <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/>
    /// over all requests — there is no <c>Thread.Sleep</c>; the fixture guarantees container readiness via
    /// its wait strategies before any test runs. All test code is isolated in this file and no production
    /// code is modified.
    /// </para>
    ///
    /// <para>
    /// <b>Shared collection.</b> This class joins the shared <c>"Integration"</c> collection (via
    /// <c>[Collection("Integration")]</c>) so it reuses the one already-started PostgreSQL + Redis pair and
    /// the wired <see cref="CustomWebApplicationFactory"/>. It uses the anonymous
    /// <see cref="ContainerFixture.CreateClient"/> (no bearer token) because the basket endpoints are not
    /// gated by <c>[Authorize]</c>. Each test uses <b>unique GUID-based basket ids</b> so its keys never
    /// collide with sibling tests on the shared Redis instance.
    /// </para>
    ///
    /// <para>
    /// Naming follows the repository convention <c>MethodName_StateUnderTest_ExpectedBehavior</c>, with an
    /// Arrange-Act-Assert structure and FluentAssertions.
    /// </para>
    /// </summary>
    [Collection("Integration")]
    public class BasketConcurrencyTests
    {
        /// <summary>Number of simultaneous <c>POST api/basket</c> requests fired per test.</summary>
        private const int ConcurrentRequestCount = 20;

        /// <summary>
        /// Canonical per-item price used by every generated basket item. It is comfortably above the DTO's
        /// <c>[Range(0.1, ...)]</c> lower bound so model validation always passes, and it is an exactly
        /// representable decimal so equality assertions on the persisted value are stable.
        /// </summary>
        private const decimal TestItemPrice = 10.5m;

        /// <summary>
        /// Shared fixture (started once for the whole <c>"Integration"</c> collection) providing the real
        /// Testcontainers PostgreSQL + Redis and the wired in-process host. Injected by xUnit through the
        /// collection fixture.
        /// </summary>
        private readonly ContainerFixture _fixture;

        /// <summary>
        /// Receives the shared <see cref="ContainerFixture"/> from xUnit's collection-fixture machinery.
        /// </summary>
        /// <param name="fixture">The shared container fixture for the <c>"Integration"</c> collection.</param>
        public BasketConcurrencyTests(ContainerFixture fixture) => _fixture = fixture;

        /// <summary>
        /// Fires <see cref="ConcurrentRequestCount"/> concurrent <c>POST api/basket</c> requests with
        /// <b>distinct</b> basket ids and asserts that every request succeeds (no <c>500</c>, all
        /// <c>200 OK</c>) and that each response body deserialises to a well-formed
        /// <see cref="CustomerBasket"/> whose id echoes the request that produced it. Distinct keys mean
        /// there is no write contention, so the id echo is fully deterministic.
        /// </summary>
        [Fact]
        public async Task UpdateBasket_TwentyConcurrentRequestsWithDistinctIds_AllSucceedWithWellFormedBaskets()
        {
            // Arrange — a unique run prefix keeps this test's keys isolated on the shared Redis instance,
            // and 20 distinct ids guarantee no two requests contend on the same key.
            var prefix = $"basket-concurrency-{Guid.NewGuid():N}";
            var ids = Enumerable.Range(0, ConcurrentRequestCount)
                .Select(i => $"{prefix}-{i}")
                .ToList();

            using var client = _fixture.CreateClient();

            // Establish the application's lazily-connected Redis multiplexer BEFORE the concurrent burst so
            // this test measures concurrent write-safety, not one-time cold-connect behavior (see
            // WarmUpRedisConnectionAsync). This is a readiness wait strategy, never a fixed Thread.Sleep.
            await WarmUpRedisConnectionAsync(client);

            // Act — fire all posts concurrently through a SINGLE Task.WhenAll (no Thread.Sleep, one shared
            // client). WhenAll preserves the input order, so responses[i] corresponds to ids[i].
            var responses = await Task.WhenAll(
                ids.Select(id => client.PostAsJsonAsync("api/basket", BuildBasketDto(id))));

            // Assert.
            try
            {
                responses.Should().NotContain(
                    r => r.StatusCode == HttpStatusCode.InternalServerError,
                    "no concurrent basket write may surface an unhandled exception (the global " +
                    "ExceptionMiddleware would render one as a structured 500)");
                responses.Should().OnlyContain(
                    r => r.StatusCode == HttpStatusCode.OK,
                    "every concurrent basket write must complete successfully through the real pipeline " +
                    "and the real Redis instance");

                // Every response must deserialize to a well-formed CustomerBasket whose Id echoes the
                // corresponding request id (zip responses back to ids by index).
                for (var i = 0; i < ids.Count; i++)
                {
                    var basket = await responses[i].Content.ReadFromJsonAsync<CustomerBasket>();
                    AssertWellFormed(basket, ids[i]);
                }
            }
            finally
            {
                // Dispose the response messages once all bodies have been read.
                foreach (var response in responses)
                {
                    response?.Dispose();
                }
            }
        }

        /// <summary>
        /// Fires <see cref="ConcurrentRequestCount"/> concurrent <c>POST api/basket</c> requests to the
        /// <b>same</b> basket id using an <b>identical</b> payload, asserts each response is a well-formed
        /// two-item <see cref="CustomerBasket"/> (no <c>500</c>, all <c>200 OK</c>), then reads the basket
        /// back with a <c>GET</c> and asserts the final persisted state is coherent (exactly the two
        /// canonical items — never a torn, merged, or partially-written document).
        /// </summary>
        [Fact]
        public async Task UpdateBasket_TwentyConcurrentRequestsSameId_NoCorruptionAndFinalStateCoherent()
        {
            // Arrange — a single GUID-based id shared by all requests, and ONE canonical DTO reused for
            // every request. Concurrent writes to the SAME Redis key race (last-write-wins is
            // non-deterministic), but because every payload is byte-identical the final stored document is
            // identical regardless of ordering — which keeps this test deterministic. We therefore assert
            // only structural coherence, never which particular write "won".
            var basketId = $"basket-concurrency-same-{Guid.NewGuid():N}";
            var dto = BuildBasketDto(basketId, itemCount: 2);

            using var client = _fixture.CreateClient();

            // Establish the application's lazily-connected Redis multiplexer BEFORE the concurrent burst so
            // this test measures concurrent write-safety, not one-time cold-connect behavior (see
            // WarmUpRedisConnectionAsync). This is a readiness wait strategy, never a fixed Thread.Sleep.
            await WarmUpRedisConnectionAsync(client);

            // Act — fire all identical posts concurrently through a SINGLE Task.WhenAll.
            var responses = await Task.WhenAll(
                Enumerable.Range(0, ConcurrentRequestCount)
                    .Select(_ => client.PostAsJsonAsync("api/basket", dto)));

            // Assert (per-response): none failed, and each returns the coherent 2-item basket.
            try
            {
                responses.Should().NotContain(
                    r => r.StatusCode == HttpStatusCode.InternalServerError,
                    "racing writes to the same key must not degrade into an unhandled exception / 500");
                responses.Should().OnlyContain(
                    r => r.StatusCode == HttpStatusCode.OK,
                    "every concurrent write to the same basket must complete successfully");

                foreach (var response in responses)
                {
                    var basket = await response.Content.ReadFromJsonAsync<CustomerBasket>();
                    AssertWellFormed(basket, basketId);
                    basket.Items.Should().HaveCount(2,
                        "each response echoes the canonical 2-item payload, never a torn/merged document");
                }
            }
            finally
            {
                foreach (var response in responses)
                {
                    response?.Dispose();
                }
            }

            // Assert (final Redis state via GET): because every concurrent write sent an IDENTICAL payload,
            // the value persisted in Redis is deterministic despite the write race — so the stored basket
            // must be a coherent 2-item document matching the canonical payload exactly.
            using var getResponse = await client.GetAsync($"api/basket?id={basketId}");
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK,
                "the basket persisted in Redis must be retrievable through the real pipeline");

            var finalBasket = await getResponse.Content.ReadFromJsonAsync<CustomerBasket>();
            AssertWellFormed(finalBasket, basketId);
            finalBasket.Items.Should().HaveCount(2,
                "the persisted basket must contain exactly the two canonical items " +
                "(proving the stored document is not corrupted, partial, or merged)");
            finalBasket.Items.Should().OnlyContain(
                bi => bi.Quantity == 1 && bi.Price == TestItemPrice,
                "every persisted item must match the canonical payload, confirming a coherent final state");
        }

        // ----------------------------------------------------------------------------------------------
        // Private helpers
        // ----------------------------------------------------------------------------------------------

        /// <summary>
        /// Builds a fully-populated <see cref="CustomerBasketDto"/>. Every <see cref="BasketItemDto"/> field
        /// is <c>[Required]</c>, <c>Price</c> is <c>[Range(0.1, ...)]</c> and <c>Quantity</c> is
        /// <c>[Range(1, ...)]</c>, so all fields are filled with valid values; otherwise the
        /// <c>[ApiController]</c> automatic model-validation would short-circuit the request into a
        /// <c>400</c> <c>ApiValidationErrorResponose</c> body rather than a <see cref="CustomerBasket"/>.
        /// (<c>POST api/basket</c> only serialises the DTO to Redis and never touches PostgreSQL/products,
        /// so the item <c>Id</c> values need not correspond to real products.)
        /// </summary>
        /// <param name="basketId">The basket id to assign; also the Redis key the basket is stored under.</param>
        /// <param name="itemCount">Number of basket items to generate (defaults to a single item).</param>
        /// <returns>A valid basket DTO ready to POST.</returns>
        private static CustomerBasketDto BuildBasketDto(string basketId, int itemCount = 1)
        {
            return new CustomerBasketDto
            {
                Id = basketId,
                Items = Enumerable.Range(1, itemCount).Select(i => new BasketItemDto
                {
                    Id = i,                         // any positive int; basket writes don't hit the product table
                    ProductName = $"Product {i}",
                    Price = TestItemPrice,          // >= 0.1 so the Range validation passes
                    Quantity = 1,                   // >= 1 so the Range validation passes
                    PictureUrl = $"product-{i}.png",
                    Brand = "TestBrand",
                    Type = "TestType"
                }).ToList()
            };
        }

        /// <summary>
        /// Reusable coherence assertions applied to every deserialised response: the basket is non-null,
        /// its id echoes the expected id, its items collection is present, and every item is non-corrupted
        /// (a valid quantity and price). Guarantees the JSON round-trip produced a structurally sound
        /// <see cref="CustomerBasket"/> rather than a garbled or partial document.
        /// </summary>
        /// <param name="basket">The deserialised basket returned by the pipeline.</param>
        /// <param name="expectedId">The id the returned basket must carry.</param>
        private static void AssertWellFormed(CustomerBasket basket, string expectedId)
        {
            basket.Should().NotBeNull("every response body must deserialize to a non-null CustomerBasket");
            basket.Id.Should().Be(expectedId, "the returned basket must echo the requested id");
            basket.Items.Should().NotBeNull("a well-formed basket always carries an items collection");
            basket.Items.Should().OnlyContain(
                bi => bi.Quantity >= 1 && bi.Price >= 0.1m,
                "every deserialized item must be non-corrupted (a valid quantity and price)");
        }

        /// <summary>
        /// Establishes the application's Redis connection through the real pipeline BEFORE a concurrent
        /// burst, then returns once it is confirmed usable.
        ///
        /// <para>
        /// <b>Why this is required.</b> <c>Startup</c> registers the Redis <c>IConnectionMultiplexer</c> as a
        /// <b>lazily-connected singleton</b> (created with <c>AbortOnConnectFail</c>), so the multiplexer's
        /// one-time, <i>synchronous</i> <c>ConnectionMultiplexer.Connect()</c> handshake only runs on the
        /// FIRST Redis operation. Firing all twenty requests simultaneously as that very first operation
        /// races the handshake under thread-pool pressure and can exceed the connect timeout — surfacing as
        /// a spurious <c>500</c> that reflects cold-start behavior, not the concurrent write-safety this
        /// class is meant to verify. Issuing a single <b>sequential</b> readiness request first lets the
        /// handshake complete without contention, after which the connected singleton is reused by every
        /// subsequent request (including the concurrent burst). In a full-suite run the shared
        /// <see cref="ContainerFixture"/> is already warmed by earlier classes; this makes the isolated
        /// <c>--filter</c> run equally deterministic.
        /// </para>
        ///
        /// <para>
        /// <b>Constraint compliance.</b> This is a bounded, asynchronous <i>readiness wait strategy</i>
        /// (poll-until-ready) — the same philosophy the container fixture uses — not a fixed
        /// <c>Thread.Sleep</c>. It drives the genuine HTTP pipeline and the genuine Redis instance (nothing
        /// is mocked). It targets the anonymous <c>GET api/basket</c> endpoint, which always returns
        /// <c>200 OK</c> (an empty basket for an unknown id) and writes nothing, so it neither pollutes the
        /// keyspace nor depends on any seeded data.
        /// </para>
        /// </summary>
        /// <param name="client">The HTTP client bound to the in-process application.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown if the connection cannot be established within the bounded attempt budget, so a genuine
        /// infrastructure problem surfaces loudly instead of as a confusing downstream assertion failure.
        /// </exception>
        private static async Task WarmUpRedisConnectionAsync(HttpClient client)
        {
            const int maxAttempts = 15;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                using var response = await client.GetAsync($"api/basket?id=warmup-{Guid.NewGuid():N}");
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    // The multiplexer completed its one-time connect; the singleton is now connected and
                    // cached, so the upcoming concurrent burst reuses it without re-handshaking.
                    return;
                }

                // Brief asynchronous back-off between readiness polls (a wait strategy, NOT a fixed sleep);
                // the loop exits immediately once the connection is usable.
                await Task.Delay(250);
            }

            throw new InvalidOperationException(
                $"The application's Redis connection could not be established through the pipeline within " +
                $"{maxAttempts} readiness attempts; the concurrency test cannot proceed against an " +
                "unavailable cache.");
        }
    }
}
