using System.Threading.Tasks;
using Core.Entities;
using Core.Entities.OrderAggregate;
using Core.Interfaces;

namespace API.IntegrationTests.Infrastructure
{
    /// <summary>
    /// Offline, deterministic, side-effect-free substitute for
    /// <see cref="Core.Interfaces.IPaymentService"/> used by the integration-test harness.
    ///
    /// <para>
    /// The production implementation (<c>Infrastructure.Services.PaymentService</c>) reaches out to
    /// the live Stripe API and to Redis/PostgreSQL. That is unacceptable inside the integration
    /// suite, which is bound by the constraint "No live Stripe calls" (AAP §0.10.1) — Stripe must be
    /// mocked for unit tests, stubbed for integration tests, and only exercised via Stripe's offline
    /// signing utility for the dedicated webhook test.
    /// </para>
    ///
    /// <para>
    /// <c>CustomWebApplicationFactory</c> registers this stub through <c>ConfigureTestServices</c>
    /// (for example <c>services.AddScoped&lt;IPaymentService, StripePaymentServiceStub&gt;()</c>),
    /// replacing the production service so the ASP.NET Core application under test never performs any
    /// network I/O against Stripe during an integration run.
    /// </para>
    ///
    /// <para>
    /// Every method returns a synchronously-available, in-memory value via
    /// <see cref="System.Threading.Tasks.Task.FromResult{TResult}(TResult)"/>. There is no HTTP, no
    /// Stripe SDK usage, no Redis access, no database access, no configuration lookup, no randomness,
    /// and no clock-dependent branching — so results are fully deterministic and repeatable.
    /// </para>
    /// </summary>
    public class StripePaymentServiceStub : IPaymentService
    {
        /// <summary>
        /// Returns the supplied basket echoed back with fixed, fake Stripe payment-intent fields set,
        /// completely offline. No Stripe <c>PaymentIntentService</c> is created and no external call is
        /// made; the basket identity is preserved so callers can correlate the response with the
        /// request, and the deterministic <c>"pi_test_stub"</c>/<c>"pi_test_stub_secret"</c> values give
        /// consumers a stable payment-intent identity to assert on.
        /// </summary>
        /// <param name="basketId">The identifier of the basket a payment intent is requested for.</param>
        /// <returns>
        /// A completed task wrapping a <see cref="CustomerBasket"/> whose <c>Id</c> equals
        /// <paramref name="basketId"/> and whose <c>PaymentIntentId</c>/<c>ClientSecret</c> are the
        /// fixed stub values.
        /// </returns>
        public Task<CustomerBasket> CreateOrUpdatePaymentIntent(string basketId)
        {
            // offline stub: no Stripe, no Redis, no DB access — pure in-memory echo of the basket id
            var basket = new CustomerBasket(basketId)
            {
                PaymentIntentId = "pi_test_stub",
                ClientSecret = "pi_test_stub_secret"
            };

            return Task.FromResult(basket);
        }

        /// <summary>
        /// Offline, deterministic substitute for the "payment succeeded" webhook path. Returns a
        /// non-null <see cref="Order"/> whose <c>PaymentId</c> echoes the supplied
        /// <paramref name="paymentIntentId"/> and whose <c>Status</c> is
        /// <see cref="OrderStatus.PaymentReceived"/> — mirroring the production
        /// <c>PaymentService.UpdateOrderPaymentSucceeded</c> transition without any Stripe or database
        /// access.
        /// </summary>
        /// <remarks>
        /// A non-null result is REQUIRED by the system under test: <c>PaymentsController.StripeWebhook</c>
        /// dereferences <c>order.Id</c> immediately after this call (to log the updated order), so
        /// returning <c>null</c> here would raise a <see cref="System.NullReferenceException"/> and
        /// surface as an HTTP 500 instead of exercising the status transition. The returned order carries
        /// the payment identity and the target status so webhook tests can assert the transition
        /// deterministically.
        /// </remarks>
        /// <param name="paymentIntentId">The Stripe payment-intent identifier from the webhook.</param>
        /// <returns>
        /// A completed task wrapping an <see cref="Order"/> whose <c>PaymentId</c> equals
        /// <paramref name="paymentIntentId"/> and whose <c>Status</c> is
        /// <see cref="OrderStatus.PaymentReceived"/>.
        /// </returns>
        public Task<Order> UpdateOrderPaymentSucceeded(string paymentIntentId)
        {
            // offline stub: no Stripe, no DB access. Return a deterministic, non-null order carrying the
            // payment-intent id and the PaymentReceived status so the controller's order.Id dereference is
            // safe and the webhook status transition is observable to integration tests.
            var order = new Order
            {
                PaymentId = paymentIntentId,
                Status = OrderStatus.PaymentReceived
            };

            return Task.FromResult(order);
        }

        /// <summary>
        /// Offline, deterministic substitute for the "payment failed" webhook path. Returns a non-null
        /// <see cref="Order"/> whose <c>PaymentId</c> echoes the supplied
        /// <paramref name="paymentIntentId"/> and whose <c>Status</c> is
        /// <see cref="OrderStatus.PaymentFailed"/> — mirroring the production
        /// <c>PaymentService.UpdateOrderPaymentFailed</c> transition without any Stripe or database
        /// access.
        /// </summary>
        /// <remarks>
        /// As with the success path, a non-null result is REQUIRED because
        /// <c>PaymentsController.StripeWebhook</c> dereferences <c>order.Id</c> right after this call;
        /// returning <c>null</c> would raise a <see cref="System.NullReferenceException"/> (HTTP 500)
        /// rather than exercising the failed-payment transition.
        /// </remarks>
        /// <param name="paymentIntentId">The Stripe payment-intent identifier from the webhook.</param>
        /// <returns>
        /// A completed task wrapping an <see cref="Order"/> whose <c>PaymentId</c> equals
        /// <paramref name="paymentIntentId"/> and whose <c>Status</c> is
        /// <see cref="OrderStatus.PaymentFailed"/>.
        /// </returns>
        public Task<Order> UpdateOrderPaymentFailed(string paymentIntentId)
        {
            // offline stub: no Stripe, no DB access. Return a deterministic, non-null order carrying the
            // payment-intent id and the PaymentFailed status so the controller's order.Id dereference is
            // safe and the failed-payment transition is observable to integration tests.
            var order = new Order
            {
                PaymentId = paymentIntentId,
                Status = OrderStatus.PaymentFailed
            };

            return Task.FromResult(order);
        }
    }
}
