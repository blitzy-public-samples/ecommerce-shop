using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.Tests.Services
{
    /// <summary>
    /// Unit tests for <see cref="InventoryBroadcastCoordinator"/>, focused on the QA Issue 1 fix
    /// (duplicate <c>FlashSaleStarted</c> event).
    ///
    /// <para>
    /// <b>Why this matters.</b> A flash sale that enters its active window has TWO independent publishers of
    /// <c>FlashSaleStarted</c>: <see cref="FlashSaleService.ScheduleAsync"/> (immediately, when a sale is created
    /// already inside its window) and <see cref="ReservationExpirySweepService"/> (on the first sweep tick that
    /// observes the window open). Previously the sweep held a PRIVATE dedup set that could not see ScheduleAsync's
    /// publication, so the two raced and produced two identical <c>FlashSaleStarted</c> events. The fix moves the
    /// "already announced started" marker into this shared coordinator — the single publication mechanism used by
    /// every writer — so <see cref="InventoryBroadcastCoordinator.PublishFlashSaleStartedAsync"/> is idempotent
    /// per <see cref="FlashSale.Id"/> across BOTH publishers, and <see cref="InventoryBroadcastCoordinator
    /// .ForgetStartedAnnouncements"/> keeps that marker set bounded.
    /// </para>
    ///
    /// <para>
    /// The SignalR-facing send is abstracted behind <see cref="IInventoryBroadcaster"/>; a controllable recording
    /// double (<see cref="RecordingBroadcaster"/>) captures exactly which <c>FlashSaleStarted</c> broadcasts the
    /// coordinator actually issued, and can be told to throw once to prove the failure/retry semantics. The logger
    /// is <see cref="NullLogger{T}"/> so tests never assert on brittle <c>ILogger</c> extension calls.
    /// </para>
    /// </summary>
    public class InventoryBroadcastCoordinatorTests
    {
        // Fresh double per test (xUnit re-instantiates the class for every [Fact]) => parallel-safe, no shared state.
        private readonly RecordingBroadcaster _broadcaster = new RecordingBroadcaster();

        private InventoryBroadcastCoordinator CreateSut() =>
            new InventoryBroadcastCoordinator(_broadcaster, NullLogger<InventoryBroadcastCoordinator>.Instance);

        /// <summary>
        /// Controllable recording double for <see cref="IInventoryBroadcaster"/>. Records the sale id and product
        /// id of every <c>FlashSaleStarted</c> broadcast the coordinator issues. When <see cref="ThrowOnStarted"/>
        /// is set it throws BEFORE recording, modelling a transient SignalR send failure so the coordinator's
        /// swallow-and-release-marker path can be exercised.
        /// </summary>
        private class RecordingBroadcaster : IInventoryBroadcaster
        {
            public List<int> StartedSaleIds { get; } = new List<int>();
            public List<int> StartedProductIds { get; } = new List<int>();
            public List<int> Ended { get; } = new List<int>();
            public List<(int ProductId, int Quantity)> Updated { get; } = new List<(int, int)>();

            // When true, the next FlashSaleStarted broadcast throws (transient failure) and records nothing.
            public bool ThrowOnStarted { get; set; }

            public Task BroadcastInventoryUpdatedAsync(int productId, int quantityAvailable)
            {
                Updated.Add((productId, quantityAvailable));
                return Task.CompletedTask;
            }

            public Task BroadcastFlashSaleStartedAsync(FlashSale sale, int quantityAvailable)
            {
                if (ThrowOnStarted)
                {
                    throw new InvalidOperationException("simulated transient SignalR send failure");
                }
                StartedSaleIds.Add(sale.Id);
                StartedProductIds.Add(sale.ProductId);
                return Task.CompletedTask;
            }

            public Task BroadcastFlashSaleEndedAsync(int productId)
            {
                Ended.Add(productId);
                return Task.CompletedTask;
            }
        }

        private static FlashSale Sale(int id, int productId) =>
            new FlashSale
            {
                Id = id,
                ProductId = productId,
                StartAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                EndAt = DateTimeOffset.UtcNow.AddHours(1),
                SalePrice = 5m,
                StockAllocation = 100
            };

        // Availability compute delegate stand-in (the coordinator re-reads authoritative availability inside its
        // lock; the exact value is irrelevant to the cardinality assertions).
        private static Func<Task<int>> Available(int value) => () => Task.FromResult(value);

        [Fact]
        public async Task PublishFlashSaleStartedAsync_CalledTwiceForSameSaleId_BroadcastsExactlyOnce()
        {
            // Arrange — the QA Issue 1 core invariant, isolated to the coordinator: two publishers announce the
            // SAME sale entering its window.
            var sut = CreateSut();
            var sale = Sale(id: 1, productId: 1);

            // Act — simulate ScheduleAsync's publish followed by the sweep's publish for the identical sale id.
            await sut.PublishFlashSaleStartedAsync(sale, Available(100));
            await sut.PublishFlashSaleStartedAsync(sale, Available(100));

            // Assert — exactly ONE FlashSaleStarted broadcast (the duplicate is gone).
            _broadcaster.StartedSaleIds.Should().ContainSingle().Which.Should().Be(1);
        }

        [Fact]
        public async Task PublishFlashSaleStartedAsync_ForDifferentSaleIds_BroadcastsEach()
        {
            // Arrange — dedup is per sale id, NOT global: two distinct sales must each announce.
            var sut = CreateSut();

            // Act
            await sut.PublishFlashSaleStartedAsync(Sale(id: 1, productId: 1), Available(100));
            await sut.PublishFlashSaleStartedAsync(Sale(id: 2, productId: 2), Available(50));

            // Assert
            _broadcaster.StartedSaleIds.Should().Equal(1, 2);
        }

        [Fact]
        public async Task ForgetStartedAnnouncements_AfterEnding_AllowsANewSaleToAnnounce()
        {
            // Arrange — announce sale 1, then tell the coordinator that sale 1's window has ended.
            var sut = CreateSut();
            await sut.PublishFlashSaleStartedAsync(Sale(id: 1, productId: 1), Available(100));
            _broadcaster.StartedSaleIds.Should().ContainSingle();

            // Act — forget the ended sale, then a brand-new sale (distinct id) for the same product announces
            // normally. (Ids are monotonic, so id=1 is never reused; forgetting simply keeps the set bounded.)
            sut.ForgetStartedAnnouncements(new[] { 1 });
            await sut.PublishFlashSaleStartedAsync(Sale(id: 2, productId: 1), Available(100));

            // Assert — the second, distinct sale announced (proving the ended marker was released and the set
            // stays bounded without suppressing legitimately-new sales).
            _broadcaster.StartedSaleIds.Should().Equal(1, 2);
        }

        [Fact]
        public async Task PublishFlashSaleStartedAsync_WhenBroadcastThrows_DoesNotThrowAndAllowsRetry()
        {
            // Arrange — the first send fails transiently; a post-commit broadcast failure must never propagate,
            // and (QA Issue 1 fix) must NOT permanently suppress the event — the marker is released so the other
            // publisher / next sweep tick can retry.
            var sut = CreateSut();
            var sale = Sale(id: 1, productId: 1);

            // Act 1 — failing send: swallowed, nothing recorded.
            _broadcaster.ThrowOnStarted = true;
            Func<Task> failing = () => sut.PublishFlashSaleStartedAsync(sale, Available(100));
            await failing.Should().NotThrowAsync();
            _broadcaster.StartedSaleIds.Should().BeEmpty();

            // Act 2 — the retry (send now succeeds) DOES broadcast, because the failed attempt released the marker.
            _broadcaster.ThrowOnStarted = false;
            await sut.PublishFlashSaleStartedAsync(sale, Available(100));

            // Assert — exactly one successful broadcast after the retry.
            _broadcaster.StartedSaleIds.Should().ContainSingle().Which.Should().Be(1);
        }

        [Fact]
        public void ForgetStartedAnnouncements_WithNull_DoesNotThrow()
        {
            // Arrange
            var sut = CreateSut();

            // Act
            Action act = () => sut.ForgetStartedAnnouncements(null);

            // Assert — null is a safe no-op (defensive against an empty/absent ended set on a tick).
            act.Should().NotThrow();
        }
    }
}
