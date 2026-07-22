using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Unit tests for the Real-Time Inventory &amp; Flash-Sale sweep
    /// (<see cref="ReservationExpirySweepService"/> — AAP requirement R4, §0.4.1 Group 2).
    ///
    /// <para>
    /// The service derives from <c>Microsoft.Extensions.Hosting.BackgroundService</c> and keeps its per-tick
    /// work behind a <c>protected virtual</c> seam (<c>ExecuteSweepAsync</c>) exactly like the proven
    /// <c>PaymentService</c> seam. Rather than depend on wall-clock timing, we exercise that seam directly
    /// through a nested test subclass (<see cref="TestableSweep"/>), mirroring
    /// <c>PaymentServiceTests.TestablePaymentService</c>. Host-loop resilience is proven independently with a
    /// second subclass (<see cref="ThrowingSweep"/>) whose tick cancels then throws, so the loop must swallow
    /// the exception and terminate cleanly.
    /// </para>
    ///
    /// <para>
    /// No real infrastructure is used: the DI scope is a genuine in-memory <see cref="ServiceCollection"/>
    /// serving a fresh <see cref="StoreContext"/> per scope over the EF Core InMemory provider (a shared
    /// backing store keyed by <c>dbName</c>, the factory pattern already used across this test project), and the
    /// broadcast coordinator is a recording <see cref="IInventoryBroadcastCoordinator"/> fake
    /// (<see cref="RecordingBroadcastCoordinator"/>) that evaluates the post-commit compute delegate the sweep
    /// hands it and records the resolved availability. Configuration is a <see cref="Mock{T}"/> of
    /// <see cref="IConfiguration"/>. The logger is <see cref="NullLogger{T}"/> so we never assert on brittle
    /// <c>ILogger</c> extension-method calls.
    /// </para>
    ///
    /// <para>
    /// <b>Behavioral note driving the assertions.</b> This service implements the hardened, durable
    /// Status-based reservation model (review findings F01/F04): an expired hold is NOT deleted — it is
    /// transitioned to <see cref="ReservationStatus.Expired"/>, which returns its stock to the available pool
    /// while preserving the row so a lapsed <c>ExpiresAt</c> can never erase a completed (Consumed) sale
    /// (zero-oversell invariant, AAP R3). Availability is SALE-SCOPED (by <c>FlashSaleId</c>) and recomputed as
    /// <c>FlashSale.StockAllocation − SUM(Quantity)</c> over rows of that sale that still hold stock (Consumed,
    /// or Active and not yet expired); consequently the seeded holds carry the sale's <c>FlashSaleId</c>. The
    /// assertions below reflect that state-transition semantics rather than a physical delete.
    /// </para>
    ///
    /// <para>
    /// Every test follows the <c>MethodName_StateUnderTest_ExpectedBehavior</c> convention, an explicit
    /// Arrange-Act-Assert structure, FluentAssertions, and creates its own fresh doubles via the xUnit
    /// per-test constructor (no shared mutable state), consistent with the sibling <c>Services/</c> tests.
    /// </para>
    /// </summary>
    public class ReservationExpirySweepServiceTests
    {
        // Fresh recording coordinator per test (xUnit constructs the test class once per [Fact]) => parallel-safe,
        // no shared state. The sweep publishes through the ordered IInventoryBroadcastCoordinator, handing it a
        // post-commit compute delegate; this fake evaluates that delegate and records what would be broadcast.
        private readonly RecordingBroadcastCoordinator _coordinator = new RecordingBroadcastCoordinator();

        /// <summary>
        /// In-test recording double for <see cref="IInventoryBroadcastCoordinator"/>. Availability publications
        /// evaluate the supplied compute delegate (exactly as the real coordinator does inside its per-product
        /// lock) and record the resolved <c>(productId, availability)</c>; flash-sale boundary publications are
        /// recorded as product ids. Keeping the three streams separate lets a test assert precisely on the one it
        /// exercises without cross-contamination.
        /// </summary>
        private class RecordingBroadcastCoordinator : IInventoryBroadcastCoordinator
        {
            public List<(int ProductId, int Available)> AvailabilityPublications { get; } = new List<(int, int)>();
            public List<int> FlashSaleStarted { get; } = new List<int>();
            public List<int> FlashSaleEnded { get; } = new List<int>();

            // QA Issue 1 fix: this fake faithfully models the real InventoryBroadcastCoordinator's contract, in
            // which PublishFlashSaleStartedAsync is IDEMPOTENT per sale id and ForgetStartedAnnouncements releases
            // those markers. The sweep no longer owns a private started-dedup set, so this double is what makes
            // the "exactly once per entered window" behavior observable end-to-end through the sweep's ticks.
            private readonly HashSet<int> _startedSaleIds = new HashSet<int>();

            public async Task PublishAvailabilityAsync(int productId, Func<Task<int>> computeAuthoritativeAvailabilityAsync)
            {
                var available = await computeAuthoritativeAvailabilityAsync();
                AvailabilityPublications.Add((productId, available));
            }

            public async Task PublishFlashSaleStartedAsync(FlashSale sale, Func<Task<int>> computeAuthoritativeAvailabilityAsync)
            {
                // Idempotent per sale id, exactly as InventoryBroadcastCoordinator: the FIRST call for a sale id
                // records the started event; a later call for the SAME id is a no-op (until ForgetStartedAnnouncements
                // releases it). This is what dedups the two publishers (sweep + ScheduleAsync).
                if (!_startedSaleIds.Add(sale.Id))
                {
                    return;
                }

                // Exercise the compute delegate exactly as the real coordinator would (authoritative re-read),
                // then record the boundary event by product id.
                await computeAuthoritativeAvailabilityAsync();
                FlashSaleStarted.Add(sale.ProductId);
            }

            public void ForgetStartedAnnouncements(IEnumerable<int> endedSaleIds)
            {
                if (endedSaleIds == null)
                {
                    return;
                }
                foreach (var id in endedSaleIds)
                {
                    _startedSaleIds.Remove(id);
                }
            }

            public Task PublishFlashSaleEndedAsync(int productId)
            {
                FlashSaleEnded.Add(productId);
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Test-only subclass that surfaces the production seams. <c>ExecuteSweepAsync</c> is
        /// <c>protected virtual</c> and <c>ExecuteAsync</c> is <c>protected</c>; a derived type can therefore
        /// invoke both, letting a test run exactly one deterministic tick (<see cref="RunOnceAsync"/>) or the
        /// full host loop (<see cref="RunLoopAsync"/>) without any timing dependency. Mirrors
        /// <c>PaymentServiceTests.TestablePaymentService</c>.
        /// </summary>
        private class TestableSweep : ReservationExpirySweepService
        {
            public TestableSweep(IServiceScopeFactory f, IInventoryBroadcastCoordinator coordinator,
                IConfiguration c, ILogger<ReservationExpirySweepService> l)
                : base(f, coordinator, c, l) { }

            // protected virtual ExecuteSweepAsync => reachable from this derived type.
            public Task RunOnceAsync(CancellationToken ct) => ExecuteSweepAsync(ct);

            // protected ExecuteAsync (from BackgroundService, overridden by the SUT) => reachable here.
            public Task RunLoopAsync(CancellationToken ct) => ExecuteAsync(ct);
        }

        /// <summary>
        /// Test-only subclass that overrides the sweep tick to deterministically model a transient failure:
        /// it records the tick, cancels the supplied token, then throws. Driving <see cref="RunLoopAsync"/>
        /// with this subclass proves the host loop swallows the exception (never crashing the host) and exits
        /// promptly once the token is cancelled.
        /// </summary>
        private class ThrowingSweep : ReservationExpirySweepService
        {
            private readonly CancellationTokenSource _cts;
            public int Ticks;

            public ThrowingSweep(IServiceScopeFactory f, IInventoryBroadcastCoordinator coordinator,
                IConfiguration c, ILogger<ReservationExpirySweepService> l, CancellationTokenSource cts)
                : base(f, coordinator, c, l) => _cts = cts;

            protected override Task ExecuteSweepAsync(CancellationToken ct)
            {
                Ticks++;
                _cts.Cancel();
                throw new InvalidOperationException("transient");
            }

            public Task RunLoopAsync(CancellationToken ct) => ExecuteAsync(ct);
        }

        /// <summary>
        /// Builds a mocked <see cref="IConfiguration"/> whose <c>FLASH_SALE_POLL_INTERVAL_MS</c> key returns
        /// <paramref name="ms"/>. Passing <c>null</c> models an unset key, so the SUT falls back to its 5000 ms
        /// default (unused by the direct-tick tests; harmless for the resilience test because its token is
        /// already cancelled when the delay is reached).
        /// </summary>
        private static Mock<IConfiguration> PollConfig(string ms)
        {
            var config = new Mock<IConfiguration>();
            config.Setup(c => c["FLASH_SALE_POLL_INTERVAL_MS"]).Returns(ms);
            return config;
        }

        /// <summary>
        /// Registers a scoped <see cref="StoreContext"/> (a fresh context per scope, all sharing one InMemory
        /// backing store via <paramref name="dbName"/>), then resolves the framework
        /// <see cref="IServiceScopeFactory"/> the SUT opens a scope from on every tick. The broadcast coordinator
        /// is injected into the SUT directly (a singleton), not resolved from this per-tick scope, so only
        /// <see cref="StoreContext"/> needs registering here.
        /// </summary>
        private IServiceScopeFactory BuildScopeFactory(string dbName)
        {
            var services = new ServiceCollection();
            // Register the concrete StoreContext the SUT resolves per tick via
            // GetRequiredService<StoreContext>(); the explicit type parameter documents that contract.
            services.AddScoped<StoreContext>(_ => TestStoreContextFactory.CreateInMemoryContext(dbName));
            return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        }

        [Fact]
        public async Task ExecuteSweepAsync_WhenReservationsExpired_ExpiresThemReleasesStockAndBroadcastsInventoryUpdated()
        {
            // Arrange — an active flash sale (required so availability is StockAllocation − held stock, not 0)
            // with one lapsed hold (Qty 20) and one still-live hold (Qty 10) for the same product. Availability
            // is sale-scoped, so both holds carry the sale's FlashSaleId.
            var dbName = Guid.NewGuid().ToString();
            using (var seed = TestStoreContextFactory.CreateInMemoryContext(dbName))
            {
                var sale = new FlashSale
                {
                    ProductId = 1,
                    StartAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    EndAt = DateTimeOffset.UtcNow.AddHours(1),
                    SalePrice = 5m,
                    StockAllocation = 100
                };
                seed.FlashSales.Add(sale);
                await seed.SaveChangesAsync(); // assigns sale.Id for the sale-scoped holds below.

                seed.InventoryReservations.AddRange(
                    new InventoryReservation
                    {
                        FlashSaleId = sale.Id,
                        ProductId = 1,
                        Quantity = 20,
                        SessionId = "expired",
                        Status = ReservationStatus.Active,
                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
                    },
                    new InventoryReservation
                    {
                        FlashSaleId = sale.Id,
                        ProductId = 1,
                        Quantity = 10,
                        SessionId = "active",
                        Status = ReservationStatus.Active,
                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
                    });
                await seed.SaveChangesAsync();
            }
            var sut = new TestableSweep(
                BuildScopeFactory(dbName),
                _coordinator,
                PollConfig(null).Object,
                NullLogger<ReservationExpirySweepService>.Instance);

            // Act
            await sut.RunOnceAsync(CancellationToken.None);

            // Assert — the sweep uses the durable Status-based model: the lapsed hold is transitioned to
            // Expired (NOT deleted) so both rows persist, and the released stock re-broadcasts availability
            // => 100 − 10 (the still-active hold) = 90. The live hold is untouched.
            using var verify = TestStoreContextFactory.CreateInMemoryContext(dbName);
            verify.InventoryReservations.Count().Should().Be(2);
            verify.InventoryReservations.Count(r => r.Status == ReservationStatus.Expired).Should().Be(1);
            verify.InventoryReservations.Count(r => r.Status == ReservationStatus.Active).Should().Be(1);
            verify.InventoryReservations.Single(r => r.Status == ReservationStatus.Active)
                .SessionId.Should().Be("active");
            _coordinator.AvailabilityPublications.Should().ContainSingle().Which.Should().Be((1, 90));
        }

        [Fact]
        public async Task ExecuteSweepAsync_WhenSaleWindowClosed_BroadcastsFlashSaleEnded()
        {
            // Arrange — a sale whose [StartAt, EndAt] window has JUST closed (a few seconds ago, comfortably
            // inside the sweep's recent-ended lookback window so the boundary event is emitted).
            var dbName = Guid.NewGuid().ToString();
            using (var seed = TestStoreContextFactory.CreateInMemoryContext(dbName))
            {
                seed.FlashSales.Add(new FlashSale
                {
                    ProductId = 1,
                    StartAt = DateTimeOffset.UtcNow.AddHours(-2),
                    EndAt = DateTimeOffset.UtcNow.AddSeconds(-5),
                    SalePrice = 5m,
                    StockAllocation = 100
                });
                await seed.SaveChangesAsync();
            }
            var sut = new TestableSweep(
                BuildScopeFactory(dbName),
                _coordinator,
                PollConfig(null).Object,
                NullLogger<ReservationExpirySweepService>.Instance);

            // Act
            await sut.RunOnceAsync(CancellationToken.None);

            // Assert — a just-closed window fires FlashSaleEnded exactly once for the affected product.
            _coordinator.FlashSaleEnded.Should().ContainSingle().Which.Should().Be(1);
        }

        [Fact]
        public async Task ExecuteSweepAsync_WhenNothingExpired_DoesNotBroadcastInventoryUpdated()
        {
            // Arrange — a single still-active hold and deliberately NO sale, isolating this test from the
            // FlashSaleStarted/FlashSaleEnded window events so only the expiry path is under scrutiny.
            var dbName = Guid.NewGuid().ToString();
            using (var seed = TestStoreContextFactory.CreateInMemoryContext(dbName))
            {
                seed.InventoryReservations.Add(new InventoryReservation
                {
                    ProductId = 1,
                    Quantity = 10,
                    SessionId = "active",
                    Status = ReservationStatus.Active,
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
                });
                await seed.SaveChangesAsync();
            }
            var sut = new TestableSweep(
                BuildScopeFactory(dbName),
                _coordinator,
                PollConfig(null).Object,
                NullLogger<ReservationExpirySweepService>.Instance);

            // Act
            await sut.RunOnceAsync(CancellationToken.None);

            // Assert — nothing expired and no active sale, so the row is untouched and no availability broadcast
            // is emitted.
            using var verify = TestStoreContextFactory.CreateInMemoryContext(dbName);
            verify.InventoryReservations.Count().Should().Be(1);
            _coordinator.AvailabilityPublications.Should().BeEmpty();
        }

        [Fact]
        public async Task ExecuteSweepAsync_CalledTwiceForSameEnteredSale_BroadcastsFlashSaleStartedOnce()
        {
            // Arrange — a currently-active sale and no reservations.
            var dbName = Guid.NewGuid().ToString();
            using (var seed = TestStoreContextFactory.CreateInMemoryContext(dbName))
            {
                seed.FlashSales.Add(new FlashSale
                {
                    ProductId = 1,
                    StartAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    EndAt = DateTimeOffset.UtcNow.AddHours(1),
                    SalePrice = 5m,
                    StockAllocation = 100
                });
                await seed.SaveChangesAsync();
            }
            var sut = new TestableSweep(
                BuildScopeFactory(dbName),
                _coordinator,
                PollConfig(null).Object,
                NullLogger<ReservationExpirySweepService>.Instance);

            // Act — tick the SAME instance twice against the SAME coordinator so the coordinator's idempotency
            // marker (QA Issue 1 fix: the started-dedup now lives in the shared IInventoryBroadcastCoordinator,
            // not privately in the sweep) persists across ticks.
            await sut.RunOnceAsync(CancellationToken.None);
            await sut.RunOnceAsync(CancellationToken.None);

            // Assert — FlashSaleStarted is announced exactly once for the entered window (deduplicated).
            _coordinator.FlashSaleStarted.Should().ContainSingle().Which.Should().Be(1);
        }

        [Fact]
        public async Task ExecuteAsync_WhenTickThrows_SwallowsExceptionAndDoesNotCrashHost()
        {
            // Arrange — the first tick cancels the token and then throws; the loop must log-and-swallow the
            // exception, then break when the honored Task.Delay observes the cancellation.
            var cts = new CancellationTokenSource();
            var scopeFactory = new ServiceCollection()
                .BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>();
            var sut = new ThrowingSweep(
                scopeFactory,
                _coordinator,
                PollConfig(null).Object,
                NullLogger<ReservationExpirySweepService>.Instance,
                cts);

            // Act
            Func<Task> act = () => sut.RunLoopAsync(cts.Token);

            // Assert — no exception escapes the host loop, and exactly one tick executed. Task.Delay on the
            // already-cancelled token throws TaskCanceledException immediately, so the loop breaks without the
            // full poll-interval wait (no hang).
            await act.Should().NotThrowAsync();
            sut.Ticks.Should().Be(1);
        }
    }
}
