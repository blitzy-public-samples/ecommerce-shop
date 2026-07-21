using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using FluentAssertions;
using Infrastructure.Data;
using Infrastructure.Services;
using Infrastructure.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.Tests.Services
{
    /// <summary>
    /// Unit tests for <see cref="FlashSaleService"/> (Real-Time Inventory &amp; Flash Sale feature).
    ///
    /// Strategy:
    /// <list type="bullet">
    ///   <item>
    ///     Data comes exclusively from an EF Core InMemory <see cref="StoreContext"/> built by
    ///     <see cref="TestStoreContextFactory.CreateInMemoryContext(string)"/> with a unique GUID database
    ///     name per test, so every fact is fully isolated and safe to run in parallel. InMemory preserves
    ///     exact decimals, so monetary assertions use the <c>m</c> suffix and exact equality.
    ///   </item>
    ///   <item>
    ///     The real-time seam <see cref="IInventoryBroadcastCoordinator"/> is mocked with Moq. Moq auto-returns a
    ///     completed <see cref="Task"/> for the async publication methods, so no explicit <c>.Returns</c> setup
    ///     is required; we assert purely on invocation (<see cref="Times.Once"/> / <see cref="Times.Never"/>).
    ///     The coordinator receives a compute delegate (re-read after commit); the availability it publishes is
    ///     verified separately through <see cref="FlashSaleService.GetActiveSalesAsync"/>, whose returned value is
    ///     computed by the same authoritative, sale-scoped formula.
    ///   </item>
    /// </list>
    ///
    /// Behavior under test (authoritative, per <see cref="FlashSaleService"/>):
    /// <list type="bullet">
    ///   <item>
    ///     <c>ScheduleAsync</c> is a trust boundary: it validates product existence and that the sale price is a
    ///     genuine discount below the base price, persists a <see cref="FlashSale"/>, and — only when the window
    ///     already contains <c>UtcNow</c> — publishes <c>FlashSaleStarted</c> through the coordinator.
    ///   </item>
    ///   <item>
    ///     <c>GetActiveSalesAsync</c> returns only sales whose window currently contains <c>UtcNow</c>, each
    ///     paired with a SALE-SCOPED <c>QuantityAvailable</c> equal to
    ///     <c>StockAllocation − SUM(Quantity WHERE FlashSaleId = that sale AND (Consumed OR (Active AND
    ///     ExpiresAt &gt; now)))</c>, clamped at zero. Expired and released holds are ignored.
    ///   </item>
    /// </list>
    /// </summary>
    public class FlashSaleServiceTests
    {
        // Fresh mocks per test: xUnit re-instantiates the test class for every [Fact], so these fields are
        // brand-new each time — no shared, mutable state leaks across tests.
        private readonly Mock<IInventoryBroadcastCoordinator> _coordinator = new Mock<IInventoryBroadcastCoordinator>();
        private readonly Mock<ILogger<FlashSaleService>> _logger = new Mock<ILogger<FlashSaleService>>();

        private FlashSaleService CreateSut(StoreContext context) =>
            new FlashSaleService(context, _coordinator.Object, _logger.Object);

        // Small factory for a FlashSale aggregate added directly to the store (bypassing ScheduleAsync's
        // validation) for GetActiveSalesAsync tests. Id and Version are assigned by the store on save.
        private static FlashSale MakeSale(int productId, DateTimeOffset startAt, DateTimeOffset endAt,
            decimal salePrice, int stockAllocation) =>
            new FlashSale
            {
                ProductId = productId,
                StartAt = startAt,
                EndAt = endAt,
                SalePrice = salePrice,
                StockAllocation = stockAllocation
            };

        [Fact]
        public async Task ScheduleAsync_WhenValidAndActiveNow_PersistsSuccessAndBroadcastsFlashSaleStarted()
        {
            // Arrange — seed product 1 (base Price = 10) so the sale price 9.99 is a genuine discount.
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            var products = await TestStoreContextFactory.SeedProductsAsync(context, 1);
            var productId = products[0].Id;
            var sut = CreateSut(context);

            // Act
            var result = await sut.ScheduleAsync(productId,
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), 9.99m, 100);

            // Assert — deterministic Success outcome carrying the persisted sale.
            result.Should().NotBeNull();
            result.Outcome.Should().Be(FlashSaleScheduleOutcome.Success);
            result.FlashSale.Should().NotBeNull();
            result.FlashSale.Id.Should().BeGreaterThan(0);
            result.FlashSale.ProductId.Should().Be(productId);
            result.FlashSale.SalePrice.Should().Be(9.99m);
            result.FlashSale.StockAllocation.Should().Be(100);
            context.FlashSales.Count(s => s.Id == result.FlashSale.Id).Should().Be(1);

            // A live sale announces itself exactly once through the coordinator (after commit).
            _coordinator.Verify(c => c.PublishFlashSaleStartedAsync(
                It.Is<FlashSale>(s => s.Id == result.FlashSale.Id), It.IsAny<Func<Task<int>>>()), Times.Once);
        }

        [Fact]
        public async Task ScheduleAsync_WhenScheduledForFuture_PersistsButDoesNotBroadcast()
        {
            // Arrange
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            var products = await TestStoreContextFactory.SeedProductsAsync(context, 1);
            var productId = products[0].Id;
            var sut = CreateSut(context);

            // Act — window is entirely in the future.
            var result = await sut.ScheduleAsync(productId,
                DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(2), 9.99m, 100);

            // Assert — persisted, but a not-yet-active sale must NOT broadcast a started event.
            result.Outcome.Should().Be(FlashSaleScheduleOutcome.Success);
            result.FlashSale.Id.Should().BeGreaterThan(0);
            context.FlashSales.Count(s => s.Id == result.FlashSale.Id).Should().Be(1);
            _coordinator.Verify(c => c.PublishFlashSaleStartedAsync(
                It.IsAny<FlashSale>(), It.IsAny<Func<Task<int>>>()), Times.Never);
        }

        [Fact]
        public async Task ScheduleAsync_WhenProductDoesNotExist_ReturnsProductNotFoundAndPersistsNothing()
        {
            // Arrange — no products seeded.
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            var sut = CreateSut(context);

            // Act
            var result = await sut.ScheduleAsync(999,
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), 9.99m, 100);

            // Assert — trust-boundary rejection, no authority row persisted, no broadcast.
            result.Outcome.Should().Be(FlashSaleScheduleOutcome.ProductNotFound);
            result.FlashSale.Should().BeNull();
            context.FlashSales.Should().BeEmpty();
            _coordinator.Verify(c => c.PublishFlashSaleStartedAsync(
                It.IsAny<FlashSale>(), It.IsAny<Func<Task<int>>>()), Times.Never);
        }

        [Fact]
        public async Task ScheduleAsync_WhenSalePriceNotBelowBasePrice_ReturnsSalePriceNotBelowBasePrice()
        {
            // Arrange — product 1 base Price = 10; a "discount" that is not below base must be rejected.
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            var products = await TestStoreContextFactory.SeedProductsAsync(context, 1);
            var productId = products[0].Id;
            var sut = CreateSut(context);

            // Act — sale price equals the base price (10), i.e. not a genuine discount.
            var result = await sut.ScheduleAsync(productId,
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), 10m, 100);

            // Assert
            result.Outcome.Should().Be(FlashSaleScheduleOutcome.SalePriceNotBelowBasePrice);
            result.FlashSale.Should().BeNull();
            context.FlashSales.Should().BeEmpty();
        }

        [Fact]
        public async Task GetActiveSalesAsync_WhenSalesActiveInactiveAndFuture_ReturnsOnlyActive()
        {
            // Arrange
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            var active = MakeSale(1, DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(10), 5m, 50);
            var past = MakeSale(2, DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1), 5m, 50);
            var future = MakeSale(3, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(2), 5m, 50);
            context.FlashSales.AddRange(active, past, future);
            await context.SaveChangesAsync();
            var sut = CreateSut(context);

            // Act
            var result = await sut.GetActiveSalesAsync();

            // Assert
            result.Should().ContainSingle();
            result.Single().Sale.Id.Should().Be(active.Id);
        }

        [Fact]
        public async Task GetActiveSalesAsync_WhenActiveSaleHasMixedReservations_ComputesQuantityAvailableIgnoringExpired()
        {
            // Arrange — a single active sale with a live hold (counts) and an expired hold (ignored). Availability
            // is SALE-SCOPED, so the reservations reference the sale via FlashSaleId.
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            var active = MakeSale(1, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), 5m, 100);
            context.FlashSales.Add(active);
            await context.SaveChangesAsync();
            context.InventoryReservations.AddRange(
                new InventoryReservation
                {
                    FlashSaleId = active.Id, ProductId = 1, Quantity = 40, SessionId = "a",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5), Status = ReservationStatus.Active
                },
                new InventoryReservation
                {
                    FlashSaleId = active.Id, ProductId = 1, Quantity = 50, SessionId = "b",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1), Status = ReservationStatus.Active // expired
                });
            await context.SaveChangesAsync();
            var sut = CreateSut(context);

            // Act
            var result = await sut.GetActiveSalesAsync();

            // Assert — 100 − 40 live = 60; the expired 50 is ignored.
            result.Should().ContainSingle();
            result.Single().QuantityAvailable.Should().Be(60);
        }

        [Fact]
        public async Task GetActiveSalesAsync_WhenReservationsExceedAllocation_ClampsQuantityAvailableToZero()
        {
            // Arrange
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            var active = MakeSale(1, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), 5m, 10);
            context.FlashSales.Add(active);
            await context.SaveChangesAsync();
            context.InventoryReservations.Add(new InventoryReservation
            {
                FlashSaleId = active.Id, ProductId = 1, Quantity = 25, SessionId = "a",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5), Status = ReservationStatus.Active
            });
            await context.SaveChangesAsync();
            var sut = CreateSut(context);

            // Act
            var result = await sut.GetActiveSalesAsync();

            // Assert — never negative.
            result.Single().QuantityAvailable.Should().Be(0);
        }

        [Fact]
        public async Task GetActiveSalesAsync_WhenNoSales_ReturnsEmpty()
        {
            // Arrange
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            var sut = CreateSut(context);

            // Act
            var result = await sut.GetActiveSalesAsync();

            // Assert
            result.Should().BeEmpty();
        }
    }
}
