using System;
using System.Reflection;
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
using Xunit;

namespace Infrastructure.Tests.Services
{
    /// <summary>
    /// Unit tests for <see cref="StockReconciliationService"/> — a single reconciliation pass driven
    /// through the IHostedService lifecycle. After P7-1 this service ORCHESTRATES: it performs NO direct
    /// Redis I/O and no direct reservation writes, delegating the reclaim and the counter reseed to the
    /// sole stock writer (IInventoryService). These tests therefore verify the ORCHESTRATION contract
    /// (reclaim + advance flash sales + reseed are each invoked per pass) via a mocked IServiceScopeFactory
    /// chain and mocked IInventoryService/IFlashSaleService; the reclaim's internal effects (Active ->
    /// Expired, INCR, publish, hold-key cleanup) are unit-tested in InventoryServiceTests. A
    /// TaskCompletionSource gates on the pass's last step so tests are deterministic and fast.
    /// </summary>
    public class StockReconciliationServiceTests
    {
        private readonly Mock<IInventoryService> _mockInventory;
        private readonly Mock<IFlashSaleService> _mockFlashSale;
        private readonly Mock<IConfiguration> _mockConfig;

        public StockReconciliationServiceTests()
        {
            _mockInventory = new Mock<IInventoryService>();
            _mockFlashSale = new Mock<IFlashSaleService>();
            _mockFlashSale.Setup(f => f.AdvanceFlashSaleStatusesAsync()).Returns(Task.CompletedTask);

            // After P7-1 the reclaim (Active -> Expired + INCR + publish + hold-key cleanup) lives behind the
            // sole stock writer; this service merely ORCHESTRATES it. Stub the delegated reclaim so the pass
            // completes; its internal Redis/DB effects are covered by InventoryServiceTests. No Redis mock is
            // wired here because the service no longer injects or touches an IConnectionMultiplexer.
            _mockInventory.Setup(i => i.ReclaimExpiredReservationsAsync()).Returns(Task.CompletedTask);

            _mockConfig = new Mock<IConfiguration>();
            // Large interval => exactly one pass runs before the loop blocks on Task.Delay.
            _mockConfig.Setup(c => c["Inventory:ReconciliationIntervalSeconds"]).Returns("30");
        }

        // Builds the SUT wired to the given context, returning a TCS that completes at the pass's last step.
        private (StockReconciliationService Sut, TaskCompletionSource<bool> PassDone) CreateService(StoreContext context)
        {
            var provider = new Mock<IServiceProvider>();
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

            var sut = new StockReconciliationService(scopeFactory.Object, _mockConfig.Object, null);
            return (sut, passDone);
        }

        private static async Task RunOnePassAsync(StockReconciliationService sut, TaskCompletionSource<bool> passDone)
        {
            await sut.StartAsync(CancellationToken.None);
            await Task.WhenAny(passDone.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            await sut.StopAsync(CancellationToken.None);
        }

        // 1. Each pass DELEGATES expired-reservation reclaim to the sole stock writer (P7-1). The reclaim
        // itself — Active -> Expired, returning held stock to the counters, publishing the corrected value,
        // and residual hold-key cleanup — now lives in InventoryService.ReclaimExpiredReservationsAsync and
        // is unit-tested there (see InventoryServiceTests, which covers both the expired-reclaim and the
        // non-expired-left-Active behaviors that this class previously asserted). This service must merely
        // INVOKE it each pass; it no longer writes the Reservations table or the Redis stock keys directly
        // (the sole-writer invariant). Verifying the delegated call is the orchestration counterpart.
        [Fact]
        public async Task ExecuteAsync_WhenPassRuns_DelegatesExpiredReclaimToInventoryService()
        {
            // Arrange
            var context = TestStoreContextFactory.CreateInMemoryContext(Guid.NewGuid().ToString());
            var (sut, passDone) = CreateService(context);

            // Act
            await RunOnePassAsync(sut, passDone);

            // Assert: the reclaim was delegated to the sole writer, not performed directly by this service.
            _mockInventory.Verify(i => i.ReclaimExpiredReservationsAsync(), Times.AtLeastOnce);
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

        // ---------------------------------------------------------------------------------------------
        // QA Issue #3 (MAJOR): the configured Inventory:ReconciliationIntervalSeconds MUST be clamped to
        // a safe range so an extreme operator value can never overflow Int32 milliseconds inside
        // TimeSpan.FromSeconds -> Task.Delay (which threw ArgumentOutOfRangeException and silently killed
        // the loop after one pass). The effective interval is a private property; asserting it directly
        // via reflection is a deterministic, fast unit test of the clamp itself (no timing dependency).
        // ---------------------------------------------------------------------------------------------
        [Theory]
        [InlineData("2147483647", 86400)] // int.MaxValue (the extreme value from the QA repro) -> upper clamp
        [InlineData("2147484", 86400)]    // just past Task.Delay's overflow boundary -> upper clamp
        [InlineData("100000", 86400)]     // above the max -> upper clamp
        [InlineData("86400", 86400)]      // exactly the max -> unchanged
        [InlineData("30", 30)]            // normal default -> unchanged
        [InlineData("5", 5)]              // normal override -> unchanged
        [InlineData("1", 1)]              // exactly the min -> unchanged
        [InlineData("0", 1)]              // zero -> lower clamp
        [InlineData("-5", 1)]             // negative -> lower clamp
        [InlineData("abc", 30)]           // unparseable -> default
        [InlineData("", 30)]              // empty -> default
        [InlineData(null, 30)]            // missing key -> default
        public void ReconciliationIntervalSeconds_IsClampedToSafeRange(string configured, int expected)
        {
            // Arrange: a config that returns the case's raw value for the interval key.
            var config = new Mock<IConfiguration>();
            config.Setup(c => c["Inventory:ReconciliationIntervalSeconds"]).Returns(configured);

            var scopeFactory = new Mock<IServiceScopeFactory>();
            var sut = new StockReconciliationService(
                scopeFactory.Object, config.Object, null);

            // Act: read the private effective-interval property.
            var prop = typeof(StockReconciliationService).GetProperty(
                "ReconciliationIntervalSeconds", BindingFlags.Instance | BindingFlags.NonPublic);
            prop.Should().NotBeNull("the clamp is applied by the ReconciliationIntervalSeconds getter");
            var actual = (int)prop.GetValue(sut);

            // Assert: always within the safe [1, 86400] range and equal to the expected clamped value.
            actual.Should().Be(expected);
            actual.Should().BeInRange(1, 86400);

            // And the safety guarantee itself: TimeSpan.FromSeconds(effective) never overflows Task.Delay
            // (Int32.MaxValue ms), i.e. constructing the delay does not throw.
            Action buildDelay = () => Task.Delay(TimeSpan.FromSeconds(actual), new CancellationTokenSource().Token);
            buildDelay.Should().NotThrow<ArgumentOutOfRangeException>();
        }

        // 6. QA Issue #3 behavioural guard: even with the extreme int.MaxValue interval, a pass still runs
        // AND the loop does not fault — StopAsync completes cleanly (before the fix the loop silently died).
        [Fact]
        public async Task ExecuteAsync_WithExtremeInterval_RunsAPassAndStaysAlive()
        {
            // Arrange
            var context = TestStoreContextFactory.CreateInMemoryContext(Guid.NewGuid().ToString());
            var (sut, passDone) = CreateService(context);
            // Override the config to the extreme value that previously killed the loop.
            _mockConfig.Setup(c => c["Inventory:ReconciliationIntervalSeconds"]).Returns("2147483647");

            // Act
            await sut.StartAsync(CancellationToken.None);
            var passRan = await Task.WhenAny(passDone.Task, Task.Delay(TimeSpan.FromSeconds(5))) == passDone.Task;
            Func<Task> stop = async () => await sut.StopAsync(CancellationToken.None);

            // Assert: the pass ran (SeedStockCountersAsync fired) and shutdown is clean (no faulted loop).
            passRan.Should().BeTrue("a reconciliation pass must complete even with an extreme interval");
            await stop.Should().NotThrowAsync();
        }

        // 7. QA Issue #6 (MINOR, observability): a positive hosted-service startup confirmation is logged
        // at Information level so an operator can tell from the logs alone that the loop began.
        [Fact]
        public async Task ExecuteAsync_WhenStarted_LogsInformationStartupConfirmation()
        {
            // Arrange: a mock logger so the startup Information line can be verified.
            var context = TestStoreContextFactory.CreateInMemoryContext(Guid.NewGuid().ToString());
            var mockLogger = new Mock<ILogger<StockReconciliationService>>();

            var provider = new Mock<IServiceProvider>();
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

            var sut = new StockReconciliationService(
                scopeFactory.Object, _mockConfig.Object, mockLogger.Object);

            // Act
            await sut.StartAsync(CancellationToken.None);
            await Task.WhenAny(passDone.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            await sut.StopAsync(CancellationToken.None);

            // Assert: an Information log containing the startup confirmation was emitted.
            mockLogger.Verify(
                l => l.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, _) => v.ToString().Contains("StockReconciliationService started")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.AtLeastOnce);
        }
    }
}
