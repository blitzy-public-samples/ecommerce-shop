using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using API.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
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
    /// (API.IntegrationTests/Infrastructure/StripePaymentServiceStub.cs). That stub deliberately returns a
    /// NON-NULL Order from both UpdateOrderPaymentSucceeded and UpdateOrderPaymentFailed (its own XML docs
    /// explain that a non-null result is REQUIRED so PaymentsController.StripeWebhook can safely dereference
    /// order.Id when logging the updated order). Consequently a validly-signed "payment_intent.succeeded" /
    /// "payment_intent.payment_failed" webhook completes successfully (HTTP 200 / EmptyResult) rather than
    /// throwing a NullReferenceException. The tests below are written against that real, unmodified behavior
    /// (production code must NOT be changed — AAP 0.10.1). Where the outcome could differ depending on
    /// whether the stub returns null vs non-null, the assertions are made robust to BOTH so the essential
    /// invariant under test — that a valid offline signature is ACCEPTED — is proven either way.
    /// </summary>
    [Collection("Integration")]
    public class StripeWebhookTests
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
            var client = _fixture.Factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            // Act
            var response = await PostWebhookAsync(client, json, signature);

            // Assert
            response.IsSuccessStatusCode.Should().BeTrue(
                "a valid offline-computed signature must be accepted by EventUtility.ConstructEvent, and an " +
                "unhandled event type falls through to EmptyResult");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Theory]
        [InlineData("payment_intent.succeeded")]
        [InlineData("payment_intent.payment_failed")]
        public async Task StripeWebhook_WithValidSignatureForHandledEventType_PassesSignatureVerification(
            string eventType)
        {
            // Arrange
            // A VALID offline signature for a HANDLED event type ("payment_intent.succeeded" /
            // "payment_intent.payment_failed"). EventUtility.ConstructEvent verifies the signature FIRST;
            // the controller then enters the corresponding branch and calls the offline
            // StripePaymentServiceStub.
            //
            // What happens next depends on the stub's Order return value, and this test is written to be
            // correct for the REAL, UNMODIFIED code (production code must NOT change — AAP 0.10.1):
            //   * The stub in this repository returns a NON-NULL Order, so the controller logs order.Id
            //     (Id == 0, the int default) safely and falls through to `return new EmptyResult();`
            //     => HTTP 200. This is the expected, observed outcome here.
            //   * Were the stub instead to return null (the historical "null-order landmine"), the
            //     order.Id dereference would raise a NullReferenceException => ExceptionMiddleware => 500.
            //
            // The single invariant this test asserts — the invariant that actually matters for a webhook
            // signature-verification test — is that the VALID offline signature was ACCEPTED. That is true
            // in BOTH cases above: a signature *rejection* would have thrown a StripeException inside
            // ConstructEvent (BEFORE any branch ran) and produced a 500 whose message references the
            // signature/header. So we accept either a success status OR a non-signature 500, and we
            // explicitly reject a signature-related 500. Do NOT "simplify" this to assert 500 only — that
            // would silently break the moment the stub returns non-null (as it does today).
            var json = BuildEventJson(eventType);
            var signature = GenerateSignatureHeader(json, WebhookSecret);
            var client = _fixture.Factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            // Act
            var response = await PostWebhookAsync(client, json, signature);

            // Assert
            // The signature was accepted iff the request did NOT fail at the signature-verification step.
            // Success (2xx) trivially satisfies that. A 500 satisfies it only if it is NOT a signature
            // error (i.e., it is a downstream failure such as the documented null-order NRE).
            if (response.IsSuccessStatusCode)
            {
                response.StatusCode.Should().Be(
                    HttpStatusCode.OK,
                    "the real StripePaymentServiceStub returns a non-null Order, so the handled branch logs " +
                    "order.Id safely and the controller returns EmptyResult (200) after the offline signature " +
                    "was verified");
            }
            else
            {
                response.StatusCode.Should().Be(
                    HttpStatusCode.InternalServerError,
                    "the only non-success outcome for a valid signature is a downstream failure surfaced by " +
                    "ExceptionMiddleware as a 500");
                var error = await ReadErrorAsync(response);
                // The factory forces the Development environment, so ExceptionMiddleware surfaces the real
                // exception message. A signature failure would reference the Stripe-Signature header; a
                // downstream (e.g. null-order) failure does not -- either way this proves the offline
                // signature verified successfully.
                error.Message.Should().NotBeNull();
                error.Message.Should().NotContainEquivalentOf(
                    "signature",
                    "a valid offline signature must have been accepted; any 500 here is a DOWNSTREAM failure, " +
                    "not a signature-verification failure");
            }
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
            var client = _fixture.Factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            // Act
            var response = await PostWebhookAsync(client, json, tamperedSignature);

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
            var client = _fixture.Factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            // Act -- signatureHeader == null => header is not added.
            var response = await PostWebhookAsync(client, json, signatureHeader: null);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private static async Task<HttpResponseMessage> PostWebhookAsync(
            HttpClient client, string json, string signatureHeader)
        {
            // StringContent(..., Encoding.UTF8, ...) writes the UTF-8 bytes of `json` with NO BOM, and the
            // controller reads them back verbatim via StreamReader, so the bytes signed here match the bytes
            // the server HMACs. `json` is single-line (no CR/LF) to avoid any \r\n vs \n mismatch.
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath) { Content = content };
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

        private static string GenerateSignatureHeader(string payload, string secret)
        {
            // Replicates Stripe's webhook signing algorithm (Stripe.net has no public test-signing helper):
            // signed payload = "{timestamp}.{payload}", HMAC-SHA256 keyed by the webhook secret, emitted as
            // lowercase hex; header = "t={timestamp},v1={signature}". 100% offline -- no network.
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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
