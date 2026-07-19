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
using Microsoft.Extensions.DependencyInjection;  // CreateScope, GetRequiredService (MJ-04 cleanup)
using StackExchange.Redis;                        // IConnectionMultiplexer — MJ-04 Redis key cleanup
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
    /// supplied by this class's dedicated <see cref="ContainerFixture"/> and the in-process HTTP transport
    /// are both genuine, and the shared <c>docker-compose</c> stack is never reused (Testcontainers assigns dynamic
    /// ports). Concurrency is driven exclusively via a single <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/>
    /// over all requests — there is no <c>Thread.Sleep</c>; the fixture guarantees container readiness via
    /// its wait strategies before any test runs. All test code is isolated in this file and no production
    /// code is modified.
    /// </para>
    ///
    /// <para>
    /// <b>Per-class isolated containers (CR-01).</b> This class consumes <see cref="ContainerFixture"/> as
    /// an <c>IClassFixture&lt;ContainerFixture&gt;</c>, so it owns a dedicated already-started PostgreSQL +
    /// Redis pair and its own wired <see cref="CustomWebApplicationFactory"/>. It uses the anonymous
    /// <see cref="ContainerFixture.CreateClient"/> (no bearer token) because the basket endpoints are not
    /// gated by <c>[Authorize]</c>. Each test uses <b>unique GUID-based basket ids</b> so its keys never
    /// collide with sibling tests within this class's Redis instance.
    /// </para>
    ///
    /// <para>
    /// Naming follows the repository convention <c>MethodName_StateUnderTest_ExpectedBehavior</c>, with an
    /// Arrange-Act-Assert structure and FluentAssertions.
    /// </para>
    /// </summary>
    public class BasketConcurrencyTests : IClassFixture<ContainerFixture>
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
        /// This class's dedicated fixture (started once for THIS class) providing the real Testcontainers
        /// PostgreSQL + Redis and the wired in-process host. Injected by xUnit through the class fixture.
        /// </summary>
        private readonly ContainerFixture _fixture;

        /// <summary>
        /// Receives this class's dedicated <see cref="ContainerFixture"/> from xUnit's class-fixture machinery.
        /// </summary>
        /// <param name="fixture">This class's isolated container fixture.</param>
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

                // MJ-04 deterministic cleanup: delete every basket key this test wrote to Redis so no key
                // lingers in this class's cache between its tests.
                await CleanupBasketsAsync(ids);
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

                // Assert (final Redis state via GET): because every concurrent write sent an IDENTICAL
                // payload, the value persisted in Redis is deterministic despite the write race — so the
                // stored basket must be a coherent 2-item document matching the canonical payload exactly.
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
            finally
            {
                foreach (var response in responses)
                {
                    response?.Dispose();
                }

                // MJ-04 deterministic cleanup: delete the basket key this test wrote to Redis.
                await CleanupBasketsAsync(new[] { basketId });
            }
        }

        /// <summary>
        /// Fires <see cref="ConcurrentRequestCount"/> concurrent <c>POST api/basket</c> requests to the
        /// <b>same</b> basket id using <b>conflicting</b> payloads: each request writes a single-item basket
        /// whose <c>Quantity</c> is distinct (the writer index, 1..N), so every writer genuinely competes to
        /// persist a <i>different</i> document to the same Redis key. Asserts that despite the write race
        /// (a) no request degrades into a <c>500</c> — all return <c>200 OK</c>; (b) each response body is a
        /// well-formed single-item <see cref="CustomerBasket"/> that is a coherent snapshot of some writer's
        /// full document — its quantity is always one of the submitted values, never a torn, partial, or
        /// merged multi-item document; and (c) the final document read back via <c>GET</c> is a single
        /// coherent basket whose item carries exactly one of the submitted quantities — a clean
        /// last-write-wins outcome, never a corrupted, partial, summed, or field-merged document.
        ///
        /// <para>
        /// <b>Why conflicting (not identical) payloads.</b> An identical-payload burst can only ever prove
        /// the stored value is byte-stable; it cannot detect a torn or merged write because every writer's
        /// bytes are the same. Making each concurrent write observably different (a distinct
        /// <c>Quantity</c> per writer) means any interleaving that produced a merged/partial document —
        /// e.g. two items, a summed quantity, or a quantity outside the submitted set — would fail these
        /// coherence assertions. This is the conflicting-payload same-key concurrency scenario required by
        /// the AAP §0.4.2 blueprint ("20 concurrent <c>POST api/basket</c> ... non-corrupted JSON").
        /// </para>
        ///
        /// <para>
        /// <b>Determinism.</b> Which writer wins is non-deterministic under a race (last-write-wins), so
        /// this test never asserts <i>which</i> quantity survives — only that every observed document (each
        /// response and the final <c>GET</c>) is exactly one coherent single-item basket drawn from the
        /// submitted set. Note that <c>UpdateBasketAsync</c> SETs the caller's payload and then RE-READS the
        /// shared key (<c>return await GetBasketAsync(basket.Id)</c>), so a response reflects a coherent
        /// snapshot of whichever writer's full document was current at the read — not necessarily the
        /// caller's own write. That is precisely why conflicting payloads (rather than identical ones) are
        /// required: a torn, partial, or field-merged write would surface as a multi-item document or a
        /// quantity outside the submitted set — which these assertions would catch — whereas an
        /// identical-payload burst cannot detect it. A full-document overwrite per request (not a
        /// field-level merge) is the storage contract; this test verifies the application preserves it
        /// end-to-end under genuine concurrency against real Redis.
        /// </para>
        /// </summary>
        [Fact]
        public async Task UpdateBasket_TwentyConcurrentRequestsSameIdConflictingPayloads_NoCorruptionAndCoherentLastWriteWins()
        {
            // Arrange — one shared basket id, and ConcurrentRequestCount DISTINCT single-item payloads whose
            // Quantity encodes the writer index (1..N). Every payload is individually valid (Quantity >= 1,
            // Price >= 0.1) but genuinely conflicts with the others, so the final stored document must equal
            // exactly one writer's payload rather than any merged/torn combination of them.
            var basketId = $"basket-concurrency-conflict-{Guid.NewGuid():N}";

            // The set of quantities that were submitted; the surviving persisted quantity MUST be one of these.
            var submittedQuantities = Enumerable.Range(1, ConcurrentRequestCount).ToList();

            var payloads = submittedQuantities
                .Select(quantity =>
                {
                    // Reuse the shared single-item builder, then vary ONLY the quantity so each payload is a
                    // distinct-yet-valid conflicting document for the SAME key.
                    var dto = BuildBasketDto(basketId, itemCount: 1);
                    dto.Items[0].Quantity = quantity;
                    return dto;
                })
                .ToList();

            using var client = _fixture.CreateClient();

            // Complete the lazy Redis connect BEFORE the burst so this measures concurrent write-safety, not
            // one-time cold-connect behavior (bounded readiness wait strategy, never a fixed Thread.Sleep).
            await WarmUpRedisConnectionAsync(client);

            // Act — fire all conflicting posts concurrently through a SINGLE Task.WhenAll. Task.WhenAll
            // preserves input order, so responses[i] is the response to payloads[i].
            var responses = await Task.WhenAll(
                payloads.Select(dto => client.PostAsJsonAsync("api/basket", dto)));

            try
            {
                // Assert (per-response): none failed, and each echoes its OWN single-item payload intact —
                // proving the pipeline never returned a torn/merged body under the conflicting race.
                responses.Should().NotContain(
                    r => r.StatusCode == HttpStatusCode.InternalServerError,
                    "conflicting racing writes to the same key must not degrade into an unhandled exception / 500");
                responses.Should().OnlyContain(
                    r => r.StatusCode == HttpStatusCode.OK,
                    "every conflicting concurrent write to the same basket must complete successfully");

                foreach (var response in responses)
                {
                    var basket = await response.Content.ReadFromJsonAsync<CustomerBasket>();
                    AssertWellFormed(basket, basketId);

                    // Because UpdateBasketAsync SETs the caller's payload and then RE-READS the shared key
                    // (return await GetBasketAsync(basket.Id)), a response under a same-key race reflects a
                    // COHERENT SNAPSHOT of whichever writer's full document was current in Redis at the read
                    // — not necessarily this caller's own write. The corruption-freedom invariant is
                    // therefore that every response body is a single-item basket whose quantity is one of
                    // the values submitted by SOME writer: never a torn/partial document, never a merged
                    // multi-item document, never a quantity outside the submitted set.
                    var snapshotItem = basket.Items.Should().ContainSingle(
                        "each response is a coherent single-item snapshot of the shared key, " +
                        "never a torn, partial, or merged multi-item document").Which;
                    submittedQuantities.Should().Contain(
                        snapshotItem.Quantity,
                        "each returned quantity must be one submitted by some concurrent writer, " +
                        "confirming the SET/GET round-trip never yielded a corrupted or out-of-set value");
                }

                // Assert (final Redis state via GET): the race resolves to ONE coherent last-write-wins
                // document — a single-item basket whose quantity is exactly one of the submitted values,
                // never a merged/summed/partial result.
                using var getResponse = await client.GetAsync($"api/basket?id={basketId}");
                getResponse.StatusCode.Should().Be(HttpStatusCode.OK,
                    "the basket persisted in Redis must be retrievable through the real pipeline");

                var finalBasket = await getResponse.Content.ReadFromJsonAsync<CustomerBasket>();
                AssertWellFormed(finalBasket, basketId);

                var survivingItem = finalBasket.Items.Should().ContainSingle(
                    "the persisted basket must be a single coherent document (last-write-wins), " +
                    "never a torn, merged, or partially-written combination of conflicting writers").Which;
                submittedQuantities.Should().Contain(
                    survivingItem.Quantity,
                    "the surviving quantity must be exactly one of the submitted values, " +
                    "proving a clean last-write-wins outcome rather than a corrupted/merged document");
            }
            finally
            {
                foreach (var response in responses)
                {
                    response?.Dispose();
                }

                // MJ-04 deterministic cleanup: delete the basket key this test wrote to Redis.
                await CleanupBasketsAsync(new[] { basketId });
            }
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
        /// MJ-04 deterministic cleanup: deletes each given basket key from the real Redis instance (a basket
        /// id IS its Redis key) through the application's own <see cref="IConnectionMultiplexer"/>, so no key
        /// this test wrote lingers in this class's cache between its tests.
        /// </summary>
        /// <param name="basketIds">The basket ids / Redis keys to delete.</param>
        private async Task CleanupBasketsAsync(IEnumerable<string> basketIds)
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

            foreach (var basketId in basketIds)
            {
                await database.KeyDeleteAsync(basketId);
            }
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
