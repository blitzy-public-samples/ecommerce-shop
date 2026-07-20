using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Core.Entities;
using FluentAssertions;
using Infrastructure.Data;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Infrastructure.Tests.Data
{
    /// <summary>
    /// Unit tests for <see cref="BasketRepository"/>.
    ///
    /// The Redis <see cref="IConnectionMultiplexer"/> and its <see cref="IDatabase"/> are mocked with
    /// Moq so these tests exercise the repository logic in complete isolation from a real Redis server
    /// (real Redis is covered by the separate API.IntegrationTests project, per AAP 0.10.1).
    ///
    /// StackExchange.Redis 2.2.62 signatures used here (verified against the assembly):
    ///   - IConnectionMultiplexer.GetDatabase(int db = -1, object asyncState = null)
    ///   - IDatabaseAsync.StringSetAsync(RedisKey, RedisValue, TimeSpan? expiry, When, CommandFlags)  (5-arg overload; no keepTtl)
    ///   - IDatabaseAsync.StringGetAsync(RedisKey, CommandFlags)
    ///   - IDatabaseAsync.KeyDeleteAsync(RedisKey, CommandFlags)
    /// Optional parameters therefore require an explicit matcher per parameter in every Setup/Verify.
    ///
    /// Conventions (AAP 0.10.2): MethodName_StateUnderTest_ExpectedBehavior, Arrange-Act-Assert,
    /// FluentAssertions 6.12.0, and fresh mocks + a fresh SUT per test through the xUnit per-test
    /// constructor so the tests carry no shared mutable state and run safely in parallel.
    /// </summary>
    public class BasketRepositoryTests
    {
        private readonly Mock<IConnectionMultiplexer> _mockMultiplexer;
        private readonly Mock<IDatabase> _mockDatabase;
        private readonly BasketRepository _sut;

        public BasketRepositoryTests()
        {
            // Fresh mocks + SUT per test (xUnit constructs the test class once per test method).
            _mockDatabase = new Mock<IDatabase>();
            _mockMultiplexer = new Mock<IConnectionMultiplexer>();

            // GetDatabase is called inside the BasketRepository constructor, so it must be
            // configured before the SUT is created; otherwise the ctor receives a null IDatabase.
            _mockMultiplexer
                .Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
                .Returns(_mockDatabase.Object);

            _sut = new BasketRepository(_mockMultiplexer.Object);
        }

        /// <summary>
        /// Builds a deterministic <see cref="CustomerBasket"/> with a single item so that the serialized
        /// JSON is stable and can be asserted on directly.
        /// </summary>
        private static CustomerBasket CreateBasket(string id = "basket1") =>
            new CustomerBasket(id)
            {
                Items = new List<BasketItem>
                {
                    new BasketItem
                    {
                        Id = 1,
                        ProductName = "Test Product",
                        Price = 10m,
                        Quantity = 2,
                        PictureUrl = "images/products/test.png",
                        Brand = "Test Brand",
                        Type = "Test Type"
                    }
                }
            };

        [Fact]
        public async Task UpdateBasketAsync_WhenCalled_StoresBasketWith30DayTtl()
        {
            // Arrange
            var basket = CreateBasket();
            var expectedJson = JsonSerializer.Serialize(basket);
            _mockDatabase
                .Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
                    It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(true);
            // Success re-reads the basket via GetBasketAsync, so stub StringGetAsync to avoid an NRE.
            _mockDatabase
                .Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisValue)expectedJson);

            // Act
            await _sut.UpdateBasketAsync(basket);

            // Assert
            _mockDatabase.Verify(d => d.StringSetAsync(
                It.Is<RedisKey>(k => k == basket.Id),
                It.Is<RedisValue>(v => v == expectedJson),
                It.Is<TimeSpan?>(t => t == TimeSpan.FromDays(30)),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()), Times.Once);
        }

        [Fact]
        public async Task UpdateBasketAsync_WhenStoreSucceeds_ReturnsStoredBasket()
        {
            // Arrange
            var basket = CreateBasket();
            var json = JsonSerializer.Serialize(basket);
            _mockDatabase
                .Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
                    It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(true);
            _mockDatabase
                .Setup(d => d.StringGetAsync(It.Is<RedisKey>(k => k == basket.Id), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisValue)json);

            // Act
            var result = await _sut.UpdateBasketAsync(basket);

            // Assert
            result.Should().NotBeNull();
            result.Id.Should().Be("basket1");
            result.Items.Should().HaveCount(1);
            result.Items[0].ProductName.Should().Be("Test Product");
        }

        [Fact]
        public async Task UpdateBasketAsync_WhenStoreFails_ReturnsNull()
        {
            // Arrange
            var basket = CreateBasket();
            _mockDatabase
                .Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
                    It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(false);

            // Act
            var result = await _sut.UpdateBasketAsync(basket);

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task GetBasketAsync_WhenKeyExists_ReturnsDeserializedBasket()
        {
            // Arrange
            var basket = CreateBasket();
            var json = JsonSerializer.Serialize(basket);
            _mockDatabase
                .Setup(d => d.StringGetAsync(It.Is<RedisKey>(k => k == basket.Id), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisValue)json);

            // Act
            var result = await _sut.GetBasketAsync(basket.Id);

            // Assert
            result.Should().NotBeNull();
            result.Id.Should().Be("basket1");
            result.Items.Should().HaveCount(1);
            result.Items[0].ProductName.Should().Be("Test Product");
        }

        [Fact]
        public async Task GetBasketAsync_WhenKeyMissing_ReturnsNull()
        {
            // Arrange
            _mockDatabase
                .Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(RedisValue.Null);

            // Act
            var result = await _sut.GetBasketAsync("missing-basket");

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task DeleteBasketAsync_WhenKeyDeleted_ReturnsTrue()
        {
            // Arrange
            _mockDatabase
                .Setup(d => d.KeyDeleteAsync(It.Is<RedisKey>(k => k == "basket1"), It.IsAny<CommandFlags>()))
                .ReturnsAsync(true);

            // Act
            var result = await _sut.DeleteBasketAsync("basket1");

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public async Task DeleteBasketAsync_WhenKeyMissing_ReturnsFalse()
        {
            // Arrange
            _mockDatabase
                .Setup(d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(false);

            // Act
            var result = await _sut.DeleteBasketAsync("missing-basket");

            // Assert
            result.Should().BeFalse();
        }
    }
}
