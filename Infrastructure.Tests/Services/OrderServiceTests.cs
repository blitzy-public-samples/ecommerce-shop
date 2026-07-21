using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using API.Specifications;
using Core.Entities;
using Core.Entities.OrderAggregate;
using Core.Interfaces;
using FluentAssertions;
using Infrastructure.Services;
using Moq;
using Xunit;

namespace Infrastructure.Tests.Services
{
    public class OrderServiceTests
    {
        private readonly Mock<IBasketRepository> _basketRepo = new Mock<IBasketRepository>();
        private readonly Mock<IUnitOfWork> _unitOfWork = new Mock<IUnitOfWork>();
        private readonly Mock<IPaymentService> _paymentService = new Mock<IPaymentService>();
        private readonly Mock<IInventoryService> _inventoryService = new Mock<IInventoryService>();
        private readonly Mock<IGenericRepository<Product>> _productRepo = new Mock<IGenericRepository<Product>>();
        private readonly Mock<IGenericRepository<DeliveryMethod>> _deliveryRepo = new Mock<IGenericRepository<DeliveryMethod>>();
        private readonly Mock<IGenericRepository<Order>> _orderRepo = new Mock<IGenericRepository<Order>>();
        private readonly OrderService _sut;

        public OrderServiceTests()
        {
            _unitOfWork.Setup(u => u.Repository<Product>()).Returns(_productRepo.Object);
            _unitOfWork.Setup(u => u.Repository<DeliveryMethod>()).Returns(_deliveryRepo.Object);
            _unitOfWork.Setup(u => u.Repository<Order>()).Returns(_orderRepo.Object);
            _sut = new OrderService(_basketRepo.Object, _unitOfWork.Object, _paymentService.Object, _inventoryService.Object);
        }

        private static Address SampleAddress() =>
            new Address("Bob", "Bobbity", "10 The Street", "New York", "NY", "90210");

        // Client price is deliberately bogus (0.01) to prove the server ignores it.
        private static CustomerBasket BasketWithBogusClientPrice() =>
            new CustomerBasket("basket-1")
            {
                PaymentIntentId = "pi_123",
                DeliveryMethodId = 1,
                Items = new List<BasketItem>
                {
                    new BasketItem { Id = 1, Quantity = 2, Price = 0.01m, ProductName = "Bogus client name" }
                }
            };

        private void ArrangeValidCreateOrderDependencies(
            CustomerBasket basket, decimal productPrice = 100m, decimal shippingPrice = 10m,
            Order existingOrder = null, int completeResult = 1, DeliveryMethod delivery = null)
        {
            _basketRepo.Setup(r => r.GetBasketAsync("basket-1")).ReturnsAsync(basket);
            _productRepo.Setup(r => r.GetByIdAsync(1))
                .ReturnsAsync(new Product { Id = 1, Name = "Real Product", PictureUrl = "real.png", Price = productPrice });
            _deliveryRepo.Setup(r => r.GetByIdAsync(1))
                .ReturnsAsync(delivery ?? new DeliveryMethod { Id = 1, Price = shippingPrice, ShortName = "UPS1" });
            _orderRepo.Setup(r => r.GetEntityWithSpec(It.IsAny<ISpecification<Order>>()))
                .ReturnsAsync(existingOrder);
            _paymentService.Setup(p => p.CreateOrUpdatePaymentIntent(It.IsAny<string>()))
                .ReturnsAsync(basket);
            _unitOfWork.Setup(u => u.Complete()).ReturnsAsync(completeResult);
        }

        [Fact]
        public async Task CreateOrderAsync_WhenBasketValid_UsesServerSidePriceNotClientPrice()
        {
            // Arrange
            var basket = BasketWithBogusClientPrice();
            ArrangeValidCreateOrderDependencies(basket);

            // Act
            var result = await _sut.CreateOrderAsync("bob@test.com", 1, "basket-1", SampleAddress());

            // Assert — server authoritative price (100), NOT client price (0.01)
            result.Should().NotBeNull();
            result.OrderItems.Should().OnlyContain(oi => oi.Price == 100m);
            result.OrderItems.First().Price.Should().Be(100m);
        }

        [Fact]
        public async Task CreateOrderAsync_WhenBasketValid_ComputesSubtotalFromServerPrices()
        {
            // Arrange
            var basket = BasketWithBogusClientPrice();
            ArrangeValidCreateOrderDependencies(basket);

            // Act
            var result = await _sut.CreateOrderAsync("bob@test.com", 1, "basket-1", SampleAddress());

            // Assert — 100 * 2
            result.Subtotal.Should().Be(200m);
        }

        [Fact]
        public async Task CreateOrderAsync_WhenBasketValid_PopulatesOrderFieldsAndAddsOrderAndCompletes()
        {
            // Arrange
            var basket = BasketWithBogusClientPrice();
            var address = SampleAddress();
            var delivery = new DeliveryMethod { Id = 1, Price = 10m, ShortName = "UPS1" };
            ArrangeValidCreateOrderDependencies(basket, delivery: delivery);

            // Act
            var result = await _sut.CreateOrderAsync("bob@test.com", 1, "basket-1", address);

            // Assert
            result.Should().NotBeNull();
            result.BuyerEmail.Should().Be("bob@test.com");
            result.ShipToAddress.Should().BeSameAs(address);
            result.DeliveryMethod.Should().BeSameAs(delivery);
            result.PaymentId.Should().Be("pi_123");
            _orderRepo.Verify(r => r.Add(It.IsAny<Order>()), Times.Once);
            _unitOfWork.Verify(u => u.Complete(), Times.Once);
        }

        // (F4) On a successful flush, the order path must STAGE the reservation commit BEFORE Complete() and
        // FINALIZE (delete) the Redis holds only AFTER Complete() — never before — so a rollback can never orphan
        // the hold key. This asserts the exact call order: commit -> complete -> finalize.
        [Fact]
        public async Task CreateOrderAsync_WhenCompleteSucceeds_StagesCommitBeforeFlushAndFinalizesHoldsAfterFlush()
        {
            // Arrange
            var basket = BasketWithBogusClientPrice();
            ArrangeValidCreateOrderDependencies(basket);
            var calls = new List<string>();
            _inventoryService.Setup(i => i.CommitReservationAsync("basket-1"))
                .Callback(() => calls.Add("commit")).Returns(Task.CompletedTask);
            _unitOfWork.Setup(u => u.Complete())
                .Callback(() => calls.Add("complete")).ReturnsAsync(1);
            _inventoryService.Setup(i => i.FinalizeCommittedHoldsAsync("basket-1"))
                .Callback(() => calls.Add("finalize")).Returns(Task.CompletedTask);

            // Act
            var result = await _sut.CreateOrderAsync("bob@test.com", 1, "basket-1", SampleAddress());

            // Assert — order created, and the invocation order is exactly commit -> complete -> finalize.
            result.Should().NotBeNull();
            _inventoryService.Verify(i => i.CommitReservationAsync("basket-1"), Times.Once);
            _unitOfWork.Verify(u => u.Complete(), Times.Once);
            _inventoryService.Verify(i => i.FinalizeCommittedHoldsAsync("basket-1"), Times.Once);
            calls.Should().Equal("commit", "complete", "finalize");
        }

        // (F4) When the flush fails (Complete() <= 0 => rollback), the holds must NOT be finalized/deleted, so the
        // rolled-back (still-Active) reservation stays consistent with its surviving Redis hold key. Commit staging
        // still occurs (it is purely in-memory on the shared context and reverts with the rollback).
        [Fact]
        public async Task CreateOrderAsync_WhenCompleteReturnsZero_StagesCommitButDoesNotFinalizeHolds()
        {
            // Arrange
            var basket = BasketWithBogusClientPrice();
            ArrangeValidCreateOrderDependencies(basket, completeResult: 0);

            // Act
            var result = await _sut.CreateOrderAsync("bob@test.com", 1, "basket-1", SampleAddress());

            // Assert — flush failed => null; commit was staged and flush attempted, but hold finalization never ran.
            result.Should().BeNull();
            _inventoryService.Verify(i => i.CommitReservationAsync("basket-1"), Times.Once);
            _unitOfWork.Verify(u => u.Complete(), Times.Once);
            _inventoryService.Verify(i => i.FinalizeCommittedHoldsAsync(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task CreateOrderAsync_WhenExistingOrderWithSamePaymentIntent_DeletesItAndUpdatesPaymentIntent()
        {
            // Arrange — stale order returned by spec lookup
            var basket = BasketWithBogusClientPrice();
            var existingOrder = new Order();
            ArrangeValidCreateOrderDependencies(basket, existingOrder: existingOrder);

            // Act
            var result = await _sut.CreateOrderAsync("bob@test.com", 1, "basket-1", SampleAddress());

            // Assert — single-order guarantee: delete stale then re-sync payment intent
            _orderRepo.Verify(r => r.Delete(existingOrder), Times.Once);
            _paymentService.Verify(p => p.CreateOrUpdatePaymentIntent("pi_123"), Times.Once);
            result.Should().NotBeNull();
        }

        [Fact]
        public async Task CreateOrderAsync_WhenBasketValid_InvokesCommitReservationBetweenAddingOrderAndCompleting()
        {
            // Arrange
            var basket = BasketWithBogusClientPrice();
            ArrangeValidCreateOrderDependencies(basket);

            // Record the relative invocation order of the three finalization steps that OrderService
            // orchestrates, so we can assert the sequencing this unit test is actually able to prove.
            var callOrder = new List<string>();
            _orderRepo.Setup(r => r.Add(It.IsAny<Order>()))
                .Callback(() => callOrder.Add("Add"));
            _inventoryService.Setup(i => i.CommitReservationAsync("basket-1"))
                .Callback(() => callOrder.Add("CommitReservationAsync"))
                .Returns(Task.CompletedTask);
            _unitOfWork.Setup(u => u.Complete())
                .Callback(() => callOrder.Add("Complete"))
                .ReturnsAsync(1);

            // Act
            await _sut.CreateOrderAsync("bob@test.com", 1, "basket-1", SampleAddress());

            // Assert — this is a UNIT-LEVEL orchestration/sequencing guarantee proven with mocks:
            // OrderService stages the basket's reservation commit exactly once, AFTER Add(order) and
            // BEFORE the single Complete(), so the commit is staged for the same SaveChanges as the order.
            //
            // This test deliberately does NOT — and with mocks CANNOT — prove a real database
            // transaction, a Products "SELECT ... FOR UPDATE" row lock, rollback on failure, or the
            // ordering of Redis hold-key cleanup relative to durable commit. Those durability/atomicity
            // guarantees belong to the PostgreSQL-backed integration tests (API.IntegrationTests), not
            // to this in-memory unit test.
            _inventoryService.Verify(i => i.CommitReservationAsync("basket-1"), Times.Once);
            callOrder.Should().Equal(new[] { "Add", "CommitReservationAsync", "Complete" });
        }

        [Fact]
        public async Task CreateOrderAsync_WhenCompleteReturnsZero_ReturnsNull()
        {
            // Arrange
            var basket = BasketWithBogusClientPrice();
            ArrangeValidCreateOrderDependencies(basket, completeResult: 0);

            // Act
            var result = await _sut.CreateOrderAsync("bob@test.com", 1, "basket-1", SampleAddress());

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task CreateOrderAsync_WhenBasketNotFound_ThrowsNullReferenceException()
        {
            // Arrange — OrderService has NO null-basket guard; asserting ACTUAL behavior.
            // Production must NOT be changed to "return null" (AAP 0.8/0.10). This differs from PaymentService.
            _basketRepo.Setup(r => r.GetBasketAsync(It.IsAny<string>())).ReturnsAsync((CustomerBasket)null);

            // Act
            Func<Task> act = async () => await _sut.CreateOrderAsync("bob@test.com", 1, "missing", SampleAddress());

            // Assert
            await act.Should().ThrowAsync<NullReferenceException>();
        }

        [Fact]
        public async Task GetOrdersForUserAsync_WhenCalled_DelegatesToOrderRepositoryListAsync()
        {
            // Arrange
            var orders = new List<Order> { new Order(), new Order() };
            _orderRepo.Setup(r => r.ListAsync(It.IsAny<ISpecification<Order>>())).ReturnsAsync(orders);

            // Act
            var result = await _sut.GetOrdersForUserAsync("bob@test.com");

            // Assert
            result.Should().BeSameAs(orders);
            _orderRepo.Verify(r => r.ListAsync(It.IsAny<ISpecification<Order>>()), Times.Once);
        }

        [Fact]
        public async Task GetOrderByIdAsync_WhenCalled_DelegatesToOrderRepositoryGetEntityWithSpec()
        {
            // Arrange
            var order = new Order();
            _orderRepo.Setup(r => r.GetEntityWithSpec(It.IsAny<ISpecification<Order>>())).ReturnsAsync(order);

            // Act
            var result = await _sut.GetOrderByIdAsync(1, "bob@test.com");

            // Assert
            result.Should().BeSameAs(order);
            _orderRepo.Verify(r => r.GetEntityWithSpec(It.IsAny<ISpecification<Order>>()), Times.Once);
        }

        [Fact]
        public async Task GetDeliveryMethodsAsync_WhenCalled_DelegatesToDeliveryRepositoryListAllAsync()
        {
            // Arrange
            var methods = new List<DeliveryMethod> { new DeliveryMethod { Id = 1 } };
            _deliveryRepo.Setup(r => r.ListAllAsync()).ReturnsAsync(methods);

            // Act
            var result = await _sut.GetDeliveryMethodsAsync();

            // Assert
            result.Should().BeSameAs(methods);
            _deliveryRepo.Verify(r => r.ListAllAsync(), Times.Once);
        }
    }
}
