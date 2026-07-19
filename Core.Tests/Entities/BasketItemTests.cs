using Core.Entities;
using FluentAssertions;
using Xunit;

namespace Core.Tests.Entities
{
    /// <summary>
    /// Pure xUnit unit tests for the <see cref="BasketItem"/> Core domain entity.
    /// <para>
    /// <see cref="BasketItem"/> is a plain data-carrier class (it does not derive from
    /// <c>BaseEntity</c>) exposing seven public auto-properties and no behaviour, so the
    /// tests focus on property round-tripping and constructor defaults. No mocking is
    /// required because the entity has no collaborators; each test constructs a fresh
    /// instance to guarantee isolation and parallel-safety.
    /// </para>
    /// <para>
    /// Test method names follow the repository convention
    /// <c>MethodName_StateUnderTest_ExpectedBehavior</c> (adapted to a property/constructor
    /// vocabulary since the subject has no methods), and assertions use FluentAssertions.
    /// </para>
    /// </summary>
    public class BasketItemTests
    {
        /// <summary>
        /// Verifies that every public property stores and returns the value assigned to it
        /// via the object initializer (property setter/getter round-trip).
        /// </summary>
        [Fact]
        public void Properties_WhenSet_RoundTripValues()
        {
            // Arrange & Act
            var item = new BasketItem
            {
                Id = 5,
                ProductName = "Angular Speedster Board 2000",
                Price = 200.00m,
                Quantity = 3,
                PictureUrl = "https://test.com/images/board.png",
                Brand = "Angular",
                Type = "Boards"
            };

            // Assert
            item.Id.Should().Be(5);
            item.ProductName.Should().Be("Angular Speedster Board 2000");
            item.Price.Should().Be(200.00m);
            item.Quantity.Should().Be(3);
            item.PictureUrl.Should().Be("https://test.com/images/board.png");
            item.Brand.Should().Be("Angular");
            item.Type.Should().Be("Boards");
        }

        /// <summary>
        /// Verifies the default (parameterless) construction leaves value-type properties at
        /// their zero value and reference-type (string) properties null, confirming there is
        /// no hidden field initialization in the entity.
        /// </summary>
        [Fact]
        public void Constructor_Default_LeavesReferenceTypesNullAndValueTypesZero()
        {
            // Arrange & Act
            var item = new BasketItem();

            // Assert
            item.Id.Should().Be(0);
            item.Price.Should().Be(0m);
            item.Quantity.Should().Be(0);
            item.ProductName.Should().BeNull();
            item.PictureUrl.Should().BeNull();
            item.Brand.Should().BeNull();
            item.Type.Should().BeNull();
        }

        /// <summary>
        /// Verifies the integer <see cref="BasketItem.Quantity"/> property round-trips across a
        /// range of representative values (boundary zero, single unit, and a larger quantity).
        /// </summary>
        /// <param name="quantity">The quantity to assign and read back.</param>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(100)]
        public void Quantity_WhenSet_ReturnsSameValue(int quantity)
        {
            // Arrange & Act
            var item = new BasketItem { Quantity = quantity };

            // Assert
            item.Quantity.Should().Be(quantity);
        }
    }
}
