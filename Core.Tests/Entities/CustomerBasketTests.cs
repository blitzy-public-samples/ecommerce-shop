using Core.Entities;
using FluentAssertions;
using Xunit;

namespace Core.Tests.Entities
{
    /// <summary>
    /// Pure unit tests for <see cref="CustomerBasket"/> (which aggregates
    /// <see cref="BasketItem"/>). These tests instantiate the entity directly
    /// (no mocking) and assert on its constructor defaults and property
    /// round-trip behavior.
    ///
    /// The highest-value assertion in this suite is that a freshly-constructed
    /// basket exposes a non-null, empty <see cref="CustomerBasket.Items"/>
    /// collection. This is a real behavior of the production type — the field
    /// is initialized inline (<c>= new List&lt;BasketItem&gt;()</c>) — rather
    /// than framework magic, and callers such as the basket repository and
    /// order service rely on it to add items without a null guard.
    ///
    /// Conventions: test names follow MethodName_StateUnderTest_ExpectedBehavior
    /// and each test uses the Arrange-Act-Assert structure with FluentAssertions.
    /// Every test constructs its own fresh objects, so there is no shared mutable
    /// state and the class is safe to run in parallel.
    /// </summary>
    public class CustomerBasketTests
    {
        [Fact]
        public void Constructor_Default_InitializesEmptyItems()
        {
            // Arrange & Act
            var basket = new CustomerBasket();

            // Assert
            // Items is initialized inline in the production type, so it must be
            // a real, empty collection (never null) straight after construction.
            basket.Items.Should().NotBeNull();
            basket.Items.Should().BeEmpty();
            // DeliveryMethodId is a nullable int and has no default value assigned.
            basket.DeliveryMethodId.Should().BeNull();
            // ShippingPrice is a decimal, defaulting to 0m.
            basket.ShippingPrice.Should().Be(0m);
        }

        [Fact]
        public void Constructor_WithId_SetsId()
        {
            // Arrange & Act
            var basket = new CustomerBasket("basket-1");

            // Assert
            // The id constructor assigns the supplied value to Id...
            basket.Id.Should().Be("basket-1");
            // ...while still initializing Items to a non-null, empty collection.
            basket.Items.Should().NotBeNull().And.BeEmpty();
        }

        [Fact]
        public void Items_WhenBasketItemsAdded_ReflectsCount()
        {
            // Arrange
            var basket = new CustomerBasket("basket-2");

            // Act
            // Because Items is a mutable, pre-initialized List, items can be
            // added directly without any null-guard or re-assignment.
            basket.Items.Add(new BasketItem { Id = 1, ProductName = "P1", Price = 10m, Quantity = 1 });
            basket.Items.Add(new BasketItem { Id = 2, ProductName = "P2", Price = 20m, Quantity = 2 });

            // Assert
            basket.Items.Should().HaveCount(2);
        }

        [Fact]
        public void PaymentAndDeliveryFields_WhenSet_RoundTrip()
        {
            // Arrange & Act
            // Use an object initializer to exercise the settable payment and
            // delivery properties, then confirm each value round-trips intact.
            var basket = new CustomerBasket("basket-3")
            {
                ClientSecret = "secret_abc",
                PaymentIntentId = "pi_123",
                DeliveryMethodId = 3,
                ShippingPrice = 5.00m
            };

            // Assert
            basket.ClientSecret.Should().Be("secret_abc");
            basket.PaymentIntentId.Should().Be("pi_123");
            // int implicitly converts to the nullable int? backing DeliveryMethodId.
            basket.DeliveryMethodId.Should().Be(3);
            // Exact decimal comparison — no floating-point tolerance involved.
            basket.ShippingPrice.Should().Be(5.00m);
        }
    }
}
