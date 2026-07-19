using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Core.Entities.OrderAggregate;
using FluentAssertions;
using Xunit;

namespace Core.Tests.Entities
{
    /// <summary>
    /// Pure xUnit unit tests (no mocking) for the Order aggregate:
    /// <see cref="Order"/>, <see cref="OrderItem"/>, <see cref="ProductItemOrdered"/>,
    /// <see cref="DeliveryMethod"/> and the <see cref="OrderStatus"/> enum.
    ///
    /// The centerpiece is the financial/critical-path <see cref="Order.GetTotal"/>
    /// calculation (subtotal + delivery price) and the reflection-based verification
    /// of the <see cref="OrderStatus"/> <c>[EnumMember]</c> display strings.
    ///
    /// All subjects live in namespace <c>Core.Entities.OrderAggregate</c>. Entities are
    /// instantiated directly (no test doubles); every test arranges fresh objects so the
    /// class is free of shared mutable state and safe to run in parallel.
    /// </summary>
    public class OrderAggregateTests
    {
        /// <summary>
        /// GetTotal is the order's authoritative money computation: it returns
        /// <c>Subtotal + DeliveryMethod.Price</c>. Parameters are declared as
        /// <see cref="double"/> because <see cref="decimal"/> is not a valid C#
        /// attribute-argument constant type; each value has &lt;= 15 significant digits
        /// so the <c>(decimal)double</c> conversion is exact and decimal addition is
        /// exact, making both sides of the assertion match precisely. The boundary
        /// row (0, 0, 0) guards the zero case. DeliveryMethod is always non-null here
        /// because GetTotal dereferences <c>DeliveryMethod.Price</c> by design.
        /// </summary>
        [Theory]
        [InlineData(100.0, 5.0, 105.0)]
        [InlineData(0.0, 0.0, 0.0)]
        [InlineData(52.98, 10.0, 62.98)]
        [InlineData(19.99, 4.99, 24.98)]
        public void GetTotal_WithSubtotalAndDeliveryPrice_ReturnsSum(double subtotal, double deliveryPrice, double expectedTotal)
        {
            // Arrange
            var deliveryMethod = new DeliveryMethod { Price = (decimal)deliveryPrice };
            var order = new Order { Subtotal = (decimal)subtotal, DeliveryMethod = deliveryMethod };

            // Act
            var total = order.GetTotal();

            // Assert
            total.Should().Be((decimal)expectedTotal);
        }

        /// <summary>
        /// The six-argument constructor must map every argument onto the matching
        /// property. Reference-typed arguments (OrderItems, ShipToAddress,
        /// DeliveryMethod) are asserted with <c>BeSameAs</c> to prove the exact
        /// instances are stored rather than copies.
        /// </summary>
        [Fact]
        public void Constructor_WithArguments_SetsAllProperties()
        {
            // Arrange
            var items = new List<OrderItem>
            {
                new OrderItem(new ProductItemOrdered(1, "Board", "https://test.com/b.png"), 10.00m, 2)
            };
            var address = new Address("John", "Doe", "10 Main St", "New York", "NY", "10001");
            var deliveryMethod = new DeliveryMethod { ShortName = "UPS1", DeliveryTime = "1-2 Days", Description = "Fastest", Price = 10.00m };

            // Act
            var order = new Order(items, "buyer@test.com", address, deliveryMethod, 20.00m, "pi_123");

            // Assert
            order.OrderItems.Should().BeSameAs(items);
            order.OrderItems.Should().HaveCount(1);
            order.BuyerEmail.Should().Be("buyer@test.com");
            order.ShipToAddress.Should().BeSameAs(address);
            order.DeliveryMethod.Should().BeSameAs(deliveryMethod);
            order.Subtotal.Should().Be(20.00m);
            order.PaymentId.Should().Be("pi_123");
        }

        /// <summary>
        /// A freshly constructed order defaults its <see cref="OrderStatus"/> to
        /// <see cref="OrderStatus.Pending"/> (property initializer on the entity).
        /// </summary>
        [Fact]
        public void Constructor_Default_StatusIsPending()
        {
            // Arrange & Act
            var order = new Order();

            // Assert
            order.Status.Should().Be(OrderStatus.Pending);
        }

        /// <summary>
        /// A freshly constructed order stamps <c>OrderDate</c> with
        /// <c>DateTimeOffset.Now</c>. A one-minute tolerance absorbs scheduling
        /// jitter. FluentAssertions 6.x requires the <see cref="TimeSpan"/>-precision
        /// overload of <c>BeCloseTo</c> (the int-milliseconds overload was removed).
        /// </summary>
        [Fact]
        public void Constructor_Default_OrderDateIsCloseToNow()
        {
            // Arrange & Act
            var order = new Order();

            // Assert
            order.OrderDate.Should().BeCloseTo(DateTimeOffset.Now, TimeSpan.FromMinutes(1));
        }

        /// <summary>
        /// The <see cref="OrderItem"/> parameterized constructor must assign the
        /// ordered-product snapshot, unit price and quantity.
        /// </summary>
        [Fact]
        public void OrderItem_Constructor_SetsProperties()
        {
            // Arrange
            var itemOrdered = new ProductItemOrdered(7, "Test Product", "https://test.com/pic.png");

            // Act
            var orderItem = new OrderItem(itemOrdered, 15.50m, 3);

            // Assert
            orderItem.ItemOrdered.Should().BeSameAs(itemOrdered);
            orderItem.Price.Should().Be(15.50m);
            orderItem.Quantity.Should().Be(3);
        }

        /// <summary>
        /// The parameterless <see cref="OrderItem"/> constructor (used by EF Core
        /// materialization) leaves the reference navigation null and numeric
        /// properties at their type defaults.
        /// </summary>
        [Fact]
        public void OrderItem_DefaultConstructor_LeavesItemOrderedNull()
        {
            // Arrange & Act
            var orderItem = new OrderItem();

            // Assert
            orderItem.ItemOrdered.Should().BeNull();
            orderItem.Price.Should().Be(0m);
            orderItem.Quantity.Should().Be(0);
        }

        /// <summary>
        /// The <see cref="ProductItemOrdered"/> constructor captures the product-id,
        /// name and picture-url snapshot taken at order time.
        /// </summary>
        [Fact]
        public void ProductItemOrdered_Constructor_SetsProperties()
        {
            // Arrange & Act
            var itemOrdered = new ProductItemOrdered(42, "Angular Board", "https://test.com/a.png");

            // Assert
            itemOrdered.ProductItemId.Should().Be(42);
            itemOrdered.ProductName.Should().Be("Angular Board");
            itemOrdered.PictureUrl.Should().Be("https://test.com/a.png");
        }

        /// <summary>
        /// <see cref="DeliveryMethod"/> is a plain entity: every settable property
        /// (including the inherited <c>Id</c> from <c>BaseEntity</c>) round-trips
        /// through its object initializer.
        /// </summary>
        [Fact]
        public void DeliveryMethod_Properties_RoundTrip()
        {
            // Arrange & Act
            var deliveryMethod = new DeliveryMethod
            {
                Id = 1,
                ShortName = "UPS1",
                DeliveryTime = "1-2 Days",
                Description = "Fastest delivery time",
                Price = 10.00m
            };

            // Assert
            deliveryMethod.Id.Should().Be(1);
            deliveryMethod.ShortName.Should().Be("UPS1");
            deliveryMethod.DeliveryTime.Should().Be("1-2 Days");
            deliveryMethod.Description.Should().Be("Fastest delivery time");
            deliveryMethod.Price.Should().Be(10.00m);
        }

        /// <summary>
        /// Each <see cref="OrderStatus"/> member carries an <c>[EnumMember]</c>
        /// attribute whose <c>Value</c> is the human-readable display string used by
        /// serialization. This is verified via reflection: <c>GetMember</c> +
        /// <c>GetCustomAttribute&lt;EnumMemberAttribute&gt;()</c>. The multi-word
        /// values ("Payment Received"/"Payment Failed") differ from the identifier
        /// names, making this assertion meaningful.
        /// </summary>
        [Theory]
        [InlineData(OrderStatus.Pending, "Pending")]
        [InlineData(OrderStatus.PaymentReceived, "Payment Received")]
        [InlineData(OrderStatus.PaymentFailed, "Payment Failed")]
        public void OrderStatus_EnumMemberAttribute_HasExpectedDisplayValue(OrderStatus status, string expected)
        {
            // Arrange
            var memberInfo = typeof(OrderStatus).GetMember(status.ToString())[0];

            // Act
            var attribute = memberInfo.GetCustomAttribute<EnumMemberAttribute>();

            // Assert
            attribute.Should().NotBeNull();
            attribute.Value.Should().Be(expected);
        }
    }
}
