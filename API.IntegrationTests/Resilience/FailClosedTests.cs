using System;
using System.Diagnostics;                      // Stopwatch (MJ-05: elapsed-bound assertion)
using System.Net;                              // HttpStatusCode
using System.Net.Http;                         // HttpClient, HttpResponseMessage
using System.Text.Json;                        // JsonSerializer, JsonSerializerOptions
using System.Threading.Tasks;
using API.Errors;                              // ApiException (real deserialization target)
using API.IntegrationTests.Infrastructure;     // ContainerFixture, CustomWebApplicationFactory
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;        // WebApplicationFactoryClientOptions
using Xunit;

namespace API.IntegrationTests.Resilience
{
    /// <summary>
    /// CRITICAL-PATH resilience tests proving the application <b>fails closed</b> when a backing
    /// datastore becomes unavailable. With the real ASP.NET Core application running in-process (via
    /// <see cref="CustomWebApplicationFactory"/>), each test stops a <b>real</b> Testcontainers datastore
    /// mid-test — PostgreSQL in one test, Redis in the other — then issues a genuine HTTP request that
    /// touches that dependency. The pipeline must surface the failure as a <b>controlled, structured</b>
    /// <see cref="ApiException"/> <c>HTTP 500</c> with <c>Content-Type: application/json</c> and a
    /// camelCase body — i.e. never a hang, never a <c>200</c> serving stale/leaked data, and never an
    /// unstructured stack dump. This is exactly the behaviour <c>API/Middleware/ExceptionMiddleware.cs</c>
    /// produces for any unhandled downstream exception, so these tests exercise that global handler
    /// end-to-end (contributing to the <c>ExceptionMiddleware</c> ≥90% coverage goal).
    ///
    /// <para>
    /// <b>Why a per-<c>[Fact]</c> fixture owned as an instance field (and NOT <c>IClassFixture&lt;ContainerFixture&gt;</c>
    /// nor any shared collection).</b> Stopping a container is <b>destructive</b>: a single
    /// <see cref="ContainerFixture"/> shared across a class's tests — which is exactly what
    /// <c>IClassFixture&lt;ContainerFixture&gt;</c> (used by every sibling integration class) provides — would
    /// let one test's container-stop tear the datastore out from under its sibling <c>[Fact]</c>s. To
    /// guarantee isolation this class instead <b>owns</b> its own <see cref="ContainerFixture"/> instance and
    /// implements <see cref="IAsyncLifetime"/> directly. xUnit constructs a <b>fresh instance of the test
    /// class per <c>[Fact]</c> method</b>, so each test receives its <b>own</b> freshly-started, isolated
    /// PostgreSQL + Redis pair (via <see cref="InitializeAsync"/>) and disposes them afterwards (via
    /// <see cref="DisposeAsync"/>). Cross-class isolation is independently guaranteed because every sibling
    /// class has its own per-class <c>IClassFixture&lt;ContainerFixture&gt;</c> instance (there is no shared
    /// cross-class collection). Consequently stopping a container in one test can never affect another test —
    /// or another class — and no container-restart logic is ever required.
    /// </para>
    ///
    /// <para>
    /// <b>Trade-off (intentional).</b> Because the fixture is per-instance (per <c>[Fact]</c>), the containers
    /// are started <b>twice</b> (once per <c>[Fact]</c>). That is the accepted cost of guaranteeing isolation
    /// for destructive outage simulation: a per-instance fixture ensures that stopping a container in one
    /// <c>[Fact]</c> cannot disrupt the other <c>[Fact]</c> of this class (nor any sibling class).
    /// </para>
    ///
    /// <para>
    /// <b>Real infrastructure only (AAP §0.10.1).</b> The outage is simulated by stopping the <b>genuine</b>
    /// Testcontainers containers via the fixture's container handles — PostgreSQL, Redis and the HTTP
    /// transport are never mocked or stubbed, and the shared <c>docker-compose</c> instances are never
    /// used (Testcontainers assigns dynamic ports). No fixed <c>Thread.Sleep</c>/delay is used: a stopped
    /// container releases its mapped port, so the very next connection is refused promptly and the tests
    /// remain deterministic. Requires a running Docker daemon.
    /// </para>
    ///
    /// <para>
    /// <b>Development environment dependency.</b> <see cref="CustomWebApplicationFactory"/> runs the app in
    /// the <c>Development</c> environment, so <c>ExceptionMiddleware</c> takes its
    /// <c>_env.IsDevelopment()</c> branch and emits <c>new ApiException(500, ex.Message,
    /// ex.StackTrace.ToString())</c> — the response body therefore carries both <c>message</c> and
    /// <c>details</c> (the stack trace). The outage exceptions are genuinely thrown, so the stack trace is
    /// populated (no null-stack-trace concern). Assertions target the fields' presence, not their exact
    /// text (the underlying Npgsql/Redis wording is not contractually stable).
    /// </para>
    ///
    /// <para>
    /// Naming follows the repository convention <c>MethodName_StateUnderTest_ExpectedBehavior</c>, with an
    /// Arrange-Act-Assert structure and FluentAssertions.
    /// </para>
    /// </summary>
    public class FailClosedTests : IAsyncLifetime
    {
        /// <summary>
        /// Dedicated, non-shared container fixture owned by this test instance. Because xUnit creates a
        /// fresh <see cref="FailClosedTests"/> per <c>[Fact]</c>, each test gets its own isolated
        /// PostgreSQL + Redis pair — so stopping a container here can never disrupt any sibling test or
        /// class. This class intentionally owns its fixture as an instance field (per <c>[Fact]</c>) rather
        /// than using <c>IClassFixture&lt;ContainerFixture&gt;</c>, and it is not part of any shared collection.
        /// </summary>
        private readonly ContainerFixture _fixture = new ContainerFixture();

        /// <summary>
        /// Starts (and seeds) this instance's own PostgreSQL + Redis containers and the in-process host
        /// before the single <c>[Fact]</c> that owns this instance runs. Delegated to the fixture, which
        /// owns the container lifecycle and awaits readiness via a wait strategy (never a fixed sleep).
        /// </summary>
        public Task InitializeAsync() => _fixture.InitializeAsync();

        /// <summary>
        /// Disposes this instance's containers and host after its <c>[Fact]</c> completes. A container that
        /// was stopped during the test is disposed here as well, so no Docker resources leak between tests.
        /// </summary>
        public Task DisposeAsync() => _fixture.DisposeAsync();

        /// <summary>
        /// MJ-05 — explicit hard upper bound on any single fail-closed request, replacing
        /// <see cref="HttpClient"/>'s 100-second default. It is set comfortably above the observed worst-case
        /// outage latency (~15–16 s for a stopped-Redis connection abort) so the tests never flake, yet far
        /// below the 100 s default so a genuine <b>hang</b> regression surfaces promptly as a
        /// <see cref="TaskCanceledException"/> tied to this explicit bound instead of stalling the suite.
        /// </summary>
        private static readonly TimeSpan FailClosedTimeout = TimeSpan.FromSeconds(60);

        /// <summary>
        /// MJ-05 — promptness SLA asserted (via a <see cref="Stopwatch"/>) after each fail-closed request.
        /// Set strictly BELOW <see cref="FailClosedTimeout"/> so the elapsed-bound assertion is meaningful
        /// rather than tautological: a response that arrives between this SLA and the hard timeout FAILS the
        /// SLA (catching a slowdown regression toward a hang), while a true hang throws at the hard timeout.
        /// It retains generous headroom over the observed ~15–16 s worst case to stay non-flaky.
        /// </summary>
        private static readonly TimeSpan FailClosedPromptnessSla = TimeSpan.FromSeconds(45);

        /// <summary>
        /// Creates an <see cref="HttpClient"/> over the in-process application. <c>AllowAutoRedirect</c> is
        /// disabled so a <c>307</c> from <c>Startup</c>'s <c>UseHttpsRedirection</c> is never chased and the
        /// test observes the real status code the pipeline produces. MJ-05: the client's
        /// <see cref="HttpClient.Timeout"/> is pinned to the explicit <see cref="FailClosedTimeout"/> bound so
        /// a fail-closed request can never hang the suite on the framework's 100-second default.
        /// </summary>
        private HttpClient CreateClient()
        {
            var client = _fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            client.Timeout = FailClosedTimeout; // MJ-05: bounded, explicit timeout (see field docs).
            return client;
        }

        /// <summary>
        /// Shared assertion that a fail-closed response is a <b>controlled, structured</b>
        /// <see cref="ApiException"/> <c>500</c> rather than a hang or a success. Verifies the status code,
        /// the <c>application/json</c> content type, and — after deserialising the body into the real
        /// <see cref="ApiException"/> type (case-insensitive, matching the middleware's camelCase output) —
        /// that <c>StatusCode == 500</c>, <c>Message</c> is present, and <c>Details</c> (the Development
        /// stack trace) is present. It deliberately does NOT assert on exact message/stack-trace text.
        /// </summary>
        /// <param name="response">The HTTP response returned by the request that triggered the outage.</param>
        private static async Task AssertStructuredServerErrorAsync(HttpResponseMessage response)
        {
            // Controlled failure — NOT a hang, NOT a success. ExceptionMiddleware WRITES a 500 response
            // (it does not rethrow), so HttpClient returns normally; do NOT call EnsureSuccessStatusCode.
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            response.Content.Headers.ContentType.MediaType.Should().Be("application/json");

            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotBeNullOrWhiteSpace("the global exception handler always writes a JSON body");

            // Deserialize into the REAL API.Errors.ApiException type (the API project is referenced). The
            // middleware serialises with JsonNamingPolicy.CamelCase (statusCode/message/details); System.Text.Json
            // (net5) binds those onto ApiException's parameterized constructor case-insensitively.
            var apiException = JsonSerializer.Deserialize<ApiException>(
                body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            apiException.Should().NotBeNull("the fail-closed body must be a structured ApiException, not an unstructured error");
            apiException.StatusCode.Should().Be(500);
            apiException.Message.Should().NotBeNullOrWhiteSpace();

            // CustomWebApplicationFactory runs the app in Development, so ExceptionMiddleware populates
            // Details with the (genuinely thrown) exception's stack trace via its dev branch.
            apiException.Details.Should().NotBeNullOrWhiteSpace(
                "in the Development environment the middleware includes the stack trace in Details");
        }

        /// <summary>
        /// Fail-closed: with Redis still UP, stopping the <b>real</b> PostgreSQL container and then hitting
        /// the anonymous Postgres-backed <c>GET api/products</c> endpoint must yield a structured
        /// <see cref="ApiException"/> <c>500</c>. Flow: the <c>[Cached]</c> filter reads Redis (still up) →
        /// cache miss on a fresh empty Redis → the action queries the (now downed) PostgreSQL via
        /// <c>IGenericRepository&lt;Product&gt;</c> → EF/Npgsql throws → the exception propagates to
        /// <c>ExceptionMiddleware</c>, which returns the controlled 500.
        /// </summary>
        [Fact]
        public async Task GetProducts_WhenPostgresContainerStopped_ReturnsStructuredApiException500()
        {
            // Arrange — a client over the in-process app. Only PostgreSQL will be stopped; Redis stays UP
            // so the [Cached] filter yields a cache miss and lets the action reach the downed database.
            // MD-01: the client and the response are disposed via `using`.
            using var client = CreateClient();

            // Act — stop the REAL PostgreSQL container, then hit a Postgres-backed endpoint. No wait is
            // needed after StopAsync: the released port refuses the next connection immediately. MJ-05: the
            // elapsed time is measured so the fail-closed response can be asserted to arrive within bound.
            await _fixture.PostgresContainer.StopAsync();
            var stopwatch = Stopwatch.StartNew();
            using var response = await client.GetAsync("api/products");
            stopwatch.Stop();

            // Assert — fail closed: a controlled, structured ApiException 500 (never a hang, never a 200).
            await AssertStructuredServerErrorAsync(response);

            // MJ-05: the fail-closed 500 must arrive PROMPTLY, never hang. Receiving a response at all proves
            // the hard client timeout was not hit; assert the measured elapsed time stays within the tighter
            // promptness SLA to guard against a regression that degrades the fail-closed path toward a hang.
            stopwatch.Elapsed.Should().BeLessThan(FailClosedPromptnessSla,
                "a fail-closed response must be prompt and bounded, never a hang");
        }

        /// <summary>
        /// Fail-closed: stopping the <b>real</b> Redis container and then hitting the anonymous
        /// Redis-backed <c>GET api/basket?id=...</c> endpoint must yield a structured
        /// <see cref="ApiException"/> <c>500</c>. Flow: resolving <c>IBasketRepository</c> forces the lazy
        /// <c>IConnectionMultiplexer</c> singleton to connect to the (now downed) Redis on first use →
        /// <c>RedisConnectionException</c> → the exception propagates to <c>ExceptionMiddleware</c>, which
        /// returns the controlled 500. This endpoint touches only Redis (not Postgres), isolating the
        /// Redis-outage path.
        /// </summary>
        [Fact]
        public async Task GetBasket_WhenRedisContainerStopped_ReturnsStructuredApiException500()
        {
            // Arrange — a client over the in-process app; the multiplexer has not connected yet (lazy).
            // MD-01: the client and the response are disposed via `using`.
            using var client = CreateClient();

            // Act — stop the REAL Redis container, then hit a Redis-backed endpoint. The multiplexer's
            // first connection attempt (with AbortOnConnectFail) fails fast against the released port. MJ-05:
            // the elapsed time is measured so the fail-closed response can be asserted to arrive within bound.
            await _fixture.RedisContainer.StopAsync();
            var stopwatch = Stopwatch.StartNew();
            using var response = await client.GetAsync("api/basket?id=fail-closed-redis-test");
            stopwatch.Stop();

            // Assert — fail closed: a controlled, structured ApiException 500 (never a hang, never a 200).
            await AssertStructuredServerErrorAsync(response);

            // MJ-05: the fail-closed 500 must arrive PROMPTLY, never hang. Receiving a response at all proves
            // the hard client timeout was not hit; assert the measured elapsed time stays within the tighter
            // promptness SLA to guard against a regression that degrades the fail-closed path toward a hang.
            stopwatch.Elapsed.Should().BeLessThan(FailClosedPromptnessSla,
                "a fail-closed response must be prompt and bounded, never a hang");
        }
    }
}
