using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using API.Specifications;
using Core.Interfaces;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Moq;
using Stripe;
using Xunit;
// Alias Core domain types to avoid ambiguity with Stripe.Order / Stripe.Product etc.
using Order = Core.Entities.OrderAggregate.Order;
using OrderStatus = Core.Entities.OrderAggregate.OrderStatus;
using DeliveryMethod = Core.Entities.OrderAggregate.DeliveryMethod;
using Product = Core.Entities.Product;
using CustomerBasket = Core.Entities.CustomerBasket;
using BasketItem = Core.Entities.BasketItem;

namespace Infrastructure.Tests.Services
{
    /// <summary>
    /// Unit tests for <see cref="PaymentService"/> — the critical financial path (AAP 0.7.1, target ≥90%
    /// line coverage). Every test follows the MethodName_StateUnderTest_ExpectedBehavior convention and the
    /// Arrange-Act-Assert structure, uses FluentAssertions for expressive assertions, and creates a fresh set
    /// of Moq doubles per test via the xUnit per-test constructor (no static/shared mutable state).
    ///
    /// NO live Stripe network calls are made anywhere (AAP 0.10.1). The Stripe SDK is exercised entirely
    /// offline by intercepting the virtual <see cref="PaymentIntentService"/> methods through a Moq mock,
    /// which is injected into the production code via the documented, annotated testability seam
    /// (<c>protected virtual PaymentIntentService CreatePaymentIntentService()</c>) using a test-only subclass.
    /// The test project does NOT modify production code.
    /// </summary>
    public class PaymentServiceTests
    {
        // Fresh doubles per test (xUnit instantiates the test class once per [Fact]).
        private readonly Mock<IBasketRepository> _basketRepo = new Mock<IBasketRepository>();
        private readonly Mock<IUnitOfWork> _unitOfWork = new Mock<IUnitOfWork>();
        private readonly Mock<IConfiguration> _config = new Mock<IConfiguration>();
        private readonly Mock<IGenericRepository<Product>> _productRepo = new Mock<IGenericRepository<Product>>();
        private readonly Mock<IGenericRepository<DeliveryMethod>> _deliveryRepo = new Mock<IGenericRepository<DeliveryMethod>>();
        private readonly Mock<IGenericRepository<Order>> _orderRepo = new Mock<IGenericRepository<Order>>();

        // Offline Stripe double: PaymentIntentService.CreateAsync/UpdateAsync are public virtual, so Moq
        // subclasses the concrete type and intercepts the calls — no HTTP request ever leaves the process.
        private readonly Mock<PaymentIntentService> _stripe = new Mock<PaymentIntentService>();

        public PaymentServiceTests()
        {
            // A Stripe *test* key is supplied only through the mocked configuration; it is assigned to
            // StripeConfiguration.ApiKey by the SUT but never used because the virtual calls are intercepted.
            _config.Setup(c => c["StripeSettings:SecretKey"]).Returns("sk_test_123");

            // Route each generic repository request through the appropriate typed mock.
            _unitOfWork.Setup(u => u.Repository<Product>()).Returns(_productRepo.Object);
            _unitOfWork.Setup(u => u.Repository<DeliveryMethod>()).Returns(_deliveryRepo.Object);
            _unitOfWork.Setup(u => u.Repository<Order>()).Returns(_orderRepo.Object);

            // UpdateBasketAsync echoes the persisted basket back (the SUT ignores the return but this keeps
            // the mock faithful to the IBasketRepository contract).
            _basketRepo.Setup(r => r.UpdateBasketAsync(It.IsAny<CustomerBasket>()))
                .ReturnsAsync((CustomerBasket b) => b);
        }

        // Test-only subclass consuming the production Stripe testability seam
        // (protected virtual PaymentIntentService CreatePaymentIntentService()).
        private class TestablePaymentService : PaymentService
        {
            private readonly PaymentIntentService _stub;

            public TestablePaymentService(IBasketRepository basketRepository, IUnitOfWork unitOfWork,
                IConfiguration config, PaymentIntentService stub)
                : base(basketRepository, unitOfWork, config) => _stub = stub;

            protected override PaymentIntentService CreatePaymentIntentService() => _stub;
        }

        // Probe subclass that exposes the *base* (production default) implementation of the seam so we can
        // assert the documented default behavior — "still returns new PaymentIntentService()" — without
        // making any Stripe network call (construction alone is offline; only request methods hit the API).
        private sealed class BaseFactoryProbe : PaymentService
        {
            public BaseFactoryProbe(IBasketRepository basketRepository, IUnitOfWork unitOfWork, IConfiguration config)
                : base(basketRepository, unitOfWork, config) { }

            public PaymentIntentService InvokeBaseFactory() => base.CreatePaymentIntentService();
        }

        private PaymentService CreateSut() =>
            new TestablePaymentService(_basketRepo.Object, _unitOfWork.Object, _config.Object, _stripe.Object);

        private void SetupCreateReturns(PaymentIntent intent) =>
            _stripe.Setup(s => s.CreateAsync(It.IsAny<PaymentIntentCreateOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(intent);

        [Fact]
        public async Task CreateOrUpdatePaymentIntent_WhenClientItemPriceDiffersFromProduct_CorrectsToProductPrice()
        {
            // Arrange — bogus client price 0.01 vs authoritative product price 100
            var basket = new CustomerBasket("basket-1")
            {
                Items = new List<BasketItem> { new BasketItem { Id = 1, Quantity = 2, Price = 0.01m } }
            };
            _basketRepo.Setup(r => r.GetBasketAsync("basket-1")).ReturnsAsync(basket);
            _productRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(new Product { Id = 1, Price = 100m });
            SetupCreateReturns(new PaymentIntent { Id = "pi_test_123", ClientSecret = "secret_123" });
            var sut = CreateSut();

            // Act
            var result = await sut.CreateOrUpdatePaymentIntent("basket-1");

            // Assert
            result.Items.First().Price.Should().Be(100m);
        }

        [Fact]
        public async Task CreateOrUpdatePaymentIntent_WhenCreatingWithShipping_ComputesAmountIncludingShipping()
        {
            // Arrange — 2 * 100 * 100 = 20000 plus shipping 10 * 100 = 1000 => 21000
            var basket = new CustomerBasket("basket-1")
            {
                DeliveryMethodId = 1,
                Items = new List<BasketItem> { new BasketItem { Id = 1, Quantity = 2, Price = 100m } }
            };
            _basketRepo.Setup(r => r.GetBasketAsync("basket-1")).ReturnsAsync(basket);
            _deliveryRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(new DeliveryMethod { Id = 1, Price = 10m });
            _productRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(new Product { Id = 1, Price = 100m });

            PaymentIntentCreateOptions captured = null;
            _stripe.Setup(s => s.CreateAsync(It.IsAny<PaymentIntentCreateOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
                   .Callback<PaymentIntentCreateOptions, RequestOptions, CancellationToken>((o, ro, ct) => captured = o)
                   .ReturnsAsync(new PaymentIntent { Id = "pi_test_123", ClientSecret = "secret_123" });
            var sut = CreateSut();

            // Act
            await sut.CreateOrUpdatePaymentIntent("basket-1");

            // Assert
            captured.Should().NotBeNull();
            captured.Amount.Should().Be(21000L);
        }

        [Fact]
        public async Task CreateOrUpdatePaymentIntent_WhenPaymentIntentIdEmpty_CreatesIntentAndSetsBasketFields()
        {
            // Arrange — create branch (empty PaymentIntentId)
            var basket = new CustomerBasket("basket-1")
            {
                Items = new List<BasketItem> { new BasketItem { Id = 1, Quantity = 1, Price = 50m } }
            };
            _basketRepo.Setup(r => r.GetBasketAsync("basket-1")).ReturnsAsync(basket);
            _productRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(new Product { Id = 1, Price = 50m });
            SetupCreateReturns(new PaymentIntent { Id = "pi_test_123", ClientSecret = "secret_123" });
            var sut = CreateSut();

            // Act
            var result = await sut.CreateOrUpdatePaymentIntent("basket-1");

            // Assert
            result.PaymentIntentId.Should().Be("pi_test_123");
            result.ClientSecret.Should().Be("secret_123");
            _stripe.Verify(s => s.CreateAsync(It.IsAny<PaymentIntentCreateOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()), Times.Once);
            _basketRepo.Verify(r => r.UpdateBasketAsync(basket), Times.Once);
        }

        [Fact]
        public async Task CreateOrUpdatePaymentIntent_WhenPaymentIntentIdPresent_UpdatesIntentWithoutReassigningId()
        {
            // Arrange — update branch (existing PaymentIntentId); 1 * 50 * 100 = 5000, no shipping
            var basket = new CustomerBasket("basket-1")
            {
                PaymentIntentId = "pi_existing",
                ClientSecret = "secret_existing",
                Items = new List<BasketItem> { new BasketItem { Id = 1, Quantity = 1, Price = 50m } }
            };
            _basketRepo.Setup(r => r.GetBasketAsync("basket-1")).ReturnsAsync(basket);
            _productRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(new Product { Id = 1, Price = 50m });

            PaymentIntentUpdateOptions captured = null;
            _stripe.Setup(s => s.UpdateAsync(It.IsAny<string>(), It.IsAny<PaymentIntentUpdateOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
                   .Callback<string, PaymentIntentUpdateOptions, RequestOptions, CancellationToken>((id, o, ro, ct) => captured = o)
                   .ReturnsAsync(new PaymentIntent { Id = "pi_existing", ClientSecret = "secret_existing" });
            var sut = CreateSut();

            // Act
            var result = await sut.CreateOrUpdatePaymentIntent("basket-1");

            // Assert
            result.PaymentIntentId.Should().Be("pi_existing");
            captured.Should().NotBeNull();
            captured.Amount.Should().Be(5000L);
            _stripe.Verify(s => s.UpdateAsync("pi_existing", It.IsAny<PaymentIntentUpdateOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()), Times.Once);
            _stripe.Verify(s => s.CreateAsync(It.IsAny<PaymentIntentCreateOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()), Times.Never);
            _basketRepo.Verify(r => r.UpdateBasketAsync(basket), Times.Once);
        }

        [Fact]
        public async Task CreateOrUpdatePaymentIntent_WhenBasketNotFound_ReturnsNullWithoutStripeInteraction()
        {
            // Arrange — null-basket guard exists in PaymentService
            _basketRepo.Setup(r => r.GetBasketAsync(It.IsAny<string>())).ReturnsAsync((CustomerBasket)null);
            var sut = CreateSut();

            // Act
            var result = await sut.CreateOrUpdatePaymentIntent("missing");

            // Assert
            result.Should().BeNull();
            _stripe.Verify(s => s.CreateAsync(It.IsAny<PaymentIntentCreateOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()), Times.Never);
            _stripe.Verify(s => s.UpdateAsync(It.IsAny<string>(), It.IsAny<PaymentIntentUpdateOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()), Times.Never);
            _basketRepo.Verify(r => r.UpdateBasketAsync(It.IsAny<CustomerBasket>()), Times.Never);
        }

        [Fact]
        public async Task UpdateOrderPaymentSucceeded_WhenOrderExists_SetsPaymentReceivedAndUpdatesAndCompletes()
        {
            // Arrange
            var order = new Order();
            _orderRepo.Setup(r => r.GetEntityWithSpec(It.IsAny<ISpecification<Order>>())).ReturnsAsync(order);
            _unitOfWork.Setup(u => u.Complete()).ReturnsAsync(1);
            var sut = CreateSut();

            // Act
            var result = await sut.UpdateOrderPaymentSucceeded("pi_123");

            // Assert
            result.Should().BeSameAs(order);
            order.Status.Should().Be(OrderStatus.PaymentReceived);
            _orderRepo.Verify(r => r.Update(order), Times.Once);
            _unitOfWork.Verify(u => u.Complete(), Times.Once);
        }

        [Fact]
        public async Task UpdateOrderPaymentSucceeded_WhenOrderNotFound_ReturnsNull()
        {
            // Arrange
            _orderRepo.Setup(r => r.GetEntityWithSpec(It.IsAny<ISpecification<Order>>())).ReturnsAsync((Order)null);
            var sut = CreateSut();

            // Act
            var result = await sut.UpdateOrderPaymentSucceeded("pi_missing");

            // Assert
            result.Should().BeNull();
            _orderRepo.Verify(r => r.Update(It.IsAny<Order>()), Times.Never);
            _unitOfWork.Verify(u => u.Complete(), Times.Never);
        }

        [Fact]
        public async Task UpdateOrderPaymentFailed_WhenOrderExists_SetsPaymentFailedAndCompletesWithoutUpdate()
        {
            // Arrange
            var order = new Order();
            _orderRepo.Setup(r => r.GetEntityWithSpec(It.IsAny<ISpecification<Order>>())).ReturnsAsync(order);
            _unitOfWork.Setup(u => u.Complete()).ReturnsAsync(1);
            var sut = CreateSut();

            // Act
            var result = await sut.UpdateOrderPaymentFailed("pi_123");

            // Assert
            result.Should().BeSameAs(order);
            order.Status.Should().Be(OrderStatus.PaymentFailed);
            _orderRepo.Verify(r => r.Update(It.IsAny<Order>()), Times.Never); // Failed path does NOT call Update
            _unitOfWork.Verify(u => u.Complete(), Times.Once);
        }

        [Fact]
        public async Task UpdateOrderPaymentFailed_WhenOrderNotFound_ReturnsNull()
        {
            // Arrange
            _orderRepo.Setup(r => r.GetEntityWithSpec(It.IsAny<ISpecification<Order>>())).ReturnsAsync((Order)null);
            var sut = CreateSut();

            // Act
            var result = await sut.UpdateOrderPaymentFailed("pi_missing");

            // Assert
            result.Should().BeNull();
            _unitOfWork.Verify(u => u.Complete(), Times.Never);
        }

        [Fact]
        public void CreatePaymentIntentService_ByDefault_ReturnsRealStripePaymentIntentService()
        {
            // Arrange — the production default of the testability seam (no override). This guards the
            // documented behavior that production still uses a genuine Stripe PaymentIntentService, while
            // remaining fully offline (constructing the service performs no network I/O).
            var probe = new BaseFactoryProbe(_basketRepo.Object, _unitOfWork.Object, _config.Object);

            // Act
            var service = probe.InvokeBaseFactory();

            // Assert
            service.Should().NotBeNull();
            service.Should().BeOfType<PaymentIntentService>();
        }
    }
}
