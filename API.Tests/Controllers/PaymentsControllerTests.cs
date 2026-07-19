using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using API.Controllers;
using API.Errors;
using Core.Entities;                          // CustomerBasket
using Core.Interfaces;                        // IPaymentService
using FluentAssertions;
using Microsoft.AspNetCore.Http;              // DefaultHttpContext
using Microsoft.AspNetCore.Mvc;               // ControllerContext, BadRequestObjectResult, EmptyResult
using Microsoft.Extensions.Configuration;     // ConfigurationBuilder, IConfiguration (unambiguous — AutoMapper.Configuration NOT imported)
using Microsoft.Extensions.Logging;           // ILogger<>
using Moq;
using Stripe;                                 // StripeException, EventUtility (via ctrl), StripeConfiguration
using Xunit;
// Alias mirrors the production PaymentsController.cs: both Stripe (Stripe.Order) and the Core
// order aggregate declare an "Order" type, so the bare identifier is ambiguous (CS0104). This
// binds "Order" to the domain entity returned by the mocked IPaymentService.
using Order = Core.Entities.OrderAggregate.Order;

namespace API.Tests.Controllers
{
    /// <summary>
    /// Unit tests for <see cref="PaymentsController"/>.
    /// <para>
    /// The controller is instantiated directly (no HTTP pipeline) with a Moq-mocked
    /// <see cref="IPaymentService"/>, a mocked <see cref="ILogger{PaymentsController}"/>, and an
    /// in-memory <see cref="IConfiguration"/> that supplies the <c>StripeSettings:WhSecret</c> key
    /// the constructor reads. Every test builds a <b>fresh</b> set of mocks (test isolation, AAP §0.7.2).
    /// </para>
    /// <para>
    /// <b>Binding constraint (AAP §0.10.1): no live Stripe API calls.</b> The webhook tests are fully
    /// offline. <c>EventUtility.ConstructEvent</c> performs only local HMAC signature verification and
    /// JSON deserialization — no network. The invalid-signature test asserts the resulting
    /// <see cref="StripeException"/>. The two valid-signature tests sign the payload locally with the
    /// same HMAC-SHA256 scheme Stripe uses, so the controller's <c>switch</c> branches execute without
    /// any outbound call (the payment-service collaborators are mocked). Signing offline is explicitly
    /// permitted by the special instruction "use Stripe's offline test-mode signing utility for the
    /// webhook test"; these two happy-path tests are what raise <see cref="PaymentsController"/> line
    /// coverage past the mandated ≥70% (the exception test alone reaches only ~43% because
    /// <c>ConstructEvent</c> throws before the switch).
    /// </para>
    /// </summary>
    public class PaymentsControllerTests
    {
        /// <summary>
        /// Fake, obviously-non-production webhook signing secret shared by the controller
        /// configuration and the local signature computation so the two agree. Not a real Stripe key.
        /// </summary>
        private const string WebhookSecret = "whsec_test_dummy";

        // ------------------------------------------------------------------
        // Arrange helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds an in-memory <see cref="IConfiguration"/> carrying the single
        /// <c>StripeSettings:WhSecret</c> entry the <see cref="PaymentsController"/> constructor reads.
        /// </summary>
        private static IConfiguration BuildConfig(string whSecret = WebhookSecret) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    { "StripeSettings:WhSecret", whSecret }
                })
                .Build();

        /// <summary>
        /// Constructs the system under test from the supplied mocks and an in-memory configuration.
        /// </summary>
        private static PaymentsController CreateController(
            Mock<IPaymentService> paymentService,
            Mock<ILogger<PaymentsController>> logger,
            string whSecret = WebhookSecret) =>
            new PaymentsController(paymentService.Object, logger.Object, BuildConfig(whSecret));

        /// <summary>
        /// Produces a minimal but well-formed Stripe <c>Event</c> JSON payload.
        /// <para>
        /// The <c>api_version</c> is set to <see cref="StripeConfiguration.ApiVersion"/> at runtime so
        /// that <c>EventUtility.ConstructEvent</c> (whose <c>throwOnApiVersionMismatch</c> parameter
        /// defaults to <c>true</c>) accepts the event. Reading the version at runtime keeps the test
        /// robust across Stripe.net patch releases. The nested <c>data.object</c> carries
        /// <c>"object":"payment_intent"</c> so Stripe's polymorphic converter materializes a
        /// <see cref="PaymentIntent"/>, which the controller casts and whose <c>Id</c> it forwards to
        /// the payment service.
        /// </para>
        /// <para>
        /// The <c>"request":null</c> member is deliberate and required: Stripe.net 39.66.0's event
        /// deserializer throws a <see cref="System.NullReferenceException"/> when the <c>request</c> key
        /// is entirely absent from the payload. Supplying an explicit JSON null (a legitimate value for
        /// webhook events that were not triggered by a direct API call) satisfies the deserializer.
        /// </para>
        /// </summary>
        private static string BuildEventPayload(string eventType, string paymentIntentId)
        {
            var apiVersion = StripeConfiguration.ApiVersion;
            return
                "{" +
                "\"id\":\"evt_test_webhook\"," +
                "\"object\":\"event\"," +
                "\"api_version\":\"" + apiVersion + "\"," +
                "\"request\":null," +
                "\"type\":\"" + eventType + "\"," +
                "\"data\":{\"object\":{\"id\":\"" + paymentIntentId + "\",\"object\":\"payment_intent\"}}" +
                "}";
        }

        /// <summary>
        /// Computes a valid <c>Stripe-Signature</c> header for the supplied payload entirely offline,
        /// mirroring Stripe's signing scheme: <c>HMAC-SHA256(key = secret bytes, message =
        /// "{unixTimestamp}.{payload}")</c> rendered as lowercase hex, emitted as
        /// <c>t={timestamp},v1={signature}</c>. Uses the current time so the value falls inside
        /// <c>ConstructEvent</c>'s default 300-second tolerance window. No network access is performed.
        /// </summary>
        private static string BuildSignatureHeader(string payload, string secret)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var signedPayload = $"{timestamp}.{payload}";

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));
            var signature = BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();

            return $"t={timestamp},v1={signature}";
        }

        /// <summary>
        /// Attaches a fresh <see cref="ControllerContext"/> to the controller whose request body streams
        /// the supplied payload and whose <c>Stripe-Signature</c> header carries the supplied value.
        /// This is how <c>HttpContext.Request.Body</c> / <c>Request.Headers</c> are populated in a unit
        /// test where there is no real request pipeline.
        /// </summary>
        private static void AttachWebhookRequest(
            PaymentsController controller, string payload, string signatureHeader)
        {
            var context = new DefaultHttpContext();
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
            context.Request.Headers["Stripe-Signature"] = signatureHeader;
            controller.ControllerContext = new ControllerContext { HttpContext = context };
        }

        // ------------------------------------------------------------------
        // CreateOrUpdatePaymentIntent
        // ------------------------------------------------------------------

        [Fact]
        public async Task CreateOrUpdatePaymentIntent_WhenServiceReturnsBasket_ReturnsBasketValue()
        {
            // Arrange
            var paymentService = new Mock<IPaymentService>();
            var logger = new Mock<ILogger<PaymentsController>>();
            var basket = new CustomerBasket("basket-1");
            paymentService
                .Setup(s => s.CreateOrUpdatePaymentIntent("basket-1"))
                .ReturnsAsync(basket);
            var controller = CreateController(paymentService, logger);

            // Act
            var result = await controller.CreateOrUpdatePaymentIntent("basket-1");

            // Assert
            // A direct `return basket;` populates ActionResult<T>.Value and leaves .Result null.
            result.Value.Should().BeSameAs(basket);
            result.Result.Should().BeNull();
            paymentService.Verify(s => s.CreateOrUpdatePaymentIntent("basket-1"), Times.Once);
        }

        [Fact]
        public async Task CreateOrUpdatePaymentIntent_WhenServiceReturnsNull_ReturnsBadRequestWithApiResponse()
        {
            // Arrange
            var paymentService = new Mock<IPaymentService>();
            var logger = new Mock<ILogger<PaymentsController>>();
            paymentService
                .Setup(s => s.CreateOrUpdatePaymentIntent(It.IsAny<string>()))
                .ReturnsAsync((CustomerBasket)null);
            var controller = CreateController(paymentService, logger);

            // Act
            var result = await controller.CreateOrUpdatePaymentIntent("missing-basket");

            // Assert
            // A null basket routes to BadRequest(new ApiResponse(400, "Problem with your basket")):
            // .Result carries the BadRequestObjectResult and .Value is null.
            result.Value.Should().BeNull();
            var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
            badRequest.StatusCode.Should().Be(400);
            var apiResponse = badRequest.Value.Should().BeOfType<ApiResponse>().Subject;
            apiResponse.StatusCode.Should().Be(400);
            apiResponse.Message.Should().Be("Problem with your basket");
        }

        // ------------------------------------------------------------------
        // Constructor
        // ------------------------------------------------------------------

        [Fact]
        public void Constructor_WithConfiguredWhSecret_ConstructsWithoutThrowing()
        {
            // Arrange
            var paymentService = new Mock<IPaymentService>();
            var logger = new Mock<ILogger<PaymentsController>>();

            // Act
            // Documents that the constructor reads StripeSettings:WhSecret and completes without throwing
            // when the key is present.
            var controller = CreateController(paymentService, logger, "whsec_documented_secret");

            // Assert
            controller.Should().NotBeNull();
        }

        // ------------------------------------------------------------------
        // StripeWebhook — invalid signature (offline exception path)
        // ------------------------------------------------------------------

        [Fact]
        public async Task StripeWebhook_WithInvalidSignature_ThrowsStripeException()
        {
            // Arrange
            var paymentService = new Mock<IPaymentService>();
            var logger = new Mock<ILogger<PaymentsController>>();
            var controller = CreateController(paymentService, logger);
            AttachWebhookRequest(controller, "{\"id\":\"evt_test\"}", "t=123,v1=invalid_signature");

            // Act
            Func<Task> act = () => controller.StripeWebhook();

            // Assert
            // EventUtility.ConstructEvent performs LOCAL HMAC verification; a bogus signature throws a
            // StripeException (its SignatureVerificationException subclass) with no network call.
            // Asserting the base type is robust across Stripe.net patch versions.
            await act.Should().ThrowAsync<StripeException>();
            paymentService.Verify(s => s.UpdateOrderPaymentSucceeded(It.IsAny<string>()), Times.Never);
            paymentService.Verify(s => s.UpdateOrderPaymentFailed(It.IsAny<string>()), Times.Never);
        }

        // ------------------------------------------------------------------
        // StripeWebhook — valid signature (offline-signed happy paths)
        //
        // These exercise the controller's event switch without any live Stripe
        // call: the payload is signed locally with the same secret configured on
        // the controller, and the payment-service collaborators are mocked. They
        // are the tests that carry PaymentsController line coverage past ≥70%.
        // ------------------------------------------------------------------

        [Fact]
        public async Task StripeWebhook_WhenPaymentIntentSucceeded_ReturnsEmptyResultAndUpdatesOrder()
        {
            // Arrange
            var paymentService = new Mock<IPaymentService>();
            var logger = new Mock<ILogger<PaymentsController>>();
            // Return a non-null Order so the controller's `order.Id` log call does not dereference null.
            paymentService
                .Setup(s => s.UpdateOrderPaymentSucceeded(It.IsAny<string>()))
                .ReturnsAsync(new Order());
            var controller = CreateController(paymentService, logger);

            var payload = BuildEventPayload("payment_intent.succeeded", "pi_succeeded_1");
            AttachWebhookRequest(controller, payload, BuildSignatureHeader(payload, WebhookSecret));

            // Act
            var result = await controller.StripeWebhook();

            // Assert
            result.Should().BeOfType<EmptyResult>();
            paymentService.Verify(s => s.UpdateOrderPaymentSucceeded("pi_succeeded_1"), Times.Once);
            paymentService.Verify(s => s.UpdateOrderPaymentFailed(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task StripeWebhook_WhenPaymentIntentFailed_ReturnsEmptyResultAndMarksFailed()
        {
            // Arrange
            var paymentService = new Mock<IPaymentService>();
            var logger = new Mock<ILogger<PaymentsController>>();
            paymentService
                .Setup(s => s.UpdateOrderPaymentFailed(It.IsAny<string>()))
                .ReturnsAsync(new Order());
            var controller = CreateController(paymentService, logger);

            var payload = BuildEventPayload("payment_intent.payment_failed", "pi_failed_1");
            AttachWebhookRequest(controller, payload, BuildSignatureHeader(payload, WebhookSecret));

            // Act
            var result = await controller.StripeWebhook();

            // Assert
            result.Should().BeOfType<EmptyResult>();
            paymentService.Verify(s => s.UpdateOrderPaymentFailed("pi_failed_1"), Times.Once);
            paymentService.Verify(s => s.UpdateOrderPaymentSucceeded(It.IsAny<string>()), Times.Never);
        }
    }
}
