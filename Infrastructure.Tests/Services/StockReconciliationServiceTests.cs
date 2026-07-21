using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using Infrastructure.Data;
using Infrastructure.Services;
using Infrastructure.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Infrastructure.Tests.Services
{
    /// <summary>
    /// Unit tests for <see cref="StockReconciliationService"/> — a single reconciliation pass driven
    /// through the IHostedService lifecycle. Uses a mocked IServiceScopeFactory chain, mocked Redis,
    /// mocked IInventoryService/IFlashSaleService, and a real EF InMemory StoreContext for reservation
    /// state. A TaskCompletionSource gates on the pass's last step so tests are deterministic and fast.
    /// </summary>
    public class StockReconciliationServiceTests
    {
        private readonly Mock<IInventoryService> _mockInventory;
        private readonly Mock<IFlashSaleService> _mockFlashSale;
        private readonly Mock<IConnectionMultiplexer> _mockMux;
        private readonly Mock<IDatabase> _mockDb;
        private readonly Mock<ISubscriber> _mockSub;
        private readonly Mock<IConfiguration> _mockConfig;

        public StockReconciliationServiceTests()
        {
            _mockInventory = new Mock<IInventoryService>();
            _mockFlashSale = new Mock<IFlashSaleService>();
            _mockFlashSale.Setup(f => f.AdvanceFlashSaleStatusesAsync()).Returns(Task.CompletedTask);

            _mockDb = new Mock<IDatabase>();
            _mockSub = new Mock<ISubscriber>();
            _mockMux = new Mock<IConnectionMultiplexer>();
            _mockMux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_mockDb.Object);
            _mockMux.Setup(m => m.GetSubscriber(It.IsAny<object>())).Returns(_mockSub.Object);
            _mockDb.Setup(d => d.StringIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(0L);
            _mockSub.Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(0L);

            _mockConfig = new Mock<IConfiguration>();
            // Large interval => exactly one pass runs before the loop blocks on Task.Delay.
            _mockConfig.Setup(c => c["Inventory:ReconciliationIntervalSeconds"]).Returns("30");
        }

        // Builds the SUT wired to the given context, returning a TCS that completes at the pass's last step.
        private (StockReconciliationService Sut, TaskCompletionSource<bool> PassDone) CreateService(StoreContext context)
        {
            var provider = new Mock<IServiceProvider>();
            provider.Setup(p => p.GetService(typeof(StoreContext))).Returns(context);
            provider.Setup(p => p.GetService(typeof(IInventoryService))).Returns(_mockInventory.Object);
            provider.Setup(p => p.GetService(typeof(IFlashSaleService))).Returns(_mockFlashSale.Object);

            var scope = new Mock<IServiceScope>();
            scope.Setup(s => s.ServiceProvider).Returns(provider.Object);

            var scopeFactory = new Mock<IServiceScopeFactory>();
            scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

            var passDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mockInventory.Setup(i => i.SeedStockCountersAsync())
                .Returns(Task.CompletedTask)
                .Callback(() => passDone.TrySetResult(true));

            var sut = new StockReconciliationService(scopeFactory.Object, _mockConfig.Object, _mockMux.Object, null);
            return (sut, passDone);
        }

        private static async Task RunOnePassAsync(StockReconciliationService sut, TaskCompletionSource<bool> passDone)
        {
            await sut.StartAsync(CancellationToken.None);
            await Task.WhenAny(passDone.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            await sut.StopAsync(CancellationToken.None);
        }

        // 1. Reclaims expired reservations and frees their stock.
        [Fact]
        public async Task ExecuteAsync_WhenReservationExpired_MarksExpiredAndIncrementsCounter()
        {
            // Arrange
            var context = TestStoreContextFactory.CreateInMemoryContext(Guid.NewGuid().ToString());
            var products = await TestStoreContextFactory.SeedProductsAsync(context, count: 1, stockQuantity: 10);
            var productId = products[0].Id;
            var reservations = await TestStoreContextFactory.SeedReservationsAsync(
                context, productId, quantity: 3, status: ReservationStatus.Active,
                expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
            var (sut, passDone) = CreateService(context);

            // Act
            await RunOnePassAsync(sut, passDone);

            // Assert
            var reclaimed = await context.Reservations.FindAsync(reservations[0].Id);
            reclaimed.Status.Should().Be(ReservationStatus.Expired);
            _mockDb.Verify(d => d.StringIncrementAsync(
                It.Is<RedisKey>(k => k == $"stock:product:{productId}"), 3, It.IsAny<CommandFlags>()),
                Times.AtLeastOnce);
            _mockSub.Verify(s => s.PublishAsync(
                It.Is<RedisChannel>(c => c == "stock-updates"), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()),
                Times.AtLeastOnce);
        }

        // 2. Leaves non-expired reservations untouched.
        [Fact]
        public async Task ExecuteAsync_WhenReservationNotExpired_LeavesReservationActive()
        {
            // Arrange
            var context = TestStoreContextFactory.CreateInMemoryContext(Guid.NewGuid().ToString());
            var products = await TestStoreContextFactory.SeedProductsAsync(context, count: 1, stockQuantity: 10);
            var reservations = await TestStoreContextFactory.SeedReservationsAsync(
                context, products[0].Id, quantity: 3, status: ReservationStatus.Active,
                expiresAt: DateTimeOffset.UtcNow.AddMinutes(10));
            var (sut, passDone) = CreateService(context);

            // Act
            await RunOnePassAsync(sut, passDone);

            // Assert
            var untouched = await context.Reservations.FindAsync(reservations[0].Id);
            untouched.Status.Should().Be(ReservationStatus.Active);
        }

        // 3. Advances flash-sale statuses each pass.
        [Fact]
        public async Task ExecuteAsync_WhenPassRuns_AdvancesFlashSaleStatuses()
        {
            // Arrange
            var context = TestStoreContextFactory.CreateInMemoryContext(Guid.NewGuid().ToString());
            var (sut, passDone) = CreateService(context);

            // Act
            await RunOnePassAsync(sut, passDone);

            // Assert
            _mockFlashSale.Verify(f => f.AdvanceFlashSaleStatusesAsync(), Times.AtLeastOnce);
        }

        // 4. Reseeds Redis counters each pass.
        [Fact]
        public async Task ExecuteAsync_WhenPassRuns_ReseedsStockCounters()
        {
            // Arrange
            var context = TestStoreContextFactory.CreateInMemoryContext(Guid.NewGuid().ToString());
            var (sut, passDone) = CreateService(context);

            // Act
            await RunOnePassAsync(sut, passDone);

            // Assert
            _mockInventory.Verify(i => i.SeedStockCountersAsync(), Times.AtLeastOnce);
        }

        // 5. Clean cancellation via StopAsync.
        [Fact]
        public async Task StopAsync_WhenServiceRunning_CompletesWithoutThrowing()
        {
            // Arrange
            var context = TestStoreContextFactory.CreateInMemoryContext(Guid.NewGuid().ToString());
            var (sut, passDone) = CreateService(context);
            await sut.StartAsync(CancellationToken.None);
            await Task.WhenAny(passDone.Task, Task.Delay(TimeSpan.FromSeconds(5)));

            // Act
            Func<Task> act = async () => await sut.StopAsync(CancellationToken.None);

            // Assert
            await act.Should().NotThrowAsync();
        }
    }
}
