using System;
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
    /// <b>Why a DEDICATED, non-shared fixture (and NOT <c>[Collection("Integration")]</c>).</b> Stopping
    /// a container is <b>destructive</b>: every other integration class that shared a single
    /// <c>ContainerFixture</c> through the <c>"Integration"</c> collection would suddenly lose its
    /// PostgreSQL/Redis instance and fail. To guarantee isolation this class instead <b>owns</b> its own
    /// <see cref="ContainerFixture"/> instance and implements <see cref="IAsyncLifetime"/> directly.
    /// xUnit constructs a <b>fresh instance of the test class per <c>[Fact]</c> method</b>, so each test
    /// receives its <b>own</b> freshly-started, isolated PostgreSQL + Redis pair (via
    /// <see cref="InitializeAsync"/>) and disposes them afterwards (via <see cref="DisposeAsync"/>).
    /// Consequently stopping a container in one test can never affect another test — or another class —
    /// and no container-restart logic is ever required.
    /// </para>
    ///
    /// <para>
    /// <b>Trade-off (intentional).</b> Because the fixture is per-instance, the containers are started
    /// <b>twice</b> (once per <c>[Fact]</c>). That is the accepted cost of guaranteeing isolation for
    /// destructive outage simulation, per the folder specification: <c>FailClosedTests</c> requires its
    /// OWN dedicated fixture instance so that stopping a container does not disrupt sibling classes.
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
        /// class. This class is intentionally NOT part of the shared <c>"Integration"</c> collection.
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
        /// Creates an <see cref="HttpClient"/> over the in-process application. <c>AllowAutoRedirect</c> is
        /// disabled so a <c>307</c> from <c>Startup</c>'s <c>UseHttpsRedirection</c> is never chased and the
        /// test observes the real status code the pipeline produces.
        /// </summary>
        private HttpClient CreateClient() =>
            _fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

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
            var client = CreateClient();

            // Act — stop the REAL PostgreSQL container, then hit a Postgres-backed endpoint. No wait is
            // needed after StopAsync: the released port refuses the next connection immediately.
            await _fixture.PostgresContainer.StopAsync();
            var response = await client.GetAsync("api/products");

            // Assert — fail closed: a controlled, structured ApiException 500 (never a hang, never a 200).
            await AssertStructuredServerErrorAsync(response);
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
            var client = CreateClient();

            // Act — stop the REAL Redis container, then hit a Redis-backed endpoint. The multiplexer's
            // first connection attempt (with AbortOnConnectFail) fails fast against the released port.
            await _fixture.RedisContainer.StopAsync();
            var response = await client.GetAsync("api/basket?id=fail-closed-redis-test");

            // Assert — fail closed: a controlled, structured ApiException 500 (never a hang, never a 200).
            await AssertStructuredServerErrorAsync(response);
        }
    }
}
