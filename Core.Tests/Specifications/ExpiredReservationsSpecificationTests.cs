using System;
using System.Linq.Expressions;
// Production namespace quirk (verified in source): ExpiredReservationsSpecification physically
// lives at Core/Specifications/ExpiredReservationsSpecification.cs but declares
// `namespace API.Specifications` (NOT Core.Specifications). Import API.Specifications to resolve it.
using API.Specifications;
using Core.Entities;
using FluentAssertions;
using Xunit;

namespace Core.Tests.Specifications
{
    /// <summary>
    /// Unit tests for <see cref="ExpiredReservationsSpecification"/>.
    /// Verifies the criteria selects Active reservations whose <c>ExpiresAt</c> is strictly before
    /// the supplied reference instant, and asserts the criteria-only structural invariants.
    /// Only the deterministic <c>(DateTimeOffset now)</c> constructor is exercised.
    /// </summary>
    public class ExpiredReservationsSpecificationTests
    {
        private static readonly DateTimeOffset Now = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);

        private static Reservation MakeReservation(DateTimeOffset expiresAt, ReservationStatus status) =>
            new Reservation { ExpiresAt = expiresAt, Status = status };

        /// <summary>The constructor produces a non-null criteria expression.</summary>
        [Fact]
        public void Constructor_SetsNonNullCriteria()
        {
            var spec = new ExpiredReservationsSpecification(Now);

            spec.Criteria.Should().NotBeNull("the specification is defined solely by its criteria");
        }

        /// <summary>An Active reservation that expired before now is matched.</summary>
        [Fact]
        public void Criteria_MatchesActiveReservationExpiredBeforeNow()
        {
            // Arrange
            var spec = new ExpiredReservationsSpecification(Now);
            var reservation = MakeReservation(Now.AddMinutes(-1), ReservationStatus.Active);

            // Act
            Expression<Func<Reservation, bool>> criteria = spec.Criteria;
            var predicate = criteria.Compile();

            // Assert
            predicate.Invoke(reservation).Should().BeTrue(
                "an Active reservation whose ExpiresAt is before now is expired and must be reclaimed");
        }

        /// <summary>A reservation expiring exactly at now is NOT matched (proves the strict &lt; boundary).</summary>
        [Fact]
        public void Criteria_RejectsReservationExpiringExactlyAtNow()
        {
            var spec = new ExpiredReservationsSpecification(Now);
            var reservation = MakeReservation(Now, ReservationStatus.Active);

            spec.Criteria.Compile().Invoke(reservation).Should().BeFalse(
                "expiry uses a strict less-than: a reservation whose ExpiresAt equals now is not yet expired");
        }

        /// <summary>A reservation expiring after now is NOT matched.</summary>
        [Fact]
        public void Criteria_RejectsReservationExpiringAfterNow()
        {
            var spec = new ExpiredReservationsSpecification(Now);
            var reservation = MakeReservation(Now.AddMinutes(1), ReservationStatus.Active);

            spec.Criteria.Compile().Invoke(reservation).Should().BeFalse(
                "a reservation whose ExpiresAt is in the future relative to now is still valid");
        }

        /// <summary>Non-Active reservations are never matched even if already expired (proves the Status == Active conjunct).</summary>
        [Theory]
        [InlineData(ReservationStatus.Committed)]
        [InlineData(ReservationStatus.Expired)]
        [InlineData(ReservationStatus.Cancelled)]
        public void Criteria_RejectsNonActiveReservationEvenIfExpired(ReservationStatus status)
        {
            var spec = new ExpiredReservationsSpecification(Now);
            var reservation = MakeReservation(Now.AddMinutes(-1), status);

            spec.Criteria.Compile().Invoke(reservation).Should().BeFalse(
                "only Active reservations are reclaimable; other statuses are excluded regardless of expiry");
        }

        /// <summary>The specification is criteria-only (no includes/ordering/paging).</summary>
        [Fact]
        public void Constructor_HasNoIncludesOrderingOrPaging()
        {
            var spec = new ExpiredReservationsSpecification(Now);

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
