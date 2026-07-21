using System;
using System.Linq;
using System.Threading.Tasks;
using Core.Entities;
using Infrastructure.Data;
using Infrastructure.Services;
using Infrastructure.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Infrastructure.Tests.Services
{
    /// <summary>
    /// Unit tests for <see cref="FlashSaleService"/> — active-sale resolution by time window and
    /// time-driven status advancement. Pure LINQ over an EF Core InMemory StoreContext (no Redis,
    /// no transactions), so the SUT is constructed directly. Fresh context + SUT per test.
    /// </summary>
    public class FlashSaleServiceTests
    {
        private readonly StoreContext _context;
        private readonly FlashSaleService _sut;

        public FlashSaleServiceTests()
        {
            _context = TestStoreContextFactory.CreateInMemoryContext(Guid.NewGuid().ToString());
            _sut = new FlashSaleService(_context);
        }

        [Fact]
        public async Task GetActiveFlashSaleForProductAsync_WhenActiveAndWithinWindow_ReturnsSale()
        {
            // Arrange
            var now = DateTimeOffset.UtcNow;
            var seeded = await TestStoreContextFactory.SeedFlashSalesAsync(
                _context, productId: 1, saleStockQuantity: 50,
                startsAt: now.AddMinutes(-5), endsAt: now.AddMinutes(30),
                status: FlashSaleStatus.Active);

            // Act
            var result = await _sut.GetActiveFlashSaleForProductAsync(1);

            // Assert
            result.Should().NotBeNull();
            result.Id.Should().Be(seeded[0].Id);
            result.ProductId.Should().Be(1);
            result.Status.Should().Be(FlashSaleStatus.Active);
        }

        [Fact]
        public async Task GetActiveFlashSaleForProductAsync_WhenNotYetStarted_ReturnsNull()
        {
            // Arrange
            var now = DateTimeOffset.UtcNow;
            await TestStoreContextFactory.SeedFlashSalesAsync(
                _context, productId: 1, saleStockQuantity: 50,
                startsAt: now.AddMinutes(10), endsAt: now.AddMinutes(60),
                status: FlashSaleStatus.Active);

            // Act
            var result = await _sut.GetActiveFlashSaleForProductAsync(1);

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task GetActiveFlashSaleForProductAsync_WhenWindowAlreadyEnded_ReturnsNull()
        {
            // Arrange — end boundary is EXCLUSIVE (now >= EndsAt => not active)
            var now = DateTimeOffset.UtcNow;
            await TestStoreContextFactory.SeedFlashSalesAsync(
                _context, productId: 1, saleStockQuantity: 50,
                startsAt: now.AddMinutes(-60), endsAt: now.AddMinutes(-1),
                status: FlashSaleStatus.Active);

            // Act
            var result = await _sut.GetActiveFlashSaleForProductAsync(1);

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task GetActiveFlashSaleForProductAsync_WhenStatusNotActiveButWindowMatches_ReturnsNull()
        {
            // Arrange — window is valid but status is Scheduled, so it must not be treated as active
            var now = DateTimeOffset.UtcNow;
            await TestStoreContextFactory.SeedFlashSalesAsync(
                _context, productId: 1, saleStockQuantity: 50,
                startsAt: now.AddMinutes(-5), endsAt: now.AddMinutes(30),
                status: FlashSaleStatus.Scheduled);

            // Act
            var result = await _sut.GetActiveFlashSaleForProductAsync(1);

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task GetActiveFlashSaleForProductAsync_WhenActiveSaleBelongsToDifferentProduct_ReturnsNull()
        {
            // Arrange — an in-window Active sale exists, but for product 2, not product 1
            var now = DateTimeOffset.UtcNow;
            await TestStoreContextFactory.SeedFlashSalesAsync(
                _context, productId: 2, saleStockQuantity: 50,
                startsAt: now.AddMinutes(-5), endsAt: now.AddMinutes(30),
                status: FlashSaleStatus.Active);

            // Act
            var result = await _sut.GetActiveFlashSaleForProductAsync(1);

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task AdvanceFlashSaleStatusesAsync_WhenCalled_TransitionsStatusesByTime()
        {
            // Arrange
            var now = DateTimeOffset.UtcNow;
            // Scheduled + now inside window => should become Active
            var toActivate = await TestStoreContextFactory.SeedFlashSalesAsync(
                _context, productId: 1, saleStockQuantity: 10,
                startsAt: now.AddMinutes(-1), endsAt: now.AddMinutes(30),
                status: FlashSaleStatus.Scheduled);
            // Active + now past end => should become Ended
            var toEnd = await TestStoreContextFactory.SeedFlashSalesAsync(
                _context, productId: 2, saleStockQuantity: 10,
                startsAt: now.AddMinutes(-60), endsAt: now.AddMinutes(-1),
                status: FlashSaleStatus.Active);
            // Scheduled + now before start => should stay Scheduled
            var toStay = await TestStoreContextFactory.SeedFlashSalesAsync(
                _context, productId: 3, saleStockQuantity: 10,
                startsAt: now.AddMinutes(10), endsAt: now.AddMinutes(60),
                status: FlashSaleStatus.Scheduled);

            // Act
            await _sut.AdvanceFlashSaleStatusesAsync();

            // Assert — re-query the persisted rows
            var activated = await _context.FlashSales.FindAsync(toActivate[0].Id);
            var ended = await _context.FlashSales.FindAsync(toEnd[0].Id);
            var stayed = await _context.FlashSales.FindAsync(toStay[0].Id);

            activated.Status.Should().Be(FlashSaleStatus.Active);
            ended.Status.Should().Be(FlashSaleStatus.Ended);
            stayed.Status.Should().Be(FlashSaleStatus.Scheduled);
        }

        [Fact]
        public async Task AdvanceFlashSaleStatusesAsync_WhenActiveSaleStillWithinWindow_RemainsActive()
        {
            // Arrange — an already-Active sale whose window has NOT yet ended must stay Active.
            // This exercises the status-advance guard where sale.Status == Scheduled is FALSE (a
            // non-Scheduled, non-Ended sale reaching the else-if short-circuit), which the combined
            // transition test above does not cover on its own.
            var now = DateTimeOffset.UtcNow;
            var seeded = await TestStoreContextFactory.SeedFlashSalesAsync(
                _context, productId: 4, saleStockQuantity: 10,
                startsAt: now.AddMinutes(-5), endsAt: now.AddMinutes(30),
                status: FlashSaleStatus.Active);

            // Act
            await _sut.AdvanceFlashSaleStatusesAsync();

            // Assert — the in-window Active sale is unchanged.
            var unchanged = await _context.FlashSales.FindAsync(seeded[0].Id);
            unchanged.Status.Should().Be(FlashSaleStatus.Active);
        }

    }
}
