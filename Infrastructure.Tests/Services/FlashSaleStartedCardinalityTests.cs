using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using FluentAssertions;
using Infrastructure.Data;
using Infrastructure.Services;
using Infrastructure.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.Tests.Services
{
    /// <summary>
    /// End-to-end regression test for QA Issue 1 (duplicate <c>FlashSaleStarted</c> when a sale is created
    /// already-active). Unlike the sibling service tests that mock the coordinator, this wires the REAL
    /// <see cref="InventoryBroadcastCoordinator"/> across BOTH publishers of the started event — a real
    /// <see cref="FlashSaleService"/> (whose <c>ScheduleAsync</c> announces a created-already-active sale) and a
    /// real <see cref="ReservationExpirySweepService"/> tick (which independently observes the active window) —
    /// exactly as production composes them (a single shared singleton coordinator, per-scope database contexts).
    /// A recording <see cref="IInventoryBroadcaster"/> captures precisely how many <c>FlashSaleStarted</c>
    /// broadcasts actually reached the hub boundary.
    ///
    /// <para>
    /// Before the fix the two publishers each emitted the event (two identical <c>FlashSaleStarted</c> ~one poll
    /// interval apart); after the fix the coordinator dedups atomically per <see cref="FlashSale.Id"/>, so the
    /// sale entering its window emits the event EXACTLY ONCE regardless of which publisher fires first.
    /// </para>
    /// </summary>
    public class FlashSaleStartedCardinalityTests
    {
        private readonly RecordingBroadcaster _broadcaster = new RecordingBroadcaster();

        /// <summary>Recording <see cref="IInventoryBroadcaster"/>: captures the sale/product ids actually broadcast.</summary>
        private class RecordingBroadcaster : IInventoryBroadcaster
        {
            public List<int> StartedSaleIds { get; } = new List<int>();
            public List<int> StartedProductIds { get; } = new List<int>();
            // M9: FlashSaleEnded now carries (productId, saleId); the double records both.
            public List<(int ProductId, int SaleId)> Ended { get; } = new List<(int, int)>();
            public List<(int ProductId, int Quantity)> Updated { get; } = new List<(int, int)>();

            public Task BroadcastInventoryUpdatedAsync(int productId, int quantityAvailable)
            {
                Updated.Add((productId, quantityAvailable));
                return Task.CompletedTask;
            }

            public Task BroadcastFlashSaleStartedAsync(FlashSale sale, int quantityAvailable)
            {
                StartedSaleIds.Add(sale.Id);
                StartedProductIds.Add(sale.ProductId);
                return Task.CompletedTask;
            }

            public Task BroadcastFlashSaleEndedAsync(int productId, int saleId)
            {
                Ended.Add((productId, saleId));
                return Task.CompletedTask;
            }
        }

        /// <summary>Test subclass exposing the <c>protected virtual</c> per-tick seam for one deterministic tick.</summary>
        private class TestableSweep : ReservationExpirySweepService
        {
            public TestableSweep(IServiceScopeFactory f, IInventoryBroadcastCoordinator coordinator,
                IConfiguration c, ILogger<ReservationExpirySweepService> l)
                : base(f, coordinator, c, l) { }

            public Task RunOnceAsync(CancellationToken ct) => ExecuteSweepAsync(ct);
        }

        // Mocked IConfiguration whose FLASH_SALE_POLL_INTERVAL_MS returns ms (null => SUT default; unused by the
        // direct-tick call here).
        private static Mock<IConfiguration> PollConfig(string ms)
        {
            var config = new Mock<IConfiguration>();
            config.Setup(c => c["FLASH_SALE_POLL_INTERVAL_MS"]).Returns(ms);
            return config;
        }

        // Scoped StoreContext (fresh per scope, all sharing one InMemory backing store via dbName), resolved by
        // the sweep on each tick — mirrors the sibling sweep tests' factory.
        private static IServiceScopeFactory BuildScopeFactory(string dbName)
        {
            var services = new ServiceCollection();
            services.AddScoped<StoreContext>(_ => TestStoreContextFactory.CreateInMemoryContext(dbName));
            return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        }

        [Fact]
        public async Task AlreadyActiveSale_ScheduleThenSweepTick_BroadcastsFlashSaleStartedExactlyOnce()
        {
            // Arrange — one product (base Price = 10, so the 5.00 sale price is a genuine discount) in a shared
            // InMemory store. The REAL coordinator is shared across both publishers, exactly as the DI container
            // registers it (a process-wide singleton).
            var dbName = Guid.NewGuid().ToString();
            List<Product> products;
            using (var seed = TestStoreContextFactory.CreateInMemoryContext(dbName))
            {
                products = await TestStoreContextFactory.SeedProductsAsync(seed, 1);
            }
            var productId = products[0].Id;

            var coordinator = new InventoryBroadcastCoordinator(
                _broadcaster, NullLogger<InventoryBroadcastCoordinator>.Instance);

            // Act 1 — ScheduleAsync creates a sale that is ALREADY inside its window (StartAt in the past, EndAt
            // in the future) and announces it immediately through the shared coordinator.
            FlashSaleScheduleResult scheduleResult;
            using (var fsContext = TestStoreContextFactory.CreateInMemoryContext(dbName))
            {
                var flashSaleService = new FlashSaleService(
                    fsContext, coordinator, NullLogger<FlashSaleService>.Instance);
                scheduleResult = await flashSaleService.ScheduleAsync(productId,
                    DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), 5m, 100);
            }

            scheduleResult.Outcome.Should().Be(FlashSaleScheduleOutcome.Success);
            scheduleResult.FlashSale.Id.Should().BeGreaterThan(0);

            // After ScheduleAsync (created already-active), exactly one FlashSaleStarted has been broadcast.
            _broadcaster.StartedSaleIds.Should().ContainSingle().Which.Should().Be(scheduleResult.FlashSale.Id);

            // Act 2 — one sweep tick against the SAME coordinator and SAME store. The sweep observes the active
            // sale and calls PublishFlashSaleStartedAsync; the coordinator's idempotency marker (set by
            // ScheduleAsync) makes this a NO-OP, so no second event is broadcast.
            var sweep = new TestableSweep(
                BuildScopeFactory(dbName), coordinator, PollConfig(null).Object,
                NullLogger<ReservationExpirySweepService>.Instance);
            await sweep.RunOnceAsync(CancellationToken.None);

            // Assert — still EXACTLY ONE FlashSaleStarted for the sale (two before the fix), for the right product.
            _broadcaster.StartedSaleIds.Should().ContainSingle().Which.Should().Be(scheduleResult.FlashSale.Id);
            _broadcaster.StartedProductIds.Should().OnlyContain(p => p == productId);
        }

        [Fact]
        public async Task SweepOnlyPublisher_TwoTicksForActiveSale_BroadcastsFlashSaleStartedExactlyOnce()
        {
            // Arrange — a sale inserted DIRECTLY (bypassing ScheduleAsync's publish), so the sweep is the sole
            // publisher. Two ticks against the SAME real coordinator must still yield exactly one started event.
            var dbName = Guid.NewGuid().ToString();
            using (var seed = TestStoreContextFactory.CreateInMemoryContext(dbName))
            {
                var seeded = await TestStoreContextFactory.SeedProductsAsync(seed, 1);
                seed.FlashSales.Add(new FlashSale
                {
                    ProductId = seeded[0].Id,
                    StartAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    EndAt = DateTimeOffset.UtcNow.AddHours(1),
                    SalePrice = 5m,
                    StockAllocation = 100
                });
                await seed.SaveChangesAsync();
            }

            var coordinator = new InventoryBroadcastCoordinator(
                _broadcaster, NullLogger<InventoryBroadcastCoordinator>.Instance);
            var sweep = new TestableSweep(
                BuildScopeFactory(dbName), coordinator, PollConfig(null).Object,
                NullLogger<ReservationExpirySweepService>.Instance);

            // Act — two ticks (window stays open across both).
            await sweep.RunOnceAsync(CancellationToken.None);
            await sweep.RunOnceAsync(CancellationToken.None);

            // Assert — deduplicated to exactly one across ticks.
            _broadcaster.StartedSaleIds.Should().ContainSingle();
        }
    }
}
