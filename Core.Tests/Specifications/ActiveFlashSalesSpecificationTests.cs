using System;
using System.Linq.Expressions;
// Production namespace quirk (verified in source): ActiveFlashSalesSpecification physically
// lives at Core/Specifications/ActiveFlashSalesSpecification.cs but declares
// `namespace API.Specifications` (NOT Core.Specifications). Import API.Specifications to resolve it.
using API.Specifications;
using Core.Entities;
using FluentAssertions;
using Xunit;

namespace Core.Tests.Specifications
{
    /// <summary>
    /// Unit tests for <see cref="ActiveFlashSalesSpecification"/>.
    /// Verifies both constructors select flash sales that are Active and whose window contains the
    /// reference instant, honoring an inclusive start (&lt;=) and an exclusive end (&lt;). Also asserts
    /// the criteria-only structural invariants (no includes, ordering, or paging).
    /// </summary>
    public class ActiveFlashSalesSpecificationTests
    {
        private static readonly DateTimeOffset Now = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        private const int MatchingProductId = 7;
        private const int NonMatchingProductId = 8;

        private static FlashSale MakeFlashSale(int productId, DateTimeOffset startsAt, DateTimeOffset endsAt, FlashSaleStatus status) =>
            new FlashSale { ProductId = productId, StartsAt = startsAt, EndsAt = endsAt, Status = status };

        // ---------------------------------------------------------------------------------
        // (DateTimeOffset now) constructor
        // ---------------------------------------------------------------------------------

        /// <summary>An Active sale whose window contains now is matched.</summary>
        [Fact]
        public void NowConstructor_MatchesActiveSaleWhoseWindowContainsNow()
        {
            // Arrange
            var spec = new ActiveFlashSalesSpecification(Now);
            var sale = MakeFlashSale(MatchingProductId, Now.AddHours(-1), Now.AddHours(1), FlashSaleStatus.Active);

            // Act
            Expression<Func<FlashSale, bool>> criteria = spec.Criteria;
            var predicate = criteria.Compile();

            // Assert
            predicate.Invoke(sale).Should().BeTrue(
                "an Active sale whose window straddles now must satisfy the specification");
        }

        /// <summary>A sale whose StartsAt equals now is matched (proves the inclusive start boundary).</summary>
        [Fact]
        public void NowConstructor_MatchesWhenStartsAtEqualsNow()
        {
            var spec = new ActiveFlashSalesSpecification(Now);
            var sale = MakeFlashSale(MatchingProductId, Now, Now.AddHours(1), FlashSaleStatus.Active);

            spec.Criteria.Compile().Invoke(sale).Should().BeTrue(
                "the window start is inclusive (StartsAt <= now), so a sale starting exactly at now is active");
        }

        /// <summary>A sale whose EndsAt equals now is NOT matched (proves the exclusive end boundary).</summary>
        [Fact]
        public void NowConstructor_RejectsWhenEndsAtEqualsNow()
        {
            var spec = new ActiveFlashSalesSpecification(Now);
            var sale = MakeFlashSale(MatchingProductId, Now.AddHours(-1), Now, FlashSaleStatus.Active);

            spec.Criteria.Compile().Invoke(sale).Should().BeFalse(
                "the window end is exclusive (now < EndsAt), so a sale ending exactly at now is no longer active");
        }

        /// <summary>A sale whose window starts after now is NOT matched.</summary>
        [Fact]
        public void NowConstructor_RejectsSaleWindowStartingAfterNow()
        {
            var spec = new ActiveFlashSalesSpecification(Now);
            var sale = MakeFlashSale(MatchingProductId, Now.AddHours(1), Now.AddHours(2), FlashSaleStatus.Active);

            spec.Criteria.Compile().Invoke(sale).Should().BeFalse(
                "a sale whose window begins after now has not started yet");
        }

        /// <summary>A sale whose window ended before now is NOT matched.</summary>
        [Fact]
        public void NowConstructor_RejectsSaleWindowEndedBeforeNow()
        {
            var spec = new ActiveFlashSalesSpecification(Now);
            var sale = MakeFlashSale(MatchingProductId, Now.AddHours(-2), Now.AddHours(-1), FlashSaleStatus.Active);

            spec.Criteria.Compile().Invoke(sale).Should().BeFalse(
                "a sale whose window ended before now is no longer active");
        }

        /// <summary>A window-containing sale that is not Active is NOT matched (proves the Status == Active conjunct).</summary>
        [Theory]
        [InlineData(FlashSaleStatus.Scheduled)]
        [InlineData(FlashSaleStatus.Ended)]
        public void NowConstructor_RejectsNonActiveSaleEvenIfWindowContainsNow(FlashSaleStatus status)
        {
            var spec = new ActiveFlashSalesSpecification(Now);
            var sale = MakeFlashSale(MatchingProductId, Now.AddHours(-1), Now.AddHours(1), status);

            spec.Criteria.Compile().Invoke(sale).Should().BeFalse(
                "only sales with Status == Active are selected even when the window contains now");
        }

        /// <summary>The now-only constructor produces a criteria-only specification (no includes/ordering/paging).</summary>
        [Fact]
        public void NowConstructor_HasNoIncludesOrderingOrPaging()
        {
            var spec = new ActiveFlashSalesSpecification(Now);

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
        // (int productId, DateTimeOffset now) constructor
        // ---------------------------------------------------------------------------------

        /// <summary>The product-scoped constructor matches an Active in-window sale for that product.</summary>
        [Fact]
        public void ProductAndNowConstructor_MatchesActiveInWindowSaleForThatProduct()
        {
            var spec = new ActiveFlashSalesSpecification(MatchingProductId, Now);
            var sale = MakeFlashSale(MatchingProductId, Now.AddHours(-1), Now.AddHours(1), FlashSaleStatus.Active);

            spec.Criteria.Compile().Invoke(sale).Should().BeTrue(
                "an Active in-window sale for the requested product must satisfy the specification");
        }

        /// <summary>The product-scoped constructor rejects a sale for a different product (proves the productId conjunct).</summary>
        [Fact]
        public void ProductAndNowConstructor_RejectsSaleForDifferentProduct()
        {
            var spec = new ActiveFlashSalesSpecification(MatchingProductId, Now);
            var sale = MakeFlashSale(NonMatchingProductId, Now.AddHours(-1), Now.AddHours(1), FlashSaleStatus.Active);

            spec.Criteria.Compile().Invoke(sale).Should().BeFalse(
                "a sale for a different product must not satisfy the product-scoped specification");
        }

        /// <summary>The product-scoped constructor rejects a sale outside the window though product and status match.</summary>
        [Fact]
        public void ProductAndNowConstructor_RejectsSaleOutsideWindow()
        {
            var spec = new ActiveFlashSalesSpecification(MatchingProductId, Now);
            var sale = MakeFlashSale(MatchingProductId, Now.AddHours(1), Now.AddHours(2), FlashSaleStatus.Active);

            spec.Criteria.Compile().Invoke(sale).Should().BeFalse(
                "a sale whose window does not contain now must fail even when product and status match");
        }

        /// <summary>The product-scoped constructor rejects a non-Active sale though product and window match.</summary>
        [Fact]
        public void ProductAndNowConstructor_RejectsNonActiveSale()
        {
            var spec = new ActiveFlashSalesSpecification(MatchingProductId, Now);
            var sale = MakeFlashSale(MatchingProductId, Now.AddHours(-1), Now.AddHours(1), FlashSaleStatus.Scheduled);

            spec.Criteria.Compile().Invoke(sale).Should().BeFalse(
                "only Active sales are selected even when product and window match");
        }

        /// <summary>The product-scoped constructor produces a criteria-only specification.</summary>
        [Fact]
        public void ProductAndNowConstructor_HasNoIncludesOrderingOrPaging()
        {
            var spec = new ActiveFlashSalesSpecification(MatchingProductId, Now);

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
