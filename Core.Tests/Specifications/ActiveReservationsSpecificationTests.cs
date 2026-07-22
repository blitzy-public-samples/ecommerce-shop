using System;
using System.Linq.Expressions;
// Production namespace quirk (verified in source): the specification classes physically
// live under Core/Specifications/*.cs, but every one of them declares
// `namespace API.Specifications` (NOT Core.Specifications). Import API.Specifications
// here to resolve ActiveReservationsSpecification and BaseSpecification<T>.
using API.Specifications;
using Core.Entities;
using FluentAssertions;
using Xunit;

namespace Core.Tests.Specifications
{
    /// <summary>
    /// Unit tests for <see cref="ActiveReservationsSpecification"/>.
    /// Exercises all three constructors by compiling the <c>Criteria</c> expression and
    /// invoking it in-memory against freshly created <see cref="Reservation"/> instances,
    /// then asserts the criteria-only structural invariants (no includes, ordering, or paging).
    /// </summary>
    public class ActiveReservationsSpecificationTests
    {
        private const int MatchingProductId = 42;
        private const int NonMatchingProductId = 99;
        private const string MatchingBasketId = "basket-1";
        private const string NonMatchingBasketId = "basket-2";

        private static Reservation MakeReservation(int productId, string basketId, ReservationStatus status) =>
            new Reservation { ProductId = productId, BasketId = basketId, Status = status };

        // ---------------------------------------------------------------------------------
        // (int productId) constructor
        // ---------------------------------------------------------------------------------

        /// <summary>The product-id constructor matches an Active reservation for that product.</summary>
        [Fact]
        public void ProductIdConstructor_MatchesActiveReservationForThatProduct()
        {
            // Arrange
            var spec = new ActiveReservationsSpecification(MatchingProductId);
            var reservation = MakeReservation(MatchingProductId, "any-basket", ReservationStatus.Active);

            // Act
            Expression<Func<Reservation, bool>> criteria = spec.Criteria;
            var predicate = criteria.Compile();

            // Assert
            predicate.Invoke(reservation).Should().BeTrue(
                "an Active reservation for the requested product must satisfy the specification");
        }

        /// <summary>The product-id constructor rejects a reservation belonging to a different product.</summary>
        [Fact]
        public void ProductIdConstructor_RejectsReservationForDifferentProduct()
        {
            var spec = new ActiveReservationsSpecification(MatchingProductId);
            var reservation = MakeReservation(NonMatchingProductId, "any-basket", ReservationStatus.Active);

            spec.Criteria.Compile().Invoke(reservation).Should().BeFalse(
                "a reservation for a different product must not satisfy the product-scoped specification");
        }

        /// <summary>The product-id constructor rejects non-Active reservations (proves the Status == Active conjunct).</summary>
        [Theory]
        [InlineData(ReservationStatus.Committed)]
        [InlineData(ReservationStatus.Expired)]
        [InlineData(ReservationStatus.Cancelled)]
        public void ProductIdConstructor_RejectsNonActiveReservation(ReservationStatus status)
        {
            var spec = new ActiveReservationsSpecification(MatchingProductId);
            var reservation = MakeReservation(MatchingProductId, "any-basket", status);

            spec.Criteria.Compile().Invoke(reservation).Should().BeFalse(
                "only Active reservations are in scope even when the product id matches");
        }

        /// <summary>The product-id constructor produces a criteria-only specification (no includes/ordering/paging).</summary>
        [Fact]
        public void ProductIdConstructor_HasNoIncludesOrderingOrPaging()
        {
            var spec = new ActiveReservationsSpecification(MatchingProductId);

            spec.Criteria.Should().NotBeNull("the specification is defined solely by its criteria");
            spec.Includes.Should().NotBeNull("Includes is initialized by BaseSpecification");
            spec.Includes.Should().BeEmpty("this specification adds no eager-load includes");
            spec.OrderBy.Should().BeNull("this specification applies no ascending ordering");
            spec.OrderByDescending.Should().BeNull("this specification applies no descending ordering");
            spec.IsPagingEnabled.Should().BeFalse("this specification does not enable paging");
            spec.Skip.Should().Be(0, "no paging means the default skip of 0");
            spec.Take.Should().Be(0, "no paging means the default take of 0");
        }

        // ---------------------------------------------------------------------------------
        // (string basketId) constructor
        // ---------------------------------------------------------------------------------

        /// <summary>The basket-id constructor matches an Active reservation for that basket.</summary>
        [Fact]
        public void BasketIdConstructor_MatchesActiveReservationForThatBasket()
        {
            var spec = new ActiveReservationsSpecification(MatchingBasketId);
            var reservation = MakeReservation(1, MatchingBasketId, ReservationStatus.Active);

            spec.Criteria.Compile().Invoke(reservation).Should().BeTrue(
                "an Active reservation for the requested basket must satisfy the specification");
        }

        /// <summary>The basket-id constructor rejects a reservation belonging to a different basket.</summary>
        [Fact]
        public void BasketIdConstructor_RejectsReservationForDifferentBasket()
        {
            var spec = new ActiveReservationsSpecification(MatchingBasketId);
            var reservation = MakeReservation(1, NonMatchingBasketId, ReservationStatus.Active);

            spec.Criteria.Compile().Invoke(reservation).Should().BeFalse(
                "a reservation for a different basket must not satisfy the basket-scoped specification");
        }

        /// <summary>The basket-id constructor rejects a non-Active reservation for the matching basket (proves the Status == Active conjunct).</summary>
        [Fact]
        public void BasketIdConstructor_RejectsNonActiveReservation()
        {
            var spec = new ActiveReservationsSpecification(MatchingBasketId);
            var reservation = MakeReservation(1, MatchingBasketId, ReservationStatus.Committed);

            spec.Criteria.Compile().Invoke(reservation).Should().BeFalse(
                "only Active reservations are in scope even when the basket id matches");
        }

        /// <summary>The basket-id constructor produces a criteria-only specification.</summary>
        [Fact]
        public void BasketIdConstructor_HasNoIncludesOrderingOrPaging()
        {
            var spec = new ActiveReservationsSpecification(MatchingBasketId);

            spec.Criteria.Should().NotBeNull("the specification is defined solely by its criteria");
            spec.Includes.Should().NotBeNull("Includes is initialized by BaseSpecification");
            spec.Includes.Should().BeEmpty("this specification adds no eager-load includes");
            spec.OrderBy.Should().BeNull("this specification applies no ascending ordering");
            spec.OrderByDescending.Should().BeNull("this specification applies no descending ordering");
            spec.IsPagingEnabled.Should().BeFalse("this specification does not enable paging");
            spec.Skip.Should().Be(0, "no paging means the default skip of 0");
            spec.Take.Should().Be(0, "no paging means the default take of 0");
        }

        // ---------------------------------------------------------------------------------
        // (int productId, string basketId) constructor
        // ---------------------------------------------------------------------------------

        /// <summary>The composite constructor matches only when product, basket, and Active status all align.</summary>
        [Fact]
        public void ProductAndBasketConstructor_MatchesWhenBothMatchAndActive()
        {
            var spec = new ActiveReservationsSpecification(MatchingProductId, MatchingBasketId);
            var reservation = MakeReservation(MatchingProductId, MatchingBasketId, ReservationStatus.Active);

            spec.Criteria.Compile().Invoke(reservation).Should().BeTrue(
                "an Active reservation matching both product and basket must satisfy the specification");
        }

        /// <summary>The composite constructor rejects when the product differs (proves the productId conjunct).</summary>
        [Fact]
        public void ProductAndBasketConstructor_RejectsWhenProductDiffers()
        {
            var spec = new ActiveReservationsSpecification(MatchingProductId, MatchingBasketId);
            var reservation = MakeReservation(NonMatchingProductId, MatchingBasketId, ReservationStatus.Active);

            spec.Criteria.Compile().Invoke(reservation).Should().BeFalse(
                "a differing product id must fail the composite specification even if basket and status match");
        }

        /// <summary>The composite constructor rejects when the basket differs (proves the basketId conjunct).</summary>
        [Fact]
        public void ProductAndBasketConstructor_RejectsWhenBasketDiffers()
        {
            var spec = new ActiveReservationsSpecification(MatchingProductId, MatchingBasketId);
            var reservation = MakeReservation(MatchingProductId, NonMatchingBasketId, ReservationStatus.Active);

            spec.Criteria.Compile().Invoke(reservation).Should().BeFalse(
                "a differing basket id must fail the composite specification even if product and status match");
        }

        /// <summary>The composite constructor rejects when the reservation is not Active (proves the Status conjunct).</summary>
        [Fact]
        public void ProductAndBasketConstructor_RejectsWhenNotActive()
        {
            var spec = new ActiveReservationsSpecification(MatchingProductId, MatchingBasketId);
            var reservation = MakeReservation(MatchingProductId, MatchingBasketId, ReservationStatus.Committed);

            spec.Criteria.Compile().Invoke(reservation).Should().BeFalse(
                "a non-Active reservation must fail the composite specification even if product and basket match");
        }

        /// <summary>The composite constructor produces a criteria-only specification.</summary>
        [Fact]
        public void ProductAndBasketConstructor_HasNoIncludesOrderingOrPaging()
        {
            var spec = new ActiveReservationsSpecification(MatchingProductId, MatchingBasketId);

            spec.Criteria.Should().NotBeNull("the specification is defined solely by its criteria");
            spec.Includes.Should().NotBeNull("Includes is initialized by BaseSpecification");
            spec.Includes.Should().BeEmpty("this specification adds no eager-load includes");
            spec.OrderBy.Should().BeNull("this specification applies no ascending ordering");
            spec.OrderByDescending.Should().BeNull("this specification applies no descending ordering");
            spec.IsPagingEnabled.Should().BeFalse("this specification does not enable paging");
            spec.Skip.Should().Be(0, "no paging means the default skip of 0");
            spec.Take.Should().Be(0, "no paging means the default take of 0");
        }
    }
}
