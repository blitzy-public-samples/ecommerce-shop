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
        /// Offline no-op for the "payment succeeded" webhook path. Returns <c>null</c>, which matches
        /// the production contract where an unresolved payment intent yields no order. This stub is
        /// only wired in where the SUT would otherwise call Stripe live; sibling tests that need real
        /// webhook order transitions exercise the genuine <c>PaymentService</c> against the database
        /// instead.
        /// </summary>
        /// <param name="paymentIntentId">The Stripe payment-intent identifier from the webhook.</param>
        /// <returns>A completed task wrapping <c>null</c>.</returns>
        public Task<Order> UpdateOrderPaymentSucceeded(string paymentIntentId)
        {
            // offline stub: no Stripe, no DB access
            return Task.FromResult<Order>(null);
        }

        /// <summary>
        /// Offline no-op for the "payment failed" webhook path. Returns <c>null</c>, mirroring the
        /// production contract where an unresolved payment intent yields no order. No Stripe SDK usage
        /// and no database access occur.
        /// </summary>
        /// <param name="paymentIntentId">The Stripe payment-intent identifier from the webhook.</param>
        /// <returns>A completed task wrapping <c>null</c>.</returns>
        public Task<Order> UpdateOrderPaymentFailed(string paymentIntentId)
        {
            // offline stub: no Stripe, no DB access
            return Task.FromResult<Order>(null);
        }
    }
}
