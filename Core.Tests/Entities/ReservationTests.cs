using System;
using System.Reflection;
using System.Runtime.Serialization;
using Core.Entities;
using FluentAssertions;
using Xunit;

namespace Core.Tests.Entities
{
    /// <summary>
    /// Pure xUnit unit tests (no mocking, no I/O, no async) for the
    /// <see cref="Reservation"/> Core domain entity and its associated
    /// <see cref="ReservationStatus"/> string-backed enum.
    ///
    /// <see cref="Reservation"/> is a plain data holder that inherits
    /// <see cref="BaseEntity"/> (which supplies the <c>int Id</c> key), so the tests
    /// focus on: property round-trips, <see cref="BaseEntity"/> inheritance, default
    /// constructor state, the nullable <c>FlashSaleId</c> pool binding, enum value
    /// round-tripping, the <c>[EnumMember]</c> serialization display strings, and the
    /// pure property-comparison semantics of expiry. Each test constructs fresh
    /// objects so the class carries no shared mutable state and is parallel-safe.
    /// </summary>
    public class ReservationTests
    {
        /// <summary>
        /// Every public property stores and returns the value assigned via the object
        /// initializer (property setter/getter round-trip).
        /// </summary>
        [Fact]
        public void Properties_WhenSet_RoundTripValues()
        {
            // Arrange
            var createdAt = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
            var expiresAt = createdAt.AddMinutes(10);

            // Act
            var reservation = new Reservation
            {
                Id = 10,
                ProductId = 42,
                BasketId = "basket-1",
                Quantity = 3,
                CreatedAt = createdAt,
                ExpiresAt = expiresAt,
                Status = ReservationStatus.Active,
                FlashSaleId = 7
            };

            // Assert
            reservation.Id.Should().Be(10);
            reservation.ProductId.Should().Be(42);
            reservation.BasketId.Should().Be("basket-1");
            reservation.Quantity.Should().Be(3);
            reservation.CreatedAt.Should().Be(createdAt);
            reservation.ExpiresAt.Should().Be(expiresAt);
            reservation.Status.Should().Be(ReservationStatus.Active);
            reservation.FlashSaleId.Should().Be(7);
        }

        /// <summary>
        /// <see cref="Reservation"/> inherits <see cref="BaseEntity"/>, so instances are
        /// assignable to the base type and share the common <c>int Id</c> contract.
        /// </summary>
        [Fact]
        public void Reservation_ShouldBeAssignableTo_BaseEntity()
        {
            // Arrange & Act
            var reservation = new Reservation();

            // Assert
            reservation.Should().BeAssignableTo<BaseEntity>();
        }

        /// <summary>
        /// The parameterless constructor leaves value types at their zero/default value,
        /// the <c>string</c> BasketId null, the nullable <c>FlashSaleId</c> null, and
        /// <c>Status</c> at the first enum member (<see cref="ReservationStatus.Active"/>,
        /// value 0).
        /// </summary>
        [Fact]
        public void Constructor_Default_HasExpectedDefaults()
        {
            // Arrange & Act
            var reservation = new Reservation();

            // Assert
            reservation.Id.Should().Be(0);
            reservation.ProductId.Should().Be(0);
            reservation.BasketId.Should().BeNull();
            reservation.Quantity.Should().Be(0);
            reservation.CreatedAt.Should().Be(default(DateTimeOffset));
            reservation.ExpiresAt.Should().Be(default(DateTimeOffset));
            reservation.Status.Should().Be(ReservationStatus.Active);
            reservation.FlashSaleId.Should().BeNull();
        }

        /// <summary>
        /// A null <c>FlashSaleId</c> documents that the reservation is bound to the
        /// general product-stock pool (no specific flash sale).
        /// </summary>
        [Fact]
        public void FlashSaleId_WhenNull_RepresentsGeneralPool()
        {
            // Arrange & Act
            var reservation = new Reservation { FlashSaleId = null };

            // Assert
            reservation.FlashSaleId.Should().BeNull();
        }

        /// <summary>
        /// A non-null <c>FlashSaleId</c> round-trips the identity of the specific
        /// flash-sale pool the reservation is bound to.
        /// </summary>
        [Fact]
        public void FlashSaleId_WhenSet_RoundTripsBoundSaleId()
        {
            // Arrange & Act
            var reservation = new Reservation { FlashSaleId = 5 };

            // Assert
            reservation.FlashSaleId.Should().Be(5);
        }

        /// <summary>
        /// The <see cref="ReservationStatus"/> property round-trips each legal value.
        /// </summary>
        [Theory]
        [InlineData(ReservationStatus.Active)]
        [InlineData(ReservationStatus.Committed)]
        [InlineData(ReservationStatus.Expired)]
        [InlineData(ReservationStatus.Cancelled)]
        public void Status_WhenAssigned_RoundTripsEachValue(ReservationStatus status)
        {
            // Arrange & Act
            var reservation = new Reservation { Status = status };

            // Assert
            reservation.Status.Should().Be(status);
        }

        /// <summary>
        /// Each <see cref="ReservationStatus"/> member carries an <c>[EnumMember]</c>
        /// attribute whose <c>Value</c> equals the member name (the string persisted by
        /// the EF Core <c>HasConversion</c> mapping). Verified via reflection.
        /// </summary>
        [Theory]
        [InlineData(ReservationStatus.Active, "Active")]
        [InlineData(ReservationStatus.Committed, "Committed")]
        [InlineData(ReservationStatus.Expired, "Expired")]
        [InlineData(ReservationStatus.Cancelled, "Cancelled")]
        public void ReservationStatus_EnumMemberAttribute_HasExpectedDisplayValue(ReservationStatus status, string expected)
        {
            // Arrange
            var memberInfo = typeof(ReservationStatus).GetMember(status.ToString())[0];

            // Act
            var attribute = memberInfo.GetCustomAttribute<EnumMemberAttribute>();

            // Assert
            attribute.Should().NotBeNull();
            attribute.Value.Should().Be(expected);
        }

        /// <summary>
        /// Expiry is a pure property-comparison concern (the entity has no behavior): an
        /// <c>Active</c> reservation whose <c>ExpiresAt</c> is in the past is a candidate
        /// to be reclaimed (set to <see cref="ReservationStatus.Expired"/>) by the
        /// reconciliation service.
        /// </summary>
        [Fact]
        public void ExpiresAt_WhenInThePast_IsExpiryCandidate()
        {
            // Arrange
            var now = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
            var reservation = new Reservation
            {
                Status = ReservationStatus.Active,
                ExpiresAt = now.AddMinutes(-1)
            };

            // Act & Assert
            (reservation.Status == ReservationStatus.Active && reservation.ExpiresAt < now).Should().BeTrue();
        }

        /// <summary>
        /// Companion case: an <c>Active</c> reservation whose <c>ExpiresAt</c> is in the
        /// future is NOT an expiry candidate.
        /// </summary>
        [Fact]
        public void ExpiresAt_WhenInTheFuture_IsNotExpiryCandidate()
        {
            // Arrange
            var now = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
            var reservation = new Reservation
            {
                Status = ReservationStatus.Active,
                ExpiresAt = now.AddMinutes(1)
            };

            // Act & Assert
            (reservation.Status == ReservationStatus.Active && reservation.ExpiresAt < now).Should().BeFalse();
        }
    }
}
