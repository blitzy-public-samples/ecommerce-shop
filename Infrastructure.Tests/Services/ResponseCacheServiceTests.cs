using System;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Infrastructure.Services;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Infrastructure.Tests.Services
{
    public class ResponseCacheServiceTests
    {
        // Named DTO (not anonymous) so the camelCase-serialized keys are stable and readable.
        private class SampleResponse
        {
            public int ProductId { get; set; }
            public string ProductName { get; set; }
        }

        // Fresh mocks per test => parallel-safe, no shared mutable state.
        // GetDatabase() must be wired BEFORE the SUT is constructed (the ctor calls it).
        private static (ResponseCacheService Sut, Mock<IDatabase> Db) CreateSut()
        {
            var db = new Mock<IDatabase>();
            var multiplexer = new Mock<IConnectionMultiplexer>();
            multiplexer
                .Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
                .Returns(db.Object);
            return (new ResponseCacheService(multiplexer.Object), db);
        }

        private static string CamelCaseJson(object value) =>
            JsonSerializer.Serialize(
                value,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        [Fact]
        public async Task CacheResponseAsync_WhenResponseNotNull_StoresCamelCaseJsonWithSuppliedTtl()
        {
            // Arrange
            var (sut, db) = CreateSut();
            const string key = "products|brand=1|type=2";
            var response = new SampleResponse { ProductId = 1, ProductName = "Test Product" };
            var ttl = TimeSpan.FromSeconds(600);
            var expectedJson = CamelCaseJson(response); // {"productId":1,"productName":"Test Product"}
            db.Setup(d => d.StringSetAsync(
                    It.IsAny<RedisKey>(),
                    It.IsAny<RedisValue>(),
                    It.IsAny<TimeSpan?>(),
                    It.IsAny<When>(),
                    It.IsAny<CommandFlags>()))
                .ReturnsAsync(true);

            // Act
            await sut.CacheResponseAsync(key, response, ttl);

            // Assert
            db.Verify(d => d.StringSetAsync(
                    It.Is<RedisKey>(k => k == key),
                    It.Is<RedisValue>(v => v == expectedJson),
                    It.Is<TimeSpan?>(t => t == ttl),
                    It.IsAny<When>(),
                    It.IsAny<CommandFlags>()),
                Times.Once);
            expectedJson.Should().Contain("productId").And.Contain("productName");
        }

        [Fact]
        public async Task CacheResponseAsync_WhenResponseNull_DoesNotCallStringSet()
        {
            // Arrange
            var (sut, db) = CreateSut();

            // Act
            await sut.CacheResponseAsync("any-key", null, TimeSpan.FromSeconds(600));

            // Assert
            db.Verify(d => d.StringSetAsync(
                    It.IsAny<RedisKey>(),
                    It.IsAny<RedisValue>(),
                    It.IsAny<TimeSpan?>(),
                    It.IsAny<When>(),
                    It.IsAny<CommandFlags>()),
                Times.Never);
        }

        [Fact]
        public async Task GetCachedResponseAsync_WhenKeyExists_ReturnsCachedJsonString()
        {
            // Arrange
            var (sut, db) = CreateSut();
            const string key = "products|brand=1|type=2";
            var cachedJson = CamelCaseJson(new SampleResponse { ProductId = 1, ProductName = "Test Product" });
            db.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisValue)cachedJson);

            // Act
            var result = await sut.GetCachedResponseAsync(key);

            // Assert
            result.Should().Be(cachedJson);
        }

        [Fact]
        public async Task GetCachedResponseAsync_WhenKeyMissing_ReturnsNull()
        {
            // Arrange
            var (sut, db) = CreateSut();
            db.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(RedisValue.Null);

            // Act
            var result = await sut.GetCachedResponseAsync("missing-key");

            // Assert
            result.Should().BeNull();
        }
    }
}
