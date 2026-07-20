using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using API.IntegrationTests.Infrastructure;
using Core.Interfaces;                         // IPaymentService (resolve the singleton stub — MJ-09)
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection; // GetRequiredService (MJ-09)
using Stripe;
using Xunit;

namespace API.IntegrationTests.Payments
{
    /// <summary>
    /// Integration tests for the Stripe webhook endpoint (POST api/payments/webhook), exercised through
    /// the REAL ASP.NET Core pipeline via the shared CustomWebApplicationFactory / WebApplicationFactory&lt;Startup&gt;.
    ///
    /// Binding constraints (AAP 0.10.1): NO live Stripe network calls anywhere. The "Stripe-Signature"
    /// header is produced entirely OFFLINE by replicating Stripe's HMAC-SHA256 signing algorithm
    /// (Stripe.net 39.66.0 exposes no public test-signing helper), using the SAME webhook secret the
    /// application is configured with by CustomWebApplicationFactory ("StripeSettings:WhSecret" =
    /// "whsec_test_stub"). The surrounding infrastructure (PostgreSQL + Redis via Testcontainers) is real,
    /// though this particular endpoint does not read/write it.
    ///
    /// IMPORTANT — payment-service stub behavior (read before "fixing" any test in this file):
    /// The integration harness replaces the production IPaymentService with StripePaymentServiceStub
    /// (API.IntegrationTests/Infrastructure/StripePaymentServiceStub.cs), registered as a SINGLETON so its
    /// delegations are observable. That stub deliberately returns a NON-NULL Order from both
    /// UpdateOrderPaymentSucceeded and UpdateOrderPaymentFailed (its own XML docs explain that a non-null
    /// result is REQUIRED so PaymentsController.StripeWebhook can safely dereference order.Id when logging the
    /// updated order). Consequently a validly-signed "payment_intent.succeeded" /
    /// "payment_intent.payment_failed" webhook completes successfully (HTTP 200 / EmptyResult). Production
    /// code must NOT be changed (AAP 0.10.1).
    ///
    /// MJ-09 — because the stub is a singleton that records each webhook delegation (method name +
    /// payment-intent id), the handled-event test below resolves that same instance from the factory's
    /// service provider and asserts EXACTLY HTTP 200 AND that the controller delegated to the correct
    /// transition method with the event's payment-intent id (previously it accepted "200 or a downstream
    /// 500" and could not observe the delegation at all).
    ///
    /// MJ-10 — replay/timestamp coverage: an "old-but-correct" signature (a cryptographically valid HMAC for
    /// a timestamp ~1 hour in the past) must be rejected by Stripe's 300 s replay tolerance, and a duplicate
    /// delivery of the identical signed event is processed twice (the controller performs no server-side
    /// idempotency — a deferred PRODUCTION concern, out of scope per AAP §0.8.2/§0.10.1; see that test's docs).
    /// </summary>
    public class StripeWebhookTests : IClassFixture<ContainerFixture>
    {
        // MUST equal the value CustomWebApplicationFactory injects for "StripeSettings:WhSecret".
        // PaymentsController reads config.GetSection("StripeSettings:WhSecret").Value and passes it to
        // EventUtility.ConstructEvent, so offline signing here must use the identical secret to produce a
        // signature the application accepts.
        private const string WebhookSecret = "whsec_test_stub";
        private const string WebhookPath = "api/payments/webhook";

        private readonly ContainerFixture _fixture;

        public StripeWebhookTests(ContainerFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task StripeWebhook_WithValidSignatureAndUnhandledEventType_ReturnsSuccess()
        {
            // Arrange
            // We deliberately use an event TYPE the controller's switch does NOT handle
            // ("payment_intent.created"). Rationale (AAP 0.10.1 forbids modifying production code):
            // PaymentsController.StripeWebhook dereferences `order.Id` WITHOUT a null-check in the
            // "payment_intent.succeeded" / "payment_intent.payment_failed" branches. Using an event type
            // that no branch handles guarantees the switch falls straight through to
            // `return new EmptyResult();` (HTTP 200) WITHOUT touching the payment-service call or the
            // order.Id dereference at all — so the ONLY thing that can influence the outcome is whether
            // EventUtility.ConstructEvent accepted the OFFLINE signature. A 200 here therefore proves the
            // hand-rolled signature was verified. (The handled-type branches are covered by the [Theory]
            // below, which is written to be robust to the payment-service stub's return value.)
            var json = BuildEventJson("payment_intent.created");
            var signature = GenerateSignatureHeader(json, WebhookSecret);
            // MD-01: dispose the client and the response via `using`.
            using var client = _fixture.Factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            // Act
            using var response = await PostWebhookAsync(client, json, signature);

            // Assert
            response.IsSuccessStatusCode.Should().BeTrue(
                "a valid offline-computed signature must be accepted by EventUtility.ConstructEvent, and an " +
                "unhandled event type falls through to EmptyResult");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Theory]
        [InlineData("payment_intent.succeeded", nameof(StripePaymentServiceStub.UpdateOrderPaymentSucceeded))]
        [InlineData("payment_intent.payment_failed", nameof(StripePaymentServiceStub.UpdateOrderPaymentFailed))]
        public async Task StripeWebhook_WithValidSignatureForHandledEventType_Returns200AndDelegatesToCorrectStubMethod(
            string eventType, string expectedStubMethod)
        {
            // Arrange
            // A VALID offline signature for a HANDLED event type ("payment_intent.succeeded" /
            // "payment_intent.payment_failed"). EventUtility.ConstructEvent verifies the signature FIRST; the
            // controller then enters the corresponding branch and delegates to the offline
            // StripePaymentServiceStub, which returns a NON-NULL Order so the controller logs order.Id safely
            // and returns EmptyResult (HTTP 200). Production code is NOT modified (AAP §0.10.1).
            //
            // MJ-09: rather than accepting "200 OR a non-signature 500" (which never observed the actual
            // delegation), this test now asserts the CONCRETE contract of a valid handled event:
            //   (1) the endpoint returns EXACTLY HTTP 200, and
            //   (2) the controller delegated to the CORRECT IPaymentService transition (succeeded vs failed)
            //       with the event's payment-intent id ("pi_test_123", from BuildEventJson's data.object.id).
            // Delegation is observable because the stub is a singleton that records each webhook call; the
            // test resolves that same instance and inspects its recorded Calls. The log is reset first so the
            // assertion sees only THIS test's delegation (the suite runs sequentially).
            const string expectedPaymentIntentId = "pi_test_123";
            var stub = GetPaymentServiceStub();
            stub.ResetRecording();

            var json = BuildEventJson(eventType);
            var signature = GenerateSignatureHeader(json, WebhookSecret);
            using var client = _fixture.Factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            // Act
            using var response = await PostWebhookAsync(client, json, signature);

            // Assert — the valid signature was accepted, the handled branch ran, and the stub's non-null
            // Order let the controller return EmptyResult: EXACTLY 200.
            response.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "a validly-signed handled event must verify, delegate, and return EmptyResult (200)");

            // Assert — delegation is OBSERVABLE and correct: exactly one call, to the expected transition
            // method, carrying the event's payment-intent id.
            var calls = stub.Calls;
            calls.Should().ContainSingle(
                "a single handled webhook event must delegate to the payment service exactly once");
            calls[0].Method.Should().Be(
                expectedStubMethod,
                "'payment_intent.succeeded' must route to UpdateOrderPaymentSucceeded and " +
                "'payment_intent.payment_failed' to UpdateOrderPaymentFailed");
            calls[0].PaymentIntentId.Should().Be(
                expectedPaymentIntentId,
                "the controller must forward the event's PaymentIntent id (data.object.id) to the service");
        }

        [Fact]
        public async Task StripeWebhook_WithTamperedSignature_ReturnsInternalServerError()
        {
            // Arrange
            // Well-formed header (t=...,v1=<64 hex chars>) but the HMAC is computed with the WRONG secret,
            // so the server's recomputed signature will not match => EventUtility.ConstructEvent throws a
            // StripeException BEFORE any event processing (ValidateSignature runs before ParseEvent).
            var json = BuildEventJson("payment_intent.succeeded");
            var tamperedSignature = GenerateSignatureHeader(json, "whsec_an_entirely_different_secret");
            // MD-01: dispose the client and the response via `using`.
            using var client = _fixture.Factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            // Act
            using var response = await PostWebhookAsync(client, json, tamperedSignature);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            var error = await ReadErrorAsync(response);
            // Development env => ExceptionMiddleware surfaces the StripeException message, which references
            // the signature/header, confirming the request was rejected at the signature-verification step.
            error.Message.Should().ContainEquivalentOf("signature");
        }

        [Fact]
        public async Task StripeWebhook_WithMissingSignatureHeader_ReturnsInternalServerError()
        {
            // Arrange -- valid body but NO Stripe-Signature header at all. EventUtility.ConstructEvent
            // cannot validate a missing signature => throws => ExceptionMiddleware => HTTP 500.
            var json = BuildEventJson("payment_intent.succeeded");
            // MD-01: dispose the client and the response via `using`.
            using var client = _fixture.Factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            // Act -- signatureHeader == null => header is not added.
            using var response = await PostWebhookAsync(client, json, signatureHeader: null);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }

        [Fact]
        public async Task StripeWebhook_WithOldButCorrectlySignedEvent_IsRejectedByReplayTolerance()
        {
            // Arrange (MJ-10 — stale-timestamp / replay tolerance)
            // An "old-but-correct" signature: the HMAC is computed CORRECTLY, but for a timestamp ~1 hour in
            // the PAST. Stripe's EventUtility.ConstructEvent enforces a default replay-protection tolerance of
            // 300 s on |now - t|. Signing WITH the stale timestamp (so the cryptographic check itself passes)
            // isolates the rejection to the timestamp tolerance rather than a signature mismatch — proving the
            // endpoint genuinely enforces replay protection. Rejection surfaces as a StripeException =>
            // ExceptionMiddleware => HTTP 500 whose message references the "tolerance" (NOT "signature").
            var stub = GetPaymentServiceStub();
            stub.ResetRecording();

            var json = BuildEventJson("payment_intent.succeeded");
            var staleTimestamp = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
            var staleSignature = GenerateSignatureHeaderForTimestamp(json, WebhookSecret, staleTimestamp);
            using var client = _fixture.Factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            // Act
            using var response = await PostWebhookAsync(client, json, staleSignature);

            // Assert — replay protection rejects the stale-but-correctly-signed event with a controlled 500.
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            var error = await ReadErrorAsync(response);
            error.Message.Should().ContainEquivalentOf(
                "tolerance",
                "an old-but-correct signature must be rejected because its timestamp is outside Stripe's " +
                "300 s tolerance — a replay-protection rejection, not a signature mismatch");

            // And because verification failed BEFORE any handled branch ran, no delegation occurred. This
            // proves the rejection happened at verification, upstream of the payment-service call.
            stub.Calls.Should().BeEmpty(
                "a stale event is rejected at signature verification, before any delegation to IPaymentService");
        }

        [Fact]
        public async Task StripeWebhook_WhenSameSignedEventDeliveredTwice_BothAcceptedAndDelegatedTwice()
        {
            // Arrange (MJ-10 — duplicate-event / idempotency)
            // A SINGLE validly-signed event, delivered twice byte-for-byte (a genuine duplicate/replay within
            // Stripe's timestamp tolerance): the signature is computed ONCE and reused so both deliveries are
            // identical.
            //
            // DOCUMENTED behavior (NOT a defect asserted as correct): PaymentsController.StripeWebhook performs
            // NO server-side idempotency/deduplication — it processes every event whose signature verifies.
            // Adding event-id deduplication is a PRODUCTION change, which is OUT OF SCOPE for this testing
            // engagement: the AAP freezes production code except the single annotated Stripe seam in
            // PaymentService (§0.8.2) and forbids "more testable" production edits (§0.10.1). This test
            // therefore pins the ACTUAL current contract — both duplicate deliveries are accepted (200) and
            // each delegates to the payment service — so any silent change would be flagged. Consumer-side
            // idempotency on event.id (which Stripe expects) is recorded as deferred production work, not
            // implemented here.
            var stub = GetPaymentServiceStub();
            stub.ResetRecording();

            const string expectedPaymentIntentId = "pi_test_123";
            var json = BuildEventJson("payment_intent.succeeded");
            var signature = GenerateSignatureHeader(json, WebhookSecret); // computed ONCE; reused for both
            using var client = _fixture.Factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            // Act — deliver the identical signed event twice.
            using var first = await PostWebhookAsync(client, json, signature);
            using var second = await PostWebhookAsync(client, json, signature);

            // Assert — both deliveries verify and are accepted (no server-side dedup today).
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            second.StatusCode.Should().Be(HttpStatusCode.OK);

            // Delegation is observable: with no idempotency the handled branch ran on BOTH deliveries, so the
            // payment service was invoked twice with the same payment-intent id.
            var calls = stub.Calls;
            calls.Should().HaveCount(
                2, "the current controller has no idempotency, so a duplicate event is processed twice");
            calls.Should().OnlyContain(
                c => c.Method == nameof(StripePaymentServiceStub.UpdateOrderPaymentSucceeded)
                     && c.PaymentIntentId == expectedPaymentIntentId,
                "both deliveries route 'payment_intent.succeeded' to UpdateOrderPaymentSucceeded with pi_test_123");
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        /// <summary>
        /// MJ-09 — resolves the singleton <see cref="StripePaymentServiceStub"/> the application is running
        /// with. Because <c>CustomWebApplicationFactory</c> registers the stub as a singleton, the instance
        /// resolved here from the factory's root service provider is the SAME instance the controller uses
        /// within a request scope, so its recorded <see cref="StripePaymentServiceStub.Calls"/> reflect the
        /// controller's actual delegations.
        /// </summary>
        private StripePaymentServiceStub GetPaymentServiceStub() =>
            (StripePaymentServiceStub)_fixture.Factory.Services.GetRequiredService<IPaymentService>();

        private static async Task<HttpResponseMessage> PostWebhookAsync(
            HttpClient client, string json, string signatureHeader)
        {
            // StringContent(..., Encoding.UTF8, ...) writes the UTF-8 bytes of `json` with NO BOM, and the
            // controller reads them back verbatim via StreamReader, so the bytes signed here match the bytes
            // the server HMACs. `json` is single-line (no CR/LF) to avoid any \r\n vs \n mismatch.
            // MD-01: the HttpRequestMessage owns its StringContent, so disposing the request (via `using`)
            // disposes the content too. The default HttpCompletionOption buffers the full response body before
            // SendAsync returns, so disposing the request after the await is safe and never truncates it.
            using var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            if (signatureHeader != null)
            {
                request.Headers.Add("Stripe-Signature", signatureHeader);
            }

            return await client.SendAsync(request);
        }

        private static string BuildEventJson(string type)
        {
            // Single-line JSON shaped like a FULL Stripe Event envelope. Two hard requirements — both
            // verified empirically against the installed Stripe.net 39.66.0 (see notes below):
            //
            //   1. "api_version" MUST be present AND equal StripeConfiguration.ApiVersion.
            //      EventUtility.ConstructEvent validates the signature FIRST and then parses the event; a
            //      missing, null, or mismatched api_version makes parsing throw. Referencing
            //      StripeConfiguration.ApiVersion (resolved live) guarantees the match regardless of the
            //      exact SDK build.
            //
            //   2. The standard envelope fields "livemode", "pending_webhooks" and "request" MUST be
            //      present. Stripe.net 39.66.0's internal EventConverter.ReadJson dereferences these while
            //      materializing the Event and throws a NullReferenceException if any are absent. An
            //      over-minimal body ({id, object, api_version, created, type, data}) therefore FAILS to
            //      parse and surfaces as an HTTP 500 that is NOT a signature error. "previous_attributes":
            //      null under "data" mirrors a real event and keeps the converter happy.
            //
            // The body is single-line (no CR/LF) so the client and server byte streams are identical and
            // the server's recomputed HMAC matches the offline signature.
            var apiVersion = StripeConfiguration.ApiVersion;
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            return
                "{" +
                "\"id\":\"evt_test_webhook\"," +
                "\"object\":\"event\"," +
                $"\"api_version\":\"{apiVersion}\"," +
                $"\"created\":{created}," +
                "\"livemode\":false," +
                "\"pending_webhooks\":0," +
                "\"request\":{\"id\":null,\"idempotency_key\":null}," +
                $"\"type\":\"{type}\"," +
                "\"data\":{\"object\":{\"id\":\"pi_test_123\",\"object\":\"payment_intent\"," +
                "\"status\":\"succeeded\"},\"previous_attributes\":null}" +
                "}";
        }

        private static string GenerateSignatureHeader(string payload, string secret) =>
            // Current-timestamp signature (the common case): sign for "now" so the event is within Stripe's
            // replay tolerance. Delegates to the timestamp-parametrized overload used by the MJ-10 stale test.
            GenerateSignatureHeaderForTimestamp(payload, secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        private static string GenerateSignatureHeaderForTimestamp(string payload, string secret, long timestamp)
        {
            // Replicates Stripe's webhook signing algorithm (Stripe.net has no public test-signing helper):
            // signed payload = "{timestamp}.{payload}", HMAC-SHA256 keyed by the webhook secret, emitted as
            // lowercase hex; header = "t={timestamp},v1={signature}". 100% offline -- no network. The
            // timestamp is a parameter so a test can produce an "old-but-correct" signature (MJ-10) whose HMAC
            // is cryptographically valid yet whose timestamp is outside Stripe's replay tolerance.
            var signedPayload = $"{timestamp}.{payload}";

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));
            var signature = BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();

            return $"t={timestamp},v1={signature}";
        }

        private static async Task<ErrorResponse> ReadErrorAsync(HttpResponseMessage response)
        {
            var body = await response.Content.ReadAsStringAsync();

            // ExceptionMiddleware serializes ApiException with camelCase property names; case-insensitive
            // matching handles statusCode/message/details.
            return JsonSerializer.Deserialize<ErrorResponse>(
                       body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new ErrorResponse();
        }

        private sealed class ErrorResponse
        {
            public int StatusCode { get; set; }
            public string Message { get; set; }
            public string Details { get; set; }
        }
    }
}
