using System;
using System.Linq.Expressions;
using API.Specifications;
using Core.Entities.OrderAggregate;
using FluentAssertions;
using Xunit;

namespace Core.Tests.Specifications
{
    /// <summary>
    /// Unit tests for <see cref="OrderByPaymentIntentIdSpecification"/>.
    ///
    /// <para>
    /// The subject under test is a minimal specification that derives from
    /// <see cref="BaseSpecification{T}"/> and wraps a single equality criterion
    /// over <see cref="Order.PaymentId"/>:
    /// <code>
    ///     public OrderByPaymentIntentIdSpecification(string paymentIntentId)
    ///         : base(o =&gt; o.PaymentId == paymentIntentId) { }
    /// </code>
    /// It forwards <em>only</em> a criteria to the base class — it adds no eager
    /// includes, no ordering, and no paging.
    /// </para>
    ///
    /// <para>
    /// These are pure-logic tests, so no mocking framework is used. Each test
    /// constructs the specification directly and then either compiles/invokes the
    /// resulting <see cref="BaseSpecification{T}.Criteria"/> expression or asserts
    /// the structural invariants inherited from the base class.
    /// </para>
    ///
    /// <para>
    /// NAMESPACE QUIRK: although the production specification source file lives
    /// physically under <c>Core/Specifications/</c>, it declares
    /// <c>namespace API.Specifications</c>. That is why this test file imports
    /// <c>using API.Specifications;</c> rather than <c>using Core.Specifications;</c>.
    /// </para>
    ///
    /// <para>
    /// Conventions: xUnit <see cref="FactAttribute"/>, Arrange-Act-Assert,
    /// FluentAssertions, and the <c>MethodName_StateUnderTest_ExpectedBehavior</c>
    /// naming scheme. Every test builds its own fresh <see cref="Order"/> and
    /// specification instance, so the class holds no shared mutable state and is
    /// safe to execute in parallel.
    /// </para>
    /// </summary>
    public class OrderByPaymentIntentIdSpecificationTests
    {
        /// <summary>
        /// The canonical PaymentIntent identifier the specification is expected to
        /// match. Stripe PaymentIntent identifiers conventionally use the
        /// <c>pi_</c> prefix.
        /// </summary>
        private const string MatchingPaymentIntentId = "pi_12345";

        /// <summary>
        /// A PaymentIntent identifier that deliberately differs from
        /// <see cref="MatchingPaymentIntentId"/>. Used to prove the criterion
        /// rejects orders that reference a different payment intent.
        /// </summary>
        private const string NonMatchingPaymentIntentId = "pi_99999";

        /// <summary>
        /// Builds a fresh <see cref="Order"/> whose <see cref="Order.PaymentId"/>
        /// is set to the supplied value. Only <c>PaymentId</c> is relevant to these
        /// tests, so every other property is intentionally left at its default.
        /// Note that <see cref="Order.GetTotal"/> is never invoked here because it
        /// would dereference the (deliberately null) delivery method.
        /// </summary>
        /// <param name="paymentId">The value to assign to <see cref="Order.PaymentId"/>.</param>
        /// <returns>A new, isolated <see cref="Order"/> instance.</returns>
        private static Order MakeOrder(string paymentId) => new Order { PaymentId = paymentId };

        /// <summary>
        /// The constructor must forward a non-null criteria that evaluates to
        /// <c>true</c> for an order whose <see cref="Order.PaymentId"/> equals the
        /// requested payment intent id.
        /// </summary>
        [Fact]
        public void Constructor_SetsCriteriaMatchingPaymentId()
        {
            // Arrange & Act
            var spec = new OrderByPaymentIntentIdSpecification(MatchingPaymentIntentId);

            // Assert
            spec.Criteria.Should().NotBeNull(
                "the specification must forward an equality predicate to BaseSpecification");

            // Capture the criteria explicitly as an Expression<T> so the compiled
            // delegate can be invoked against a concrete order instance.
            Expression<Func<Order, bool>> criteria = spec.Criteria;
            Func<Order, bool> predicate = criteria.Compile();

            predicate.Invoke(MakeOrder(MatchingPaymentIntentId)).Should().BeTrue(
                "an order whose PaymentId equals the requested id must satisfy the criteria");
        }

        /// <summary>
        /// The forwarded criteria must evaluate to <c>false</c> for an order whose
        /// <see cref="Order.PaymentId"/> differs from the requested payment intent id.
        /// </summary>
        [Fact]
        public void Constructor_CriteriaRejectsDifferentPaymentId()
        {
            // Arrange & Act
            var spec = new OrderByPaymentIntentIdSpecification(MatchingPaymentIntentId);

            // Assert
            Expression<Func<Order, bool>> criteria = spec.Criteria;
            Func<Order, bool> predicate = criteria.Compile();

            predicate.Invoke(MakeOrder(NonMatchingPaymentIntentId)).Should().BeFalse(
                "an order whose PaymentId differs from the requested id must not satisfy the criteria");
        }

        /// <summary>
        /// This specification adds no eager-loading includes, so the inherited
        /// <see cref="BaseSpecification{T}.Includes"/> collection must exist and be empty.
        /// </summary>
        [Fact]
        public void Constructor_HasNoIncludes()
        {
            // Arrange & Act
            var spec = new OrderByPaymentIntentIdSpecification(MatchingPaymentIntentId);

            // Assert
            spec.Includes.Should().NotBeNull(
                "BaseSpecification initialises the Includes collection eagerly");
            spec.Includes.Should().BeEmpty(
                "this specification does not register any eager-loading includes");
        }

        /// <summary>
        /// This specification applies neither an ascending nor a descending sort,
        /// so both ordering expressions must be null.
        /// </summary>
        [Fact]
        public void Constructor_HasNoOrdering()
        {
            // Arrange & Act
            var spec = new OrderByPaymentIntentIdSpecification(MatchingPaymentIntentId);

            // Assert
            spec.OrderBy.Should().BeNull("this specification does not apply an ascending sort");
            spec.OrderByDescending.Should().BeNull("this specification does not apply a descending sort");
        }

        /// <summary>
        /// This specification never calls <c>ApplyPaging</c>, so paging must be
        /// disabled and both <see cref="BaseSpecification{T}.Skip"/> and
        /// <see cref="BaseSpecification{T}.Take"/> must remain at their defaults.
        /// </summary>
        [Fact]
        public void Constructor_DoesNotEnablePaging()
        {
            // Arrange & Act
            var spec = new OrderByPaymentIntentIdSpecification(MatchingPaymentIntentId);

            // Assert
            spec.IsPagingEnabled.Should().BeFalse("this specification never calls ApplyPaging");
            spec.Skip.Should().Be(0, "paging is disabled, so Skip stays at its default value");
            spec.Take.Should().Be(0, "paging is disabled, so Take stays at its default value");
        }
    }
}
