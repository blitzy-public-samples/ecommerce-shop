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
    /// <see cref="FlashSale"/> Core domain entity and its associated
    /// <see cref="FlashSaleStatus"/> string-backed enum.
    ///
    /// <see cref="FlashSale"/> is a plain data holder that inherits
    /// <see cref="BaseEntity"/> (which supplies the <c>int Id</c> key), so the tests
    /// focus on: property round-trips, <see cref="BaseEntity"/> inheritance, default
    /// constructor state, enum value round-tripping, the <c>[EnumMember]</c>
    /// serialization display strings, and the pure property-comparison semantics of
    /// the active sale window (<c>StartsAt</c> inclusive, <c>EndsAt</c> exclusive).
    /// Each test constructs fresh objects so the class carries no shared mutable state
    /// and is parallel-safe.
    /// </summary>
    public class FlashSaleTests
    {
        /// <summary>
        /// Every public property stores and returns the value assigned via the object
        /// initializer (property setter/getter round-trip).
        /// </summary>
        [Fact]
        public void Properties_WhenSet_RoundTripValues()
        {
            // Arrange
            var startsAt = new DateTimeOffset(2024, 6, 1, 9, 0, 0, TimeSpan.Zero);
            var endsAt = new DateTimeOffset(2024, 6, 1, 17, 0, 0, TimeSpan.Zero);

            // Act
            var flashSale = new FlashSale
            {
                Id = 3,
                ProductId = 42,
                SaleStockQuantity = 100,
                StartsAt = startsAt,
                EndsAt = endsAt,
                Status = FlashSaleStatus.Active
            };

            // Assert
            flashSale.Id.Should().Be(3);
            flashSale.ProductId.Should().Be(42);
            flashSale.SaleStockQuantity.Should().Be(100);
            flashSale.StartsAt.Should().Be(startsAt);
            flashSale.EndsAt.Should().Be(endsAt);
            flashSale.Status.Should().Be(FlashSaleStatus.Active);
        }

        /// <summary>
        /// <see cref="FlashSale"/> inherits <see cref="BaseEntity"/>, so instances are
        /// assignable to the base type and share the common <c>int Id</c> contract.
        /// </summary>
        [Fact]
        public void FlashSale_ShouldBeAssignableTo_BaseEntity()
        {
            // Arrange & Act
            var flashSale = new FlashSale();

            // Assert
            flashSale.Should().BeAssignableTo<BaseEntity>();
        }

        /// <summary>
        /// The parameterless constructor leaves value types at their zero/default value
        /// and <c>Status</c> at the first enum member
        /// (<see cref="FlashSaleStatus.Scheduled"/>, value 0).
        /// </summary>
        [Fact]
        public void Constructor_Default_HasExpectedDefaults()
        {
            // Arrange & Act
            var flashSale = new FlashSale();

            // Assert
            flashSale.Id.Should().Be(0);
            flashSale.ProductId.Should().Be(0);
            flashSale.SaleStockQuantity.Should().Be(0);
            flashSale.StartsAt.Should().Be(default(DateTimeOffset));
            flashSale.EndsAt.Should().Be(default(DateTimeOffset));
            flashSale.Status.Should().Be(FlashSaleStatus.Scheduled);
        }

        /// <summary>
        /// The <see cref="FlashSaleStatus"/> property round-trips each legal value.
        /// </summary>
        [Theory]
        [InlineData(FlashSaleStatus.Scheduled)]
        [InlineData(FlashSaleStatus.Active)]
        [InlineData(FlashSaleStatus.Ended)]
        public void Status_WhenAssigned_RoundTripsEachValue(FlashSaleStatus status)
        {
            // Arrange & Act
            var flashSale = new FlashSale { Status = status };

            // Assert
            flashSale.Status.Should().Be(status);
        }

        /// <summary>
        /// Each <see cref="FlashSaleStatus"/> member carries an <c>[EnumMember]</c>
        /// attribute whose <c>Value</c> equals the member name (the string persisted by
        /// the EF Core <c>HasConversion</c> mapping). Verified via reflection.
        /// </summary>
        [Theory]
        [InlineData(FlashSaleStatus.Scheduled, "Scheduled")]
        [InlineData(FlashSaleStatus.Active, "Active")]
        [InlineData(FlashSaleStatus.Ended, "Ended")]
        public void FlashSaleStatus_EnumMemberAttribute_HasExpectedDisplayValue(FlashSaleStatus status, string expected)
        {
            // Arrange
            var memberInfo = typeof(FlashSaleStatus).GetMember(status.ToString())[0];

            // Act
            var attribute = memberInfo.GetCustomAttribute<EnumMemberAttribute>();

            // Assert
            attribute.Should().NotBeNull();
            attribute.Value.Should().Be(expected);
        }

        /// <summary>
        /// The active window is <c>StartsAt &lt;= now &lt; EndsAt</c>. With <c>now</c> one
        /// hour after start and one hour before end, the window contains <c>now</c>.
        /// </summary>
        [Fact]
        public void ActiveWindow_WhenNowWithinStartInclusiveEndExclusive_ContainsNow()
        {
            // Arrange
            var now = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
            var sale = new FlashSale
            {
                StartsAt = now.AddHours(-1),
                EndsAt = now.AddHours(1)
            };

            // Act & Assert
            (sale.StartsAt <= now && now < sale.EndsAt).Should().BeTrue();
        }

        /// <summary>
        /// The window start is inclusive: when <c>now</c> equals <c>StartsAt</c>, the
        /// window contains <c>now</c>.
        /// </summary>
        [Fact]
        public void ActiveWindow_WhenNowEqualsStartsAt_ContainsNow()
        {
            // Arrange
            var now = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
            var sale = new FlashSale
            {
                StartsAt = now,
                EndsAt = now.AddHours(1)
            };

            // Act & Assert
            (sale.StartsAt <= now && now < sale.EndsAt).Should().BeTrue();
        }

        /// <summary>
        /// The window end is exclusive: when <c>now</c> equals <c>EndsAt</c>, the window
        /// does NOT contain <c>now</c>.
        /// </summary>
        [Fact]
        public void ActiveWindow_WhenNowEqualsEndsAt_DoesNotContainNow()
        {
            // Arrange
            var now = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
            var sale = new FlashSale
            {
                StartsAt = now.AddHours(-1),
                EndsAt = now
            };

            // Act & Assert
            (sale.StartsAt <= now && now < sale.EndsAt).Should().BeFalse();
        }

        /// <summary>
        /// A sale that has not started (now before <c>StartsAt</c>) does not contain
        /// <c>now</c>.
        /// </summary>
        [Fact]
        public void ActiveWindow_WhenNowBeforeStartsAt_DoesNotContainNow()
        {
            // Arrange
            var now = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
            var sale = new FlashSale
            {
                StartsAt = now.AddHours(1),
                EndsAt = now.AddHours(2)
            };

            // Act & Assert
            (sale.StartsAt <= now && now < sale.EndsAt).Should().BeFalse();
        }

        /// <summary>
        /// A sale that has already ended (now at or after <c>EndsAt</c>) does not contain
        /// <c>now</c>.
        /// </summary>
        [Fact]
        public void ActiveWindow_WhenNowAtOrAfterEndsAt_DoesNotContainNow()
        {
            // Arrange
            var now = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
            var sale = new FlashSale
            {
                StartsAt = now.AddHours(-2),
                EndsAt = now.AddHours(-1)
            };

            // Act & Assert
            (sale.StartsAt <= now && now < sale.EndsAt).Should().BeFalse();
        }
    }
}
