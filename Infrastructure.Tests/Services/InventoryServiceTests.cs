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

            // (F4) Commit STAGES ONLY: the Redis hold key must NOT be deleted here. Deletion is deferred to
            // FinalizeCommittedHoldsAsync, invoked by OrderService only AFTER a successful flush, so that a
            // rolled-back order leaves the hold key intact and Redis stays consistent with the reverted reservation.
            _mockDb.Verify(d => d.KeyDeleteAsync(
                It.Is<RedisKey>(k => k == $"reservation:basket-commit:{productId}"), It.IsAny<CommandFlags>()),
                Times.Never);
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

        // 14. Extend with NO existing Active hold CREATES the hold at the requested desired TOTAL, INSIDE
        //     the row lock (create-or-set; it no longer rolls back and delegates to an ADDITIVE create).
        //     A positive desired total is held in full and the counter is DECR'd by exactly that amount.
        [Fact]
        public async Task ExtendReservationAsync_WhenNoExistingHold_CreatesHoldAtDesiredTotal()
        {
            // Arrange
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;

            // Act
            var result = await _sut.ExtendReservationAsync("basket-extend-new", productId, 3);

            // Assert — a fresh Active hold was created at the requested desired total.
            result.Should().BeTrue();
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-extend-new");
            reservation.Should().NotBeNull();
            reservation.Status.Should().Be(ReservationStatus.Active);
            reservation.Quantity.Should().Be(3);
            _mockDb.Verify(d => d.StringDecrementAsync(
                It.Is<RedisKey>(k => k == $"stock:product:{productId}"), 3, It.IsAny<CommandFlags>()),
                Times.Once);
        }

        // 14b. Extend with NO existing hold and a NON-POSITIVE desired total is REJECTED (returns false):
        //      no reservation row is created and the counter is never moved. This is the P4-26 fix — the
        //      previous delegation floored the quantity to 1 (`quantity > 0 ? quantity : 1`), manufacturing
        //      a phantom 1-unit hold and a spurious decrement for a request that reserves nothing.
        [Theory]
        [InlineData(0)]
        [InlineData(-2)]
        public async Task ExtendReservationAsync_WhenNoExistingHoldAndQuantityNotPositive_RejectsAndCreatesNoHold(
            int requested)
        {
            // Arrange
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;

            // Act
            var result = await _sut.ExtendReservationAsync("basket-extend-new-nonpos", productId, requested);

            // Assert — denied, nothing persisted, counter untouched.
            result.Should().BeFalse();
            (await _context.Reservations.AnyAsync(r => r.BasketId == "basket-extend-new-nonpos"))
                .Should().BeFalse();
            _mockDb.Verify(d => d.StringDecrementAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
        }

        // 15. Extend GROWS an existing hold within capacity: DECR the counter by the positive delta.
        [Fact]
        public async Task ExtendReservationAsync_WhenGrowingWithinCapacity_IncreasesQuantityAndDecrementsByDelta()
        {
            // Arrange (stock 10; existing hold of 1)
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId, quantity: 1, status: ReservationStatus.Active,
                basketId: "basket-grow", flashSaleId: null);

            // Act — new desired total 3 => delta +2
            await _sut.ExtendReservationAsync("basket-grow", productId, 3);

            // Assert
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-grow");
            reservation.Quantity.Should().Be(3);
            _mockDb.Verify(d => d.StringDecrementAsync(
                It.Is<RedisKey>(k => k == $"stock:product:{productId}"), 2, It.IsAny<CommandFlags>()),
                Times.Once);
            _mockDb.Verify(d => d.StringIncrementAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
            // F-REDIS-1: the hold-key value must be written as the NEW held quantity (3), not a literal 0,
            // via a conditional (When.Exists) SET so an expired hold is not recreated.
            _mockDb.Verify(d => d.StringSetAsync(
                It.Is<RedisKey>(k => k == $"reservation:basket-grow:{productId}"),
                It.Is<RedisValue>(v => v == 3L),
                It.IsAny<TimeSpan?>(), When.Exists, It.IsAny<CommandFlags>()),
                Times.Once);
        }

        // 16. Extend SHRINKS an existing hold: INCR the counter back by the released amount.
        [Fact]
        public async Task ExtendReservationAsync_WhenShrinking_DecreasesQuantityAndIncrementsByReleased()
        {
            // Arrange (stock 10; existing hold of 3)
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId, quantity: 3, status: ReservationStatus.Active,
                basketId: "basket-shrink", flashSaleId: null);

            // Act — new desired total 1 => delta -2
            await _sut.ExtendReservationAsync("basket-shrink", productId, 1);

            // Assert
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-shrink");
            reservation.Quantity.Should().Be(1);
            _mockDb.Verify(d => d.StringIncrementAsync(
                It.Is<RedisKey>(k => k == $"stock:product:{productId}"), 2, It.IsAny<CommandFlags>()),
                Times.Once);
            _mockDb.Verify(d => d.StringDecrementAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
        }

        // 17. Extend to the SAME quantity is a delta=0 no-op: neither DECR nor INCR, but the hold
        //     TTL is refreshed (conditional SET on the hold key) and the current stock re-published.
        [Fact]
        public async Task ExtendReservationAsync_WhenQuantityUnchanged_RefreshesTtlWithoutCounterMove()
        {
            // Arrange (existing hold of 3)
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId, quantity: 3, status: ReservationStatus.Active,
                basketId: "basket-same", flashSaleId: null);

            // Act — new desired total equals current => delta 0
            await _sut.ExtendReservationAsync("basket-same", productId, 3);

            // Assert
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-same");
            reservation.Quantity.Should().Be(3);
            _mockDb.Verify(d => d.StringDecrementAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
            _mockDb.Verify(d => d.StringIncrementAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
            // F-REDIS-1: even on a delta=0 TTL-only refresh, the hold-key value must be the held
            // quantity (3), never a literal 0.
            _mockDb.Verify(d => d.StringSetAsync(
                It.Is<RedisKey>(k => k == $"reservation:basket-same:{productId}"),
                It.Is<RedisValue>(v => v == 3L), It.IsAny<TimeSpan?>(), When.Exists, It.IsAny<CommandFlags>()),
                Times.Once);
            _mockSub.Verify(s => s.PublishAsync(
                It.Is<RedisChannel>(c => c == "stock-updates"), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()),
                Times.Once);
        }

        // 18. Extend that would GROW beyond available capacity renews the TTL only: the quantity is
        //     NOT changed and the counter is NOT decremented (KeyExpire refreshes the hold instead).
        [Fact]
        public async Task ExtendReservationAsync_WhenGrowthExceedsAvailable_RenewsTtlOnly()
        {
            // Arrange (stock 3; existing hold already consumes all 3 => available 0)
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 3);
            var productId = products[0].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId, quantity: 3, status: ReservationStatus.Active,
                basketId: "basket-nogrow", flashSaleId: null);

            // Act — attempt to grow to 5 (delta +2) with 0 available
            await _sut.ExtendReservationAsync("basket-nogrow", productId, 5);

            // Assert — quantity unchanged, no DECR, TTL renewed via KeyExpire.
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-nogrow");
            reservation.Quantity.Should().Be(3);
            _mockDb.Verify(d => d.StringDecrementAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
            _mockDb.Verify(d => d.KeyExpireAsync(
                It.Is<RedisKey>(k => k == $"reservation:basket-nogrow:{productId}"),
                It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()),
                Times.Once);
        }

        // 19. A reservation created against a flash-sale pool stays bound to that pool on extend even
        //     after the sale is no longer active: the shrink INCRs and the published payload carries
        //     the bound (non-null) flashSaleId — the bound pool remains authoritative through the hold.
        [Fact]
        public async Task ExtendReservationAsync_WhenFlashBoundAndSaleEnded_KeepsBoundPoolOnPublish()
        {
            // Arrange — an (ended) flash sale exists in the DB and a reservation is bound to it, but
            // the flash-sale resolver now reports NO active sale (its window has elapsed).
            var now = DateTimeOffset.UtcNow;
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            var sales = await TestStoreContextFactory.SeedFlashSalesAsync(
                _context, productId, saleStockQuantity: 20,
                startsAt: now.AddMinutes(-30), endsAt: now.AddMinutes(-1), status: FlashSaleStatus.Ended);
            var flashSaleId = sales[0].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId, quantity: 5, status: ReservationStatus.Active,
                basketId: "basket-flash", flashSaleId: flashSaleId);
            _mockFlashSale.Setup(f => f.GetActiveFlashSaleForProductAsync(productId))
                .ReturnsAsync((FlashSale)null); // sale window has ended

            RedisValue capturedPayload = default;
            _mockSub.Setup(s => s.PublishAsync(
                    It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Callback<RedisChannel, RedisValue, CommandFlags>((ch, val, flags) => capturedPayload = val)
                .ReturnsAsync(0L);

            // Act — shrink 5 => 3 (delta -2); the bound pool must remain the flash sale.
            await _sut.ExtendReservationAsync("basket-flash", productId, 3);

            // Assert — INCR by 2 and the payload's flashSaleId is the bound sale id (still authoritative).
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-flash");
            reservation.Quantity.Should().Be(3);
            reservation.FlashSaleId.Should().Be(flashSaleId);
            _mockDb.Verify(d => d.StringIncrementAsync(
                It.Is<RedisKey>(k => k == $"stock:product:{productId}"), 2, It.IsAny<CommandFlags>()),
                Times.Once);

            capturedPayload.HasValue.Should().BeTrue();
            using var doc = JsonDocument.Parse(capturedPayload.ToString());
            doc.RootElement.GetProperty("flashSaleId").GetInt32().Should().Be(flashSaleId);
        }

        // 20. REGRESSION (M-EDGE1): extending to a non-positive quantity leaves the authoritative DB
        //     hold unchanged, so the Redis counter MUST NOT move. Before the fix the full negative
        //     delta was applied and the counter was spuriously INCR'd, transiently over-reporting
        //     available stock (and the broadcast badge). The hold TTL is still refreshed and the
        //     current stock re-published (publish-only path).
        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        public async Task ExtendReservationAsync_WhenQuantityNotPositive_DoesNotMoveCounterAndKeepsHold(int requested)
        {
            // Arrange (existing Active hold of 4)
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId, quantity: 4, status: ReservationStatus.Active,
                basketId: "basket-zero", flashSaleId: null);

            // Act
            await _sut.ExtendReservationAsync("basket-zero", productId, requested);

            // Assert — DB hold unchanged AND no spurious counter movement (the M-EDGE1 fix).
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-zero");
            reservation.Quantity.Should().Be(4);
            _mockDb.Verify(d => d.StringIncrementAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
            _mockDb.Verify(d => d.StringDecrementAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
            _mockSub.Verify(s => s.PublishAsync(
                It.Is<RedisChannel>(c => c == "stock-updates"), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()),
                Times.Once);
        }

        // 21. A second CreateReservation for the same basket+product+pool TOPS UP the existing Active
        //     hold rather than inserting a second row; each call DECRs by its own quantity.
        [Fact]
        public async Task CreateReservationAsync_WhenActiveHoldExists_TopsUpQuantityAndDecrementsEachTime()
        {
            // Arrange
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;

            // Act — two creates against the same basket/pool
            var first = await _sut.CreateReservationAsync("basket-topup", productId, 3);
            var second = await _sut.CreateReservationAsync("basket-topup", productId, 2);

            // Assert — one row, quantity 5, two DECRs (3 then 2)
            first.Should().BeTrue();
            second.Should().BeTrue();
            (await _context.Reservations.CountAsync(r => r.BasketId == "basket-topup")).Should().Be(1);
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-topup");
            reservation.Quantity.Should().Be(5);
            _mockDb.Verify(d => d.StringDecrementAsync(
                It.Is<RedisKey>(k => k == $"stock:product:{productId}"), 3, It.IsAny<CommandFlags>()),
                Times.Once);
            _mockDb.Verify(d => d.StringDecrementAsync(
                It.Is<RedisKey>(k => k == $"stock:product:{productId}"), 2, It.IsAny<CommandFlags>()),
                Times.Once);
        }

        // 22. A malformed (non-numeric) Redis counter value fails int.TryParse and falls back to the
        //     authoritative DB-computed available stock (fail-closed; never trust an unparseable value).
        [Fact]
        public async Task GetAvailableStockAsync_WhenCachedValueMalformed_FallsBackToDatabase()
        {
            // Arrange
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId, quantity: 3, status: ReservationStatus.Active, flashSaleId: null);
            _mockDb.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisValue)"not-a-number");

            // Act
            var result = await _sut.GetAvailableStockAsync(productId);

            // Assert (10 committed - 3 active reserved)
            result.Should().Be(7);
        }

        // 23. A Redis TIMEOUT on the counter read falls back to the DB-computed available stock.
        [Fact]
        public async Task GetAvailableStockAsync_WhenRedisTimesOut_FallsBackToDatabase()
        {
            // Arrange
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId, quantity: 3, status: ReservationStatus.Active, flashSaleId: null);
            _mockDb.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ThrowsAsync(new RedisTimeoutException("redis timeout", CommandStatus.Unknown));

            // Act
            var result = await _sut.GetAvailableStockAsync(productId);

            // Assert
            result.Should().Be(7);
        }

        // 24. FAIL-CLOSED: a Redis outage at the hold-key SET does not block the DB row-locked
        //     reservation (the hold is still persisted; stock is never assumed available).
        [Fact]
        public async Task CreateReservationAsync_WhenRedisSetThrows_StillReservesViaDatabase()
        {
            // Arrange
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            _mockDb.Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
                    It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .ThrowsAsync(new RedisTimeoutException("redis timeout", CommandStatus.Unknown));

            // Act
            var result = await _sut.CreateReservationAsync("basket-set-down", productId, 4);

            // Assert — reservation persisted despite the Redis SET outage.
            result.Should().BeTrue();
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-set-down");
            reservation.Should().NotBeNull();
            reservation.Quantity.Should().Be(4);
            reservation.Status.Should().Be(ReservationStatus.Active);
        }

        // 25. FAIL-CLOSED: a Redis outage at the pub/sub PUBLISH does not block the reservation.
        [Fact]
        public async Task CreateReservationAsync_WhenRedisPublishThrows_StillReservesViaDatabase()
        {
            // Arrange
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            _mockSub.Setup(s => s.PublishAsync(
                    It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "redis down"));

            // Act
            var result = await _sut.CreateReservationAsync("basket-pub-down", productId, 4);

            // Assert — reservation persisted despite the Redis publish outage.
            result.Should().BeTrue();
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-pub-down");
            reservation.Should().NotBeNull();
            reservation.Quantity.Should().Be(4);
        }

        // 26. Commit against the general pool tolerates a MISSING product row: the reservation is
        //     still marked Committed and the hold key deleted (no decrement possible, no exception).
        [Fact]
        public async Task CommitReservationAsync_WhenGeneralPoolProductMissing_CommitsWithoutDecrement()
        {
            // Arrange — reservation references a product id that has no Product row (general pool).
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId: 999, quantity: 3, status: ReservationStatus.Active,
                basketId: "basket-missing-prod", flashSaleId: null);

            // Act — commit STAGES only, so flush explicitly before asserting.
            await _sut.CommitReservationAsync("basket-missing-prod");
            await _context.SaveChangesAsync();

            // Assert
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-missing-prod");
            reservation.Status.Should().Be(ReservationStatus.Committed);
            // (F4) Commit STAGES ONLY: no Redis hold-key deletion here (deferred to FinalizeCommittedHoldsAsync).
            _mockDb.Verify(d => d.KeyDeleteAsync(
                It.Is<RedisKey>(k => k == $"reservation:basket-missing-prod:999"), It.IsAny<CommandFlags>()),
                Times.Never);
        }

        // 27. Commit against a flash-sale pool tolerates a MISSING sale row: the reservation is still
        //     marked Committed and the general product stock is left untouched (sale-pool branch ran).
        [Fact]
        public async Task CommitReservationAsync_WhenFlashSaleMissing_CommitsWithoutDecrement()
        {
            // Arrange — reservation bound to a flash-sale id that has no FlashSale row.
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId, quantity: 3, status: ReservationStatus.Active,
                basketId: "basket-missing-sale", flashSaleId: 4242);

            // Act
            await _sut.CommitReservationAsync("basket-missing-sale");
            await _context.SaveChangesAsync();

            // Assert — committed; the untouched product stock proves the sale-pool branch ran (not general).
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-missing-sale");
            reservation.Status.Should().Be(ReservationStatus.Committed);
            var product = await _context.Products.FindAsync(productId);
            product.StockQuantity.Should().Be(10);
        }

        // 28. Releasing when NO Active reservation exists is a no-op: no counter INCR, no hold delete,
        //     no publish (early return before any Redis work).
        [Fact]
        public async Task ReleaseReservationAsync_WhenNoActiveReservation_IsNoOp()
        {
            // Arrange
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;

            // Act — nothing reserved for this basket/product
            await _sut.ReleaseReservationAsync("basket-empty", productId);

            // Assert
            _mockDb.Verify(d => d.StringIncrementAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
            _mockDb.Verify(d => d.KeyDeleteAsync(
                It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Never);
            _mockSub.Verify(s => s.PublishAsync(
                It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()), Times.Never);
        }


        // 14. ExtendReservation grows an existing general-pool hold within available capacity.
        [Fact]
        public async Task ExtendReservationAsync_WhenGrowingGeneralPoolWithinCapacity_UpdatesQuantityAndDecrementsByDelta()
        {
            // Arrange — product stock 10, an existing Active hold of 2 for this basket (general pool).
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId, quantity: 2, status: ReservationStatus.Active,
                basketId: "basket-grow-gen", flashSaleId: null);

            // Act — grow the desired total to 5 (delta = 3; available = 10 - 2 = 8, so granted).
            var result = await _sut.ExtendReservationAsync("basket-grow-gen", productId, 5);

            // Assert
            result.Should().BeTrue();
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-grow-gen");
            reservation.Quantity.Should().Be(5);
            _mockDb.Verify(d => d.StringDecrementAsync(
                It.Is<RedisKey>(k => k == $"stock:product:{productId}"), 3, It.IsAny<CommandFlags>()),
                Times.Once);
        }

        // 15. ExtendReservation with no existing hold creates the hold at the requested desired total
        //     (create-or-set within the row lock) and returns true.
        [Fact]
        public async Task ExtendReservationAsync_WhenNoExistingHold_CreatesReservationAndReturnsTrue()
        {
            // Arrange
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;

            // Act
            var result = await _sut.ExtendReservationAsync("basket-extend-create", productId, 3);

            // Assert — a brand-new Active reservation was created for the requested quantity.
            result.Should().BeTrue();
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-extend-create");
            reservation.Should().NotBeNull();
            reservation.Quantity.Should().Be(3);
            reservation.Status.Should().Be(ReservationStatus.Active);
        }

        // 16. F3 (regression guard): a hold BOUND to a flash sale whose window has ENDED must be
        //     grown against the BOUND sale pool — never the general product stock. The grow is denied
        //     because the bound pool is too small, so the quantity is unchanged and the counter is
        //     never decremented (and therefore can never be driven negative at commit).
        [Fact]
        public async Task ExtendReservationAsync_WhenGrowingFlashSaleBoundHoldAfterSaleEnded_UsesBoundPoolAndDenies()
        {
            // Arrange — general stock is deliberately LARGE (100) to prove it is ignored. The bound flash
            // sale pool is small (5). The reservation (qty 2) is bound to that sale. The sale has ended, so
            // GetActiveFlashSaleForProductAsync returns null (the default) — exactly the F3 trigger.
            var now = DateTimeOffset.UtcNow;
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 100);
            var productId = products[0].Id;
            var sales = await TestStoreContextFactory.SeedFlashSalesAsync(
                _context, productId, saleStockQuantity: 5,
                startsAt: now.AddMinutes(-60), endsAt: now.AddMinutes(-30), status: FlashSaleStatus.Ended);
            var flashSaleId = sales[0].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId, quantity: 2, status: ReservationStatus.Active,
                basketId: "basket-fs-grow", flashSaleId: flashSaleId);
            // No active sale now (window ended) => resolver returns null.
            _mockFlashSale.Setup(f => f.GetActiveFlashSaleForProductAsync(productId))
                .ReturnsAsync((FlashSale)null);

            // Act — attempt to grow the bound hold to 10 (bound pool headroom = 5 - 2 = 3 < delta 8 => deny).
            var result = await _sut.ExtendReservationAsync("basket-fs-grow", productId, 10);

            // Assert — denied; quantity unchanged; counter NEVER decremented (so it can never go negative).
            result.Should().BeFalse();
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-fs-grow");
            reservation.Quantity.Should().Be(2);
            reservation.FlashSaleId.Should().Be(flashSaleId);
            _mockDb.Verify(d => d.StringDecrementAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
        }

        // 17. F3 (control): growing a flash-sale-bound hold WITHIN its bound pool is granted.
        [Fact]
        public async Task ExtendReservationAsync_WhenGrowingFlashSaleBoundHoldWithinBoundPool_GrantsAndDecrementsByDelta()
        {
            // Arrange — bound sale pool 5, existing bound hold of 2 (headroom 3).
            var now = DateTimeOffset.UtcNow;
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 100);
            var productId = products[0].Id;
            var sales = await TestStoreContextFactory.SeedFlashSalesAsync(
                _context, productId, saleStockQuantity: 5,
                startsAt: now.AddMinutes(-5), endsAt: now.AddMinutes(30), status: FlashSaleStatus.Active);
            var flashSaleId = sales[0].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId, quantity: 2, status: ReservationStatus.Active,
                basketId: "basket-fs-grow-ok", flashSaleId: flashSaleId);

            // Act — grow to 4 (delta 2 <= headroom 3) => granted.
            var result = await _sut.ExtendReservationAsync("basket-fs-grow-ok", productId, 4);

            // Assert
            result.Should().BeTrue();
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-fs-grow-ok");
            reservation.Quantity.Should().Be(4);
            _mockDb.Verify(d => d.StringDecrementAsync(
                It.Is<RedisKey>(k => k == $"stock:product:{productId}"), 2, It.IsAny<CommandFlags>()),
                Times.Once);
        }

        // 18. ExtendReservation denies a general-pool grow beyond available capacity (no counter change).
        [Fact]
        public async Task ExtendReservationAsync_WhenGrowingGeneralPoolBeyondCapacity_DeniesAndDoesNotDecrement()
        {
            // Arrange — product stock 5, existing hold 2 (available 3).
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 5);
            var productId = products[0].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId, quantity: 2, status: ReservationStatus.Active,
                basketId: "basket-grow-insuff", flashSaleId: null);

            // Act — grow to 10 (delta 8 > available 3) => deny.
            var result = await _sut.ExtendReservationAsync("basket-grow-insuff", productId, 10);

            // Assert
            result.Should().BeFalse();
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-grow-insuff");
            reservation.Quantity.Should().Be(2);
            _mockDb.Verify(d => d.StringDecrementAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
        }

        // 19. F1: ReleaseAllReservationsForBasketAsync cancels every Active hold for the basket and
        //     restores each product's Redis counter (INCR + hold-key delete + publish).
        [Fact]
        public async Task ReleaseAllReservationsForBasketAsync_WhenBasketHasActiveHolds_CancelsAllAndRestoresCounters()
        {
            // Arrange — two distinct products each with an Active hold for the same basket.
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 2, stockQuantity: 10);
            var firstId = products[0].Id;
            var secondId = products[1].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, firstId, quantity: 4, status: ReservationStatus.Active,
                basketId: "basket-clear", flashSaleId: null);
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, secondId, quantity: 3, status: ReservationStatus.Active,
                basketId: "basket-clear", flashSaleId: null);

            // Act
            await _sut.ReleaseAllReservationsForBasketAsync("basket-clear");

            // Assert — both holds cancelled.
            var remainingActive = await _context.Reservations
                .CountAsync(r => r.BasketId == "basket-clear" && r.Status == ReservationStatus.Active);
            remainingActive.Should().Be(0);

            // Each product's counter restored by its released quantity, hold key deleted, and stock published.
            _mockDb.Verify(d => d.StringIncrementAsync(
                It.Is<RedisKey>(k => k == $"stock:product:{firstId}"), 4, It.IsAny<CommandFlags>()),
                Times.Once);
            _mockDb.Verify(d => d.StringIncrementAsync(
                It.Is<RedisKey>(k => k == $"stock:product:{secondId}"), 3, It.IsAny<CommandFlags>()),
                Times.Once);
            _mockDb.Verify(d => d.KeyDeleteAsync(
                It.Is<RedisKey>(k => k == $"reservation:basket-clear:{firstId}"), It.IsAny<CommandFlags>()),
                Times.Once);
            _mockDb.Verify(d => d.KeyDeleteAsync(
                It.Is<RedisKey>(k => k == $"reservation:basket-clear:{secondId}"), It.IsAny<CommandFlags>()),
                Times.Once);
            _mockSub.Verify(s => s.PublishAsync(
                It.Is<RedisChannel>(c => c == "stock-updates"), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()),
                Times.Exactly(2));
        }

        // 7b. (F4) FinalizeCommittedHoldsAsync deletes the Redis hold keys for a basket's Committed reservations.
        //     This is the deferred, post-flush counterpart to the deletion that CommitReservationAsync no longer does.
        [Fact]
        public async Task FinalizeCommittedHoldsAsync_WhenReservationCommitted_DeletesHoldKeyWithoutTouchingCounter()
        {
            // Arrange — a Committed reservation (as it would exist after a successful order flush).
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId, quantity: 3, status: ReservationStatus.Committed,
                basketId: "basket-finalize", flashSaleId: null);

            // Act
            await _sut.FinalizeCommittedHoldsAsync("basket-finalize");

            // Assert — the hold key for the committed reservation is deleted exactly once; the counter is untouched.
            _mockDb.Verify(d => d.KeyDeleteAsync(
                It.Is<RedisKey>(k => k == $"reservation:basket-finalize:{productId}"), It.IsAny<CommandFlags>()),
                Times.Once);
            _mockDb.Verify(d => d.StringDecrementAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
            _mockDb.Verify(d => d.StringIncrementAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
        }

        // 7c. (F4) FinalizeCommittedHoldsAsync ignores still-Active reservations — it only cleans up Committed holds,
        //     so it never removes a hold key for a reservation that was not durably committed.
        [Fact]
        public async Task FinalizeCommittedHoldsAsync_WhenReservationStillActive_DeletesNoHoldKey()
        {
            // Arrange — an Active reservation (as it would exist if the flush had rolled back).
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            await TestStoreContextFactory.SeedReservationsAsync(
                _context, productId, quantity: 3, status: ReservationStatus.Active,
                basketId: "basket-active", flashSaleId: null);

            // Act
            await _sut.FinalizeCommittedHoldsAsync("basket-active");

            // Assert — no hold key deleted for a non-committed reservation.
            _mockDb.Verify(d => d.KeyDeleteAsync(
                It.Is<RedisKey>(k => k == $"reservation:basket-active:{productId}"), It.IsAny<CommandFlags>()),
                Times.Never);
        }

        // 28. REGRESSION (QA Issue #4): a DECR against an ABSENT stock:product:{id} key returns a
        //     NEGATIVE value — Redis materializes the missing key as 0 and then decrements below zero,
        //     which previously surfaced a spurious negative available stock on the hub-driven badges.
        //     The reserve path must SELF-HEAL the counter to the authoritative PostgreSQL-computed
        //     available (capacity minus Active reservations) and publish that non-negative truth, never
        //     the transient negative — while the reservation itself still succeeds, because the DB row
        //     lock (not the Redis counter) is the sole authority that gates the grant.
        [Fact]
        public async Task CreateReservationAsync_WhenCounterKeyAbsentAndDecrementGoesNegative_RepairsCounterToDbTruthAndPublishesNonNegative()
        {
            // Arrange — capacity 10, no prior holds; simulate a cache-miss DECR landing below zero.
            var products = await TestStoreContextFactory.SeedProductsAsync(_context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            _mockDb.Setup(d => d.StringDecrementAsync(
                    It.Is<RedisKey>(k => k == $"stock:product:{productId}"), It.IsAny<long>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(-2L); // absent key: Redis treats as 0, then 0 - 2 => -2

            RedisValue publishedPayload = default;
            _mockSub.Setup(s => s.PublishAsync(
                    It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Callback<RedisChannel, RedisValue, CommandFlags>((ch, val, flags) => publishedPayload = val)
                .ReturnsAsync(0L);

            // Act — reserve 2 of 10; the reservation persists first, then the Redis mirror is decremented.
            var result = await _sut.CreateReservationAsync("basket-repair", productId, 2);

            // Assert — the reservation still succeeds (the row lock is the authority, not the counter).
            result.Should().BeTrue();
            var reservation = await _context.Reservations
                .FirstOrDefaultAsync(r => r.BasketId == "basket-repair");
            reservation.Should().NotBeNull();
            reservation.Quantity.Should().Be(2);
            reservation.Status.Should().Be(ReservationStatus.Active);

            // The negative DECR triggered a reseed of the SAME stock counter key to the DB-authoritative
            // available (capacity 10 - the 2 just reserved = 8), via an idempotent SET.
            _mockDb.Verify(d => d.StringSetAsync(
                It.Is<RedisKey>(k => k == $"stock:product:{productId}"),
                It.Is<RedisValue>(v => (long)v == 8),
                It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()),
                Times.Once);

            // The published value is the repaired, non-negative truth (8) — never the transient -2.
            publishedPayload.HasValue.Should().BeTrue();
            using var doc = JsonDocument.Parse(publishedPayload.ToString());
            doc.RootElement.GetProperty("currentStock").GetInt64().Should().Be(8);
        }

    }
}
