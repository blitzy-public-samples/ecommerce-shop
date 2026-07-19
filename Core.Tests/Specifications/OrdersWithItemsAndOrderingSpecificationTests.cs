using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Core.Entities.OrderAggregate;
using FluentAssertions;
using Xunit;

// NOTE (namespace quirk — verified in production source):
// Although the specification classes physically live under Core/Specifications/, every
// file in that folder declares `namespace API.Specifications`. The type under test
// (OrdersWithItemsAndOrderingSpecification) and its base (BaseSpecification<T>) are therefore
// imported from API.Specifications, NOT Core.Specifications. This `using` is intentional and required.
using API.Specifications;

namespace Core.Tests.Specifications
{
    /// <summary>
    /// Pure-logic unit tests for <see cref="OrdersWithItemsAndOrderingSpecification"/>.
    ///
    /// The specification exposes two constructors, both inheriting the public surface of
    /// <c>BaseSpecification&lt;Order&gt;</c> (Criteria, Includes, OrderBy, OrderByDescending,
    /// IsPagingEnabled):
    ///   1. <c>OrdersWithItemsAndOrderingSpecification(string email)</c> — list-by-email:
    ///      criteria matches <c>BuyerEmail</c>, eagerly loads <c>OrderItems</c> and
    ///      <c>DeliveryMethod</c>, and orders by <c>OrderDate</c> descending.
    ///   2. <c>OrdersWithItemsAndOrderingSpecification(int id, string email)</c> — fetch-by-id-and-email:
    ///      criteria is the logical AND of <c>Id</c> and <c>BuyerEmail</c>, eagerly loads the same
    ///      includes, and applies NO ordering.
    ///
    /// These are pure specification-construction tests: no mocking is used. Each test constructs the
    /// specification directly and inspects its declarative public properties, compiling the criteria
    /// and selector expressions where behavior (rather than mere presence) needs to be asserted.
    /// Every test builds fresh <see cref="Order"/> instances and a fresh specification so that no
    /// mutable state is shared between tests, guaranteeing isolation and parallelizability.
    ///
    /// Naming follows the repository convention: MethodName_StateUnderTest_ExpectedBehavior.
    /// </summary>
    public class OrdersWithItemsAndOrderingSpecificationTests
    {
        // A representative, deterministic buyer email reused across the "match" arrangements.
        private const string MatchingEmail = "bob@test.com";

        // A distinct email used to prove the criteria rejects non-matching buyers.
        private const string NonMatchingEmail = "alice@test.com";

        /// <summary>
        /// Builds a minimal, fully-isolated <see cref="Order"/> for criteria evaluation.
        /// Only <c>Id</c> and <c>BuyerEmail</c> participate in the specifications under test, so
        /// those are the only fields set here; every call returns a brand-new instance.
        /// </summary>
        private static Order MakeOrder(int id, string email) => new Order { Id = id, BuyerEmail = email };

        /// <summary>
        /// Builds an <see cref="Order"/> whose <c>OrderItems</c> and <c>DeliveryMethod</c> reference
        /// navigations are set to the supplied DISTINCT instances. Compiling an include selector and
        /// invoking it against this order returns the exact navigation the selector targets, letting a
        /// test pin which member each include projects rather than merely counting the includes.
        /// </summary>
        private static Order MakeOrderWithNavigations(IReadOnlyList<OrderItem> orderItems, DeliveryMethod deliveryMethod)
            => new Order { Id = 1, BuyerEmail = MatchingEmail, OrderItems = orderItems, DeliveryMethod = deliveryMethod };

        // ---------------------------------------------------------------------------------------
        // Phase 1 — Email constructor: OrdersWithItemsAndOrderingSpecification(string email)
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void EmailConstructor_WithBuyerEmail_SetsCriteriaMatchingBuyerEmail()
        {
            // Arrange & Act
            var spec = new OrdersWithItemsAndOrderingSpecification(MatchingEmail);

            // Assert — criteria exists and is a predicate on BuyerEmail.
            spec.Criteria.Should().NotBeNull();

            var predicate = spec.Criteria.Compile();
            predicate.Invoke(MakeOrder(1, MatchingEmail)).Should().BeTrue();
            predicate.Invoke(MakeOrder(1, NonMatchingEmail)).Should().BeFalse();
        }

        [Fact]
        public void EmailConstructor_WithBuyerEmail_AddsOrderItemsAndDeliveryMethodIncludes()
        {
            // Arrange & Act
            var spec = new OrdersWithItemsAndOrderingSpecification(MatchingEmail);

            // Assert — exactly two eager-load includes, in order: [0] OrderItems, [1] DeliveryMethod.
            spec.Includes.Should().HaveCount(2);
            // Pin each selector via compile + invoke against an order carrying DISTINCT OrderItems and
            // DeliveryMethod instances. A wrong/duplicate/reordered include would fail BeSameAs even
            // though the count stays 2 — which would otherwise let order DTOs silently lose OrderItems
            // or DeliveryMethod eager loading.
            var orderItems = new List<OrderItem> { new OrderItem() };
            var deliveryMethod = new DeliveryMethod { Id = 3, ShortName = "UPS1" };
            var order = MakeOrderWithNavigations(orderItems, deliveryMethod);
            spec.Includes[0].Compile().Invoke(order).Should().BeSameAs(orderItems);
            spec.Includes[1].Compile().Invoke(order).Should().BeSameAs(deliveryMethod);
        }

        [Fact]
        public void EmailConstructor_WithBuyerEmail_OrdersByOrderDateDescending()
        {
            // Arrange
            var spec = new OrdersWithItemsAndOrderingSpecification(MatchingEmail);
            var knownDate = new DateTimeOffset(2021, 1, 2, 3, 4, 5, TimeSpan.Zero);
            var order = new Order { Id = 1, BuyerEmail = MatchingEmail, OrderDate = knownDate };

            // Assert — a descending selector is set and the ascending one is not.
            spec.OrderByDescending.Should().NotBeNull();
            spec.OrderBy.Should().BeNull();

            // Assert — the descending selector projects OrderDate specifically. The selector is typed
            // Expression<Func<Order, object>>, so the DateTimeOffset is boxed; cast back before comparing.
            var selector = spec.OrderByDescending.Compile();
            ((DateTimeOffset)selector.Invoke(order)).Should().Be(knownDate);
        }

        [Fact]
        public void EmailConstructor_WithBuyerEmail_DoesNotEnablePaging()
        {
            // Arrange & Act
            var spec = new OrdersWithItemsAndOrderingSpecification(MatchingEmail);

            // Assert — the email constructor never calls ApplyPaging.
            spec.IsPagingEnabled.Should().BeFalse();
        }

        // ---------------------------------------------------------------------------------------
        // Phase 2 — Id+Email constructor: OrdersWithItemsAndOrderingSpecification(int id, string email)
        // The criteria is a logical AND (o.Id == id && o.BuyerEmail == email); the two "reject" tests
        // prove BOTH conjuncts are evaluated.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void IdAndEmailConstructor_CriteriaMatches_WhenBothIdAndEmailMatch()
        {
            // Arrange & Act
            var spec = new OrdersWithItemsAndOrderingSpecification(3, MatchingEmail);

            // Assert — both conjuncts satisfied -> true.
            spec.Criteria.Should().NotBeNull();
            spec.Criteria.Compile().Invoke(MakeOrder(3, MatchingEmail)).Should().BeTrue();
        }

        [Fact]
        public void IdAndEmailConstructor_CriteriaRejects_WhenIdDiffers()
        {
            // Arrange & Act
            var spec = new OrdersWithItemsAndOrderingSpecification(3, MatchingEmail);

            // Assert — matching email but wrong id -> the Id conjunct fails -> false.
            spec.Criteria.Compile().Invoke(MakeOrder(9, MatchingEmail)).Should().BeFalse();
        }

        [Fact]
        public void IdAndEmailConstructor_CriteriaRejects_WhenEmailDiffers()
        {
            // Arrange & Act
            var spec = new OrdersWithItemsAndOrderingSpecification(3, MatchingEmail);

            // Assert — matching id but wrong email -> the BuyerEmail conjunct fails -> false.
            spec.Criteria.Compile().Invoke(MakeOrder(3, NonMatchingEmail)).Should().BeFalse();
        }

        [Fact]
        public void IdAndEmailConstructor_WithIdAndEmail_AddsOrderItemsAndDeliveryMethodIncludes()
        {
            // Arrange & Act
            var spec = new OrdersWithItemsAndOrderingSpecification(3, MatchingEmail);

            // Assert — same two eager-load includes, same order, as the email constructor:
            // [0] OrderItems, [1] DeliveryMethod. Pin each selector via compile + invoke against
            // distinct instances so a wrong/duplicate/reordered include is caught, not just the count.
            spec.Includes.Should().HaveCount(2);
            var orderItems = new List<OrderItem> { new OrderItem() };
            var deliveryMethod = new DeliveryMethod { Id = 3, ShortName = "UPS1" };
            var order = MakeOrderWithNavigations(orderItems, deliveryMethod);
            spec.Includes[0].Compile().Invoke(order).Should().BeSameAs(orderItems);
            spec.Includes[1].Compile().Invoke(order).Should().BeSameAs(deliveryMethod);
        }

        [Fact]
        public void IdAndEmailConstructor_WithIdAndEmail_DoesNotSetOrdering()
        {
            // Arrange & Act
            var spec = new OrdersWithItemsAndOrderingSpecification(3, MatchingEmail);

            // Assert — this overload intentionally applies neither ascending nor descending ordering,
            // and never enables paging.
            spec.OrderBy.Should().BeNull();
            spec.OrderByDescending.Should().BeNull();
            spec.IsPagingEnabled.Should().BeFalse();
        }
    }
}
