using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using Infrastructure.Data;
using Infrastructure.Services;
using Infrastructure.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Moq;
using StackExchange.Redis;
using Xunit;
// Disambiguate IDatabase: both StackExchange.Redis and Microsoft.EntityFrameworkCore.Storage
// (imported above for IDbContextTransaction, used by the row-locking test seam) declare an
// IDatabase type. In this test IDatabase always means the mocked Redis client database — mirrors
// the alias the production InventoryService uses for the same reason.
using IDatabase = StackExchange.Redis.IDatabase;

namespace Infrastructure.Tests.Services
{
    /// <summary>
    /// Unit tests for <see cref="InventoryService"/> — reservation lifecycle, stock math, Redis
    /// counter mutation, camelCase publish payload, and fail-closed behavior. Runs on EF Core
    /// InMemory via a test subclass that neutralizes the PostgreSQL row-locking seams. Fresh
    /// mocks + SUT + context per test.
    /// </summary>
    public class InventoryServiceTests
    {
        private readonly Mock<IConnectionMultiplexer> _mockMux;
        private readonly Mock<IDatabase> _mockDb;
        private readonly Mock<ISubscriber> _mockSub;
        private readonly Mock<IFlashSaleService> _mockFlashSale;
        private readonly Mock<IConfiguration> _mockConfig;
        private readonly StoreContext _context;
        private readonly TestableInventoryService _sut;

        public InventoryServiceTests()
        {
            _mockDb = new Mock<IDatabase>();
            _mockSub = new Mock<ISubscriber>();
            _mockMux = new Mock<IConnectionMultiplexer>();
            _mockMux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_mockDb.Object);
            _mockMux.Setup(m => m.GetSubscriber(It.IsAny<object>())).Returns(_mockSub.Object);

            // Safe defaults so un-arranged Redis calls never NRE; individual tests override as needed.
            _mockDb.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(RedisValue.Null);
            _mockDb.Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
                    It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(true);
            _mockDb.Setup(d => d.StringDecrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(0L);
            _mockDb.Setup(d => d.StringIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(0L);
            _mockDb.Setup(d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(true);
            _mockDb.Setup(d => d.KeyExpireAsync(It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(true);
            _mockSub.Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(0L);

            _mockFlashSale = new Mock<IFlashSaleService>();
            _mockFlashSale.Setup(f => f.GetActiveFlashSaleForProductAsync(It.IsAny<int>()))
                .ReturnsAsync((FlashSale)null); // default: general pool

            _mockConfig = new Mock<IConfiguration>();
            _mockConfig.Setup(c => c["Inventory:ReservationTtlMinutes"]).Returns("10");

            _context = TestStoreContextFactory.CreateInMemoryContext(Guid.NewGuid().ToString());
            _sut = new TestableInventoryService(
                _context, _mockMux.Object, _mockFlashSale.Object, _mockConfig.Object);
        }

        // Neutralizes the PostgreSQL-only row-locking seams so the SUT runs on EF InMemory.
        private class TestableInventoryService : InventoryService
        {
            public TestableInventoryService(StoreContext ctx, IConnectionMultiplexer redis,
                IFlashSaleService fss, IConfiguration cfg)
                : base(ctx, redis, fss, cfg) { }

            protected override bool SupportsRowLocking() => false;
            protected override Task<IDbContextTransaction> BeginLockingTransactionAsync()
                => Task.FromResult<IDbContextTransaction>(null);
            protected override Task AcquireProductRowLockAsync(int productId) => Task.CompletedTask;
        }

        // 1. GetAvailableStock DB fallback on a Redis miss.
        [Fact]
        public async Task GetAvailableStockAsync_WhenRedisMisses_ReturnsDatabaseComputedAvailable()
        {
            // Arrange
            _mockDb.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(RedisValue.Null);
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId, quantity: 3, status: ReservationStatus.Active, flashSaleId: null);

            // Act
            var result = await _sut.GetAvailableStockAsync(productId);

            // Assert (10 committed - 3 active reserved)
            result.Should().Be(7);
        }

        // 2. GetAvailableStock hot path (cached value; negative clamps to 0).
        [Theory]
        [InlineData("5", 5)]
        [InlineData("-3", 0)]
        public async Task GetAvailableStockAsync_WhenRedisCounterPresent_ReturnsClampedCachedValue(
            string counter, int expected)
        {
            // Arrange
            _mockDb.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisValue)counter);

            // Act
            var result = await _sut.GetAvailableStockAsync(1);

            // Assert
            result.Should().Be(expected);
        }

        // 3. CreateReservation success.
        [Fact]
        public async Task CreateReservationAsync_WhenStockSufficient_PersistsReservationAndDecrementsCounter()
        {
            // Arrange
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;

            // Act
            var result = await _sut.CreateReservationAsync("basket-create", productId, 4);

            // Assert
            result.Should().BeTrue();

            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-create");
            reservation.Should().NotBeNull();
            reservation.ProductId.Should().Be(productId);
            reservation.Quantity.Should().Be(4);
            reservation.Status.Should().Be(ReservationStatus.Active);

            _mockDb.Verify(d => d.StringDecrementAsync(
                It.Is<RedisKey>(k => k == $"stock:product:{productId}"), 4, It.IsAny<CommandFlags>()),
                Times.Once);
            _mockDb.Verify(d => d.StringSetAsync(
                It.Is<RedisKey>(k => k == $"reservation:basket-create:{productId}"),
                It.IsAny<RedisValue>(),
                It.Is<TimeSpan?>(t => t.HasValue && t.Value > TimeSpan.Zero),
                It.IsAny<When>(), It.IsAny<CommandFlags>()),
                Times.Once);
            _mockSub.Verify(s => s.PublishAsync(
                It.Is<RedisChannel>(c => c == "stock-updates"), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()),
                Times.Once);
        }

        // 4. CreateReservation insufficient stock.
        [Fact]
        public async Task CreateReservationAsync_WhenStockInsufficient_ReturnsFalseAndDoesNotDecrement()
        {
            // Arrange (capacity 5, already 5 reserved => available 0)
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 5);
            var productId = products[0].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId, quantity: 5, status: ReservationStatus.Active, flashSaleId: null);

            // Act
            var result = await _sut.CreateReservationAsync("basket-insufficient", productId, 1);

            // Assert
            result.Should().BeFalse();
            (await _context.Reservations.CountAsync(r => r.BasketId == "basket-insufficient"))
                .Should().Be(0);
            _mockDb.Verify(d => d.StringDecrementAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
        }

        // 5. CreateReservation non-positive quantity.
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task CreateReservationAsync_WhenQuantityNotPositive_ReturnsFalse(int quantity)
        {
            // Arrange
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;

            // Act
            var result = await _sut.CreateReservationAsync("basket-bad-qty", productId, quantity);

            // Assert
            result.Should().BeFalse();
            (await _context.Reservations.CountAsync(r => r.BasketId == "basket-bad-qty")).Should().Be(0);
            _mockDb.Verify(d => d.StringDecrementAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
        }

        // 6. CreateReservation product not found.
        [Fact]
        public async Task CreateReservationAsync_WhenProductNotFound_ReturnsFalse()
        {
            // Act (no product seeded => id 999 does not exist)
            var result = await _sut.CreateReservationAsync("basket-missing", 999, 1);

            // Assert
            result.Should().BeFalse();
            (await _context.Reservations.CountAsync()).Should().Be(0);
            _mockDb.Verify(d => d.StringDecrementAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
        }

        // 7. Commit against the general product pool.
        [Fact]
        public async Task CommitReservationAsync_WhenGeneralPoolReservation_CommitsAndDecrementsProductStock()
        {
            // Arrange
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId, quantity: 3, status: ReservationStatus.Active,
                basketId: "basket-commit", flashSaleId: null);

            // Act — commit STAGES only, so flush explicitly before asserting.
            await _sut.CommitReservationAsync("basket-commit");
            await _context.SaveChangesAsync();

            // Assert
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-commit");
            reservation.Status.Should().Be(ReservationStatus.Committed);

            var product = await _context.Products.FindAsync(productId);
            product.StockQuantity.Should().Be(7);

            _mockDb.Verify(d => d.KeyDeleteAsync(
                It.Is<RedisKey>(k => k == $"reservation:basket-commit:{productId}"), It.IsAny<CommandFlags>()),
                Times.Once);
            _mockDb.Verify(d => d.StringDecrementAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
            _mockDb.Verify(d => d.StringIncrementAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
        }

        // 8. Commit against a flash-sale pool.
        [Fact]
        public async Task CommitReservationAsync_WhenFlashSaleReservation_DecrementsSalePoolNotProductStock()
        {
            // Arrange
            var now = DateTimeOffset.UtcNow;
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            var sales = await TestStoreContextFactory.SeedFlashSalesAsync(
                _context, productId, saleStockQuantity: 20,
                startsAt: now.AddMinutes(-5), endsAt: now.AddMinutes(30), status: FlashSaleStatus.Active);
            var flashSaleId = sales[0].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId, quantity: 5, status: ReservationStatus.Active,
                basketId: "basket-fs", flashSaleId: flashSaleId);

            // Act
            await _sut.CommitReservationAsync("basket-fs");
            await _context.SaveChangesAsync();

            // Assert
            var sale = await _context.FlashSales.FindAsync(flashSaleId);
            sale.SaleStockQuantity.Should().Be(15);
            var product = await _context.Products.FindAsync(productId);
            product.StockQuantity.Should().Be(10); // untouched
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-fs");
            reservation.Status.Should().Be(ReservationStatus.Committed);
        }

        // 9. Release an active reservation.
        [Fact]
        public async Task ReleaseReservationAsync_WhenActiveReservationExists_CancelsAndIncrementsCounter()
        {
            // Arrange
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId, quantity: 4, status: ReservationStatus.Active,
                basketId: "basket-release", flashSaleId: null);

            // Act
            await _sut.ReleaseReservationAsync("basket-release", productId);

            // Assert
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-release");
            reservation.Status.Should().Be(ReservationStatus.Cancelled);

            _mockDb.Verify(d => d.StringIncrementAsync(
                It.Is<RedisKey>(k => k == $"stock:product:{productId}"), 4, It.IsAny<CommandFlags>()),
                Times.Once);
            _mockDb.Verify(d => d.KeyDeleteAsync(
                It.Is<RedisKey>(k => k == $"reservation:basket-release:{productId}"), It.IsAny<CommandFlags>()),
                Times.Once);
            _mockSub.Verify(s => s.PublishAsync(
                It.Is<RedisChannel>(c => c == "stock-updates"), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()),
                Times.Once);
        }

        // 10. Seed stock counters for every product.
        [Fact]
        public async Task SeedStockCountersAsync_WhenProductsExist_SetsCounterPerProduct()
        {
            // Arrange
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 2, stockQuantity: 10);
            var firstId = products[0].Id;
            var secondId = products[1].Id;

            // Act
            await _sut.SeedStockCountersAsync();

            // Assert
            _mockDb.Verify(d => d.StringSetAsync(
                It.Is<RedisKey>(k => k == $"stock:product:{firstId}"),
                It.Is<RedisValue>(v => v == 10),
                It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()),
                Times.Once);
            _mockDb.Verify(d => d.StringSetAsync(
                It.Is<RedisKey>(k => k == $"stock:product:{secondId}"),
                It.Is<RedisValue>(v => v == 10),
                It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()),
                Times.Once);
            _mockSub.Verify(s => s.PublishAsync(
                It.Is<RedisChannel>(c => c == "stock-updates"), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()),
                Times.Exactly(2));
        }

        // 11. Publish payload shape (camelCase, nullable flashSaleId).
        [Fact]
        public async Task SeedStockCountersAsync_WhenPublishing_EmitsCamelCaseStockUpdatePayload()
        {
            // Arrange
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            RedisValue captured = default;
            _mockSub.Setup(s => s.PublishAsync(
                    It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Callback<RedisChannel, RedisValue, CommandFlags>((ch, val, flags) => captured = val)
                .ReturnsAsync(0L);

            // Act
            await _sut.SeedStockCountersAsync();

            // Assert — parse the observable JSON, not serializer internals.
            captured.HasValue.Should().BeTrue();
            using var doc = JsonDocument.Parse(captured.ToString());
            var root = doc.RootElement;
            root.TryGetProperty("productId", out var productIdProp).Should().BeTrue();
            root.TryGetProperty("currentStock", out _).Should().BeTrue();
            root.TryGetProperty("flashSaleId", out var flashSaleProp).Should().BeTrue();
            productIdProp.GetInt32().Should().Be(productId);
            flashSaleProp.ValueKind.Should().Be(JsonValueKind.Null); // general pool => null
        }

        // 12. FAIL-CLOSED on create: Redis outage does not block the DB row-locked reservation.
        [Fact]
        public async Task CreateReservationAsync_WhenRedisUnavailable_StillReservesViaDatabase()
        {
            // Arrange
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            _mockDb.Setup(d => d.StringDecrementAsync(
                    It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
                .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "redis down"));

            // Act
            var result = await _sut.CreateReservationAsync("basket-failclosed", productId, 4);

            // Assert — reservation persisted despite the Redis outage; stock never assumed available.
            result.Should().BeTrue();
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-failclosed");
            reservation.Should().NotBeNull();
            reservation.Status.Should().Be(ReservationStatus.Active);
            reservation.Quantity.Should().Be(4);
        }

        // 13. FAIL-CLOSED on read: Redis outage falls back to the DB-computed available stock.
        [Fact]
        public async Task GetAvailableStockAsync_WhenRedisUnavailable_ReturnsDatabaseComputedAvailable()
        {
            // Arrange
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId, quantity: 3, status: ReservationStatus.Active, flashSaleId: null);
            _mockDb.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "redis down"));

            // Act
            var result = await _sut.GetAvailableStockAsync(productId);

            // Assert (10 - 3), never an assumed-available number
            result.Should().Be(7);
        }
    }
}
