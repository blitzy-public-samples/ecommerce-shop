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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.Tests.Services
{
    /// <summary>
    /// Unit tests for <see cref="InventoryReservationService"/> — the zero-oversell heart of the
    /// Real-Time Inventory &amp; Flash Sale feature (AAP R3). Every test follows the
    /// <c>MethodName_StateUnderTest_ExpectedBehavior</c> convention and the Arrange-Act-Assert
    /// structure, creates a fresh set of Moq doubles per test via the xUnit per-test constructor
    /// (no static/shared mutable state), and asserts with FluentAssertions.
    ///
    /// <para>
    /// NO real infrastructure and — critically — NO real threads are used anywhere. Data is served by
    /// the EF Core InMemory provider through <see cref="TestStoreContextFactory.CreateInMemoryContext"/>;
    /// broadcasting is a recording <see cref="IInventoryBroadcastCoordinator"/> fake; configuration is a
    /// <see cref="Mock{IConfiguration}"/>. The optimistic-concurrency retry contract is proven
    /// DETERMINISTICALLY via the nested <see cref="SaveConflictStoreContext"/> subclass, which
    /// intercepts the single virtual save entry point and throws a controllable number of
    /// <see cref="DbUpdateConcurrencyException"/>s — the InMemory provider does not enforce the
    /// <c>FlashSale.Version</c> token, so a genuine multi-threaded collision would be flaky.
    /// </para>
    ///
    /// <para>
    /// These tests bind to the hardened, review-reconciled contracts (findings F01–F06): reservations
    /// are sale-scoped (<c>FlashSaleId</c>) and carry a durable <see cref="ReservationStatus"/>;
    /// availability subtracts both <see cref="ReservationStatus.Active"/> non-expired holds AND
    /// <see cref="ReservationStatus.Consumed"/> (sold) rows; <c>ReleaseAsync</c> is ownership-checked and
    /// returns a <see cref="ReleaseOutcome"/>; and <c>ConsumeReservationsAsync</c> transitions matched
    /// holds to <see cref="ReservationStatus.Consumed"/> (never deletes) so sold units can never return to
    /// the available pool.
    /// </para>
    /// </summary>
    public class InventoryReservationServiceTests
    {
        // Recording fake for the ordered broadcast seam. The service publishes AFTER commit by handing the
        // coordinator a compute delegate (Func<Task<int>>) rather than a literal number; this fake evaluates that
        // delegate against the committed store and records the (productId, availability) it resolves, so a test
        // asserts the EXACT availability that would be broadcast - the same authoritative, sale-scoped value the
        // real coordinator would send. Fresh per test (xUnit instantiates the test class once per [Fact]).
        private readonly RecordingBroadcastCoordinator _coordinator = new RecordingBroadcastCoordinator();

        // Logger double: the service logs a debug line when it defensively skips a contended consume. The
        // Mock<ILogger<T>> pattern mirrors the existing OrderServiceTests convention.
        private readonly ILogger<InventoryReservationService> _logger =
            new Mock<ILogger<InventoryReservationService>>().Object;

        // SUT factory binding the hardened 4-arg constructor
        // (StoreContext, IInventoryBroadcastCoordinator, IConfiguration, ILogger<InventoryReservationService>).
        private InventoryReservationService CreateSut(StoreContext context, string ttl = null) =>
            new InventoryReservationService(context, _coordinator, ConfigWithTtl(ttl), _logger);

        // Canonical basket-UUID session keys. Review finding C05 makes the service a trust boundary that accepts
        // ONLY a canonical GUID (the basket UUID reused as the session key, AAP R8) and returns Outcome.Invalid
        // for anything else. Distinct constants model distinct shopper sessions; each is already in canonical
        // "D" (lowercase) form, so it equals the value the service normalises and stores.
        private const string SessionS = "11111111-1111-1111-1111-111111111111";
        private const string SessionSess1 = "22222222-2222-2222-2222-222222222222";
        private const string SessionSess42 = "33333333-3333-3333-3333-333333333333";
        private const string SessionOther = "44444444-4444-4444-4444-444444444444";
        private const string SessionStale = "55555555-5555-5555-5555-555555555555";
        private const string SessionSold = "66666666-6666-6666-6666-666666666666";
        private const string SessionOwner = "77777777-7777-7777-7777-777777777777";
        private const string SessionAttacker = "88888888-8888-8888-8888-888888888888";
        private const string SessionSomeoneElse = "99999999-9999-9999-9999-999999999999";
        private const string SessionNobody = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

        /// <summary>
        /// In-test recording double for <see cref="IInventoryBroadcastCoordinator"/>. The reservation service
        /// broadcasts availability by passing a compute delegate evaluated AFTER commit; this fake awaits that
        /// delegate and records the resolved (productId, availability) pair, letting a test assert the exact
        /// availability that would reach connected clients. Flash-sale start/end publications are recorded for
        /// completeness but are not exercised by the reservation path.
        /// </summary>
        private class RecordingBroadcastCoordinator : IInventoryBroadcastCoordinator
        {
            public List<(int ProductId, int Available)> AvailabilityPublications { get; } = new List<(int, int)>();
            public List<int> FlashSaleStarted { get; } = new List<int>();
            public List<int> FlashSaleEnded { get; } = new List<int>();

            public async Task PublishAvailabilityAsync(int productId, Func<Task<int>> computeAuthoritativeAvailabilityAsync)
            {
                var available = await computeAuthoritativeAvailabilityAsync();
                AvailabilityPublications.Add((productId, available));
            }

            public async Task PublishFlashSaleStartedAsync(FlashSale sale, Func<Task<int>> computeAuthoritativeAvailabilityAsync)
            {
                var available = await computeAuthoritativeAvailabilityAsync();
                FlashSaleStarted.Add(sale.ProductId);
                AvailabilityPublications.Add((sale.ProductId, available));
            }

            public Task PublishFlashSaleEndedAsync(int productId)
            {
                FlashSaleEnded.Add(productId);
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Deterministic concurrency harness. Subclasses <see cref="StoreContext"/> and overrides the single
        /// virtual save entry point <c>SaveChangesAsync(bool, CancellationToken)</c> — through which ALL
        /// <c>SaveChanges</c>/<c>SaveChangesAsync</c> overloads route — to throw a controllable number of
        /// <see cref="DbUpdateConcurrencyException"/>s before delegating to the real (InMemory-backed) save.
        /// This simulates a lost optimistic-concurrency race precisely, without any real threads: the service's
        /// <c>catch -&gt; detach pending reservation -&gt; Entry(sale).ReloadAsync() -&gt; retry</c> path then runs
        /// over the same InMemory store the sale was seeded into.
        /// </summary>
        private class SaveConflictStoreContext : StoreContext
        {
            private int _throwsRemaining;

            /// <summary>Number of times <c>SaveChangesAsync</c> has been invoked (thrown or not).</summary>
            public int SaveCallCount { get; private set; }

            public SaveConflictStoreContext(DbContextOptions<StoreContext> options, int throwCount) : base(options)
                => _throwsRemaining = throwCount;

            public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
            {
                SaveCallCount++;
                if (_throwsRemaining-- > 0)
                    throw new DbUpdateConcurrencyException("simulated token conflict");
                return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            }
        }

        /// <summary>
        /// Builds an <see cref="IConfiguration"/> double whose only configured key is
        /// <c>RESERVATION_TTL_SECONDS</c>. Passing <c>null</c> exercises the service's default of 300 seconds
        /// (<c>int.TryParse(null, ...)</c> is false); mirrors how <c>PaymentService</c>/<c>TokenService</c>
        /// read scalar config values via the string indexer.
        /// </summary>
        private static IConfiguration ConfigWithTtl(string ttl)
        {
            var config = new Mock<IConfiguration>();
            config.Setup(c => c["RESERVATION_TTL_SECONDS"]).Returns(ttl);
            return config.Object;
        }

        /// <summary>
        /// Creates an active flash sale (its <c>[StartAt, EndAt]</c> window straddles "now") for
        /// <paramref name="productId"/> with the supplied allocation. The <c>Id</c> is assigned by EF on save.
        /// </summary>
        private static FlashSale ActiveSale(int productId, int stockAllocation, decimal salePrice = 9.99m) =>
            new FlashSale
            {
                ProductId = productId,
                StartAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                EndAt = DateTimeOffset.UtcNow.AddHours(1),
                SalePrice = salePrice,
                StockAllocation = stockAllocation
            };

        /// <summary>
        /// Seeds a single active flash sale into <paramref name="context"/>, saves it (so its generated
        /// <c>Id</c> is populated for use as a reservation's <c>FlashSaleId</c>), and returns the tracked entity.
        /// </summary>
        private static async Task<FlashSale> SeedSaleAsync(StoreContext context, int productId, int stockAllocation)
        {
            var sale = ActiveSale(productId, stockAllocation);
            context.FlashSales.Add(sale);
            await context.SaveChangesAsync();
            return sale;
        }

        /// <summary>
        /// Builds <see cref="DbContextOptions{StoreContext}"/> bound to a fresh InMemory store via an EXPLICIT
        /// <see cref="InMemoryDatabaseRoot"/>. The concurrency tests need TWO context instances of DIFFERENT
        /// CLR types — a plain seed <see cref="StoreContext"/> and the <see cref="SaveConflictStoreContext"/>
        /// subclass — to see the SAME data. EF Core caches the InMemory store in each context's internal
        /// service provider, and different context types built from the same options otherwise resolve to
        /// distinct providers (hence distinct stores). Supplying a shared root keys the store globally by
        /// (name, root), independent of context type, so the subclass observes the seeded sale and
        /// <c>Entry(sale).ReloadAsync()</c> works.
        /// </summary>
        private static DbContextOptions<StoreContext> SharedInMemoryOptions() =>
            new DbContextOptionsBuilder<StoreContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString(), new InMemoryDatabaseRoot())
                .Options;

        // ---------------------------------------------------------------------------------------------
        // ReserveAsync — availability & the zero-oversell invariant
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task ReserveAsync_WhenStockAvailable_PersistsReservationAndBroadcastsInventoryUpdated()
        {
            // Arrange
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            await SeedSaleAsync(context, productId: 1, stockAllocation: 100);
            var sut = CreateSut(context);

            // Act
            var result = await sut.ReserveAsync(1, 10, SessionSess1);

            // Assert — Success with remaining availability, exactly one persisted hold, one live broadcast.
            result.Outcome.Should().Be(ReservationOutcome.Success);
            result.Reservation.Should().NotBeNull();
            result.Reservation.Quantity.Should().Be(10);
            result.Reservation.SessionId.Should().Be(SessionSess1);
            result.Available.Should().Be(90);
            context.InventoryReservations.Count().Should().Be(1);
            _coordinator.AvailabilityPublications.Should().ContainSingle().Which.Should().Be((1, 90));
        }

        [Fact]
        public async Task ReserveAsync_WhenStockAvailable_RecordsSaleScopedActiveReservation()
        {
            // Arrange — capture the seeded sale so we can assert the hold is scoped to its FlashSaleId (F01).
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            var sale = await SeedSaleAsync(context, productId: 7, stockAllocation: 50);
            var sut = CreateSut(context);

            // Act
            var result = await sut.ReserveAsync(7, 5, SessionSess42);

            // Assert — the hold is sale-scoped and starts Active (durable-lifecycle model).
            result.Outcome.Should().Be(ReservationOutcome.Success);
            result.Reservation.FlashSaleId.Should().Be(sale.Id);
            result.Reservation.ProductId.Should().Be(7);
            result.Reservation.Status.Should().Be(ReservationStatus.Active);
            var persisted = context.InventoryReservations.Single();
            persisted.FlashSaleId.Should().Be(sale.Id);
            persisted.Status.Should().Be(ReservationStatus.Active);
        }

        [Fact]
        public async Task ReserveAsync_WhenRequestedExceedsAvailable_ReturnsInsufficientStockWithExactAvailableAndPersistsNothing()
        {
            // Arrange — allocation of 5, request 10.
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            await SeedSaleAsync(context, productId: 1, stockAllocation: 5);
            var sut = CreateSut(context);

            // Act
            var result = await sut.ReserveAsync(1, 10, SessionS);

            // Assert — exact remaining availability, NO partial reservation, no broadcast.
            result.Outcome.Should().Be(ReservationOutcome.InsufficientStock);
            result.Available.Should().Be(5);
            result.Reservation.Should().BeNull();
            context.InventoryReservations.Count().Should().Be(0); // nothing persisted
            _coordinator.AvailabilityPublications.Should().BeEmpty();
        }

        [Fact]
        public async Task ReserveAsync_WhenActiveReservationsReduceAvailability_ReturnsInsufficientStockWithRemaining()
        {
            // Arrange — an existing Active hold of 95 against a 100-unit sale leaves only 5 available.
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            var sale = await SeedSaleAsync(context, productId: 1, stockAllocation: 100);
            context.InventoryReservations.Add(new InventoryReservation
            {
                FlashSaleId = sale.Id,
                ProductId = 1,
                Quantity = 95,
                SessionId = SessionOther,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                Status = ReservationStatus.Active
            });
            await context.SaveChangesAsync();
            var sut = CreateSut(context);

            // Act — 100 − 95 = 5 available; 10 requested.
            var result = await sut.ReserveAsync(1, 10, SessionS);

            // Assert
            result.Outcome.Should().Be(ReservationOutcome.InsufficientStock);
            result.Available.Should().Be(5);
            result.Reservation.Should().BeNull();
            context.InventoryReservations.Count().Should().Be(1); // only the pre-seeded hold
            _coordinator.AvailabilityPublications.Should().BeEmpty();
        }

        [Fact]
        public async Task ReserveAsync_WhenConsumedReservationsHoldStock_ReturnsInsufficientStockPreservingZeroOversell()
        {
            // Arrange — a Consumed (sold) hold of the full allocation. Consumed rows REMAIN subtracted from
            // availability (the units are sold), so no further stock may be reserved. This is the invariant
            // that prevents a checkout from returning sold units to the pool (AAP R3). ExpiresAt is in the
            // past to prove Consumed counts regardless of expiry.
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            var sale = await SeedSaleAsync(context, productId: 1, stockAllocation: 100);
            context.InventoryReservations.Add(new InventoryReservation
            {
                FlashSaleId = sale.Id,
                ProductId = 1,
                Quantity = 100,
                SessionId = SessionSold,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                Status = ReservationStatus.Consumed
            });
            await context.SaveChangesAsync();
            var sut = CreateSut(context);

            // Act — even 1 unit must be refused.
            var result = await sut.ReserveAsync(1, 1, SessionS);

            // Assert
            result.Outcome.Should().Be(ReservationOutcome.InsufficientStock);
            result.Available.Should().Be(0);
            result.Reservation.Should().BeNull();
            context.InventoryReservations.Count().Should().Be(1); // only the pre-seeded Consumed hold
            _coordinator.AvailabilityPublications.Should().BeEmpty();
        }

        [Fact]
        public async Task ReserveAsync_WhenPriorReservationExpired_ExcludesItFromAvailabilityAndSucceeds()
        {
            // Arrange — a hold still flagged Active but whose ExpiresAt has already lapsed (the sweep has not
            // yet run). It no longer holds stock, so availability is the full allocation again.
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            var sale = await SeedSaleAsync(context, productId: 1, stockAllocation: 100);
            context.InventoryReservations.Add(new InventoryReservation
            {
                FlashSaleId = sale.Id,
                ProductId = 1,
                Quantity = 100,
                SessionId = SessionStale,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5), // lapsed
                Status = ReservationStatus.Active
            });
            await context.SaveChangesAsync();
            var sut = CreateSut(context);

            // Act — expired hold excluded => 100 available; reserve 80.
            var result = await sut.ReserveAsync(1, 80, SessionS);

            // Assert
            result.Outcome.Should().Be(ReservationOutcome.Success);
            result.Available.Should().Be(20);
            context.InventoryReservations.Count().Should().Be(2); // stale hold + the new one
            _coordinator.AvailabilityPublications.Should().ContainSingle().Which.Should().Be((1, 20));
        }

        [Fact]
        public async Task ReserveAsync_WhenNoActiveSale_ReturnsInsufficientStockWithZeroAvailable()
        {
            // Arrange — no flash sale seeded, so there is no allocation to reserve against.
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            var sut = CreateSut(context);

            // Act
            var result = await sut.ReserveAsync(1, 10, SessionS);

            // Assert — no sale => 409 INSUFFICIENT_STOCK {available:0}, nothing persisted, no broadcast.
            result.Outcome.Should().Be(ReservationOutcome.InsufficientStock);
            result.Available.Should().Be(0);
            result.Reservation.Should().BeNull();
            context.InventoryReservations.Count().Should().Be(0);
            _coordinator.AvailabilityPublications.Should().BeEmpty();
        }

        // ---------------------------------------------------------------------------------------------
        // ReserveAsync — reservation TTL (RESERVATION_TTL_SECONDS)
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task ReserveAsync_WhenTtlConfigured_SetsExpiresAtToNowPlusConfiguredTtl()
        {
            // Arrange — TTL explicitly configured to 60 seconds.
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            await SeedSaleAsync(context, productId: 1, stockAllocation: 100);
            var sut = CreateSut(context, "60");

            // Act
            var result = await sut.ReserveAsync(1, 10, SessionS);

            // Assert — ExpiresAt ≈ now + 60s (generous tolerance absorbs test-execution latency).
            result.Outcome.Should().Be(ReservationOutcome.Success);
            result.Reservation.ExpiresAt.Should().BeCloseTo(
                DateTimeOffset.UtcNow.AddSeconds(60), TimeSpan.FromSeconds(15));
        }

        [Fact]
        public async Task ReserveAsync_WhenTtlConfigMissing_DefaultsToThreeHundredSeconds()
        {
            // Arrange — RESERVATION_TTL_SECONDS absent (null) => service default of 300 seconds.
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            await SeedSaleAsync(context, productId: 1, stockAllocation: 100);
            var sut = CreateSut(context);

            // Act
            var result = await sut.ReserveAsync(1, 10, SessionS);

            // Assert — ExpiresAt ≈ now + 300s.
            result.Outcome.Should().Be(ReservationOutcome.Success);
            result.Reservation.ExpiresAt.Should().BeCloseTo(
                DateTimeOffset.UtcNow.AddSeconds(300), TimeSpan.FromSeconds(15));
        }

        // ---------------------------------------------------------------------------------------------
        // ReserveAsync — optimistic-concurrency retry (proven deterministically, NO real threads)
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task ReserveAsync_WhenConcurrencyConflictOccursOnce_RetriesOnceAndSucceeds()
        {
            // Arrange — share ONE options instance (bound to a shared InMemoryDatabaseRoot) so the seed
            // context and the conflict-injecting SUT context resolve to the SAME InMemory store (required for
            // Entry(sale).ReloadAsync() to find the row across the two different context types).
            var options = SharedInMemoryOptions();
            using (var seed = new StoreContext(options))
            {
                seed.FlashSales.Add(ActiveSale(1, 100));
                await seed.SaveChangesAsync();
            }
            using var context = new SaveConflictStoreContext(options, throwCount: 1);
            var sut = CreateSut(context);

            // Act — first save throws (lost race), reload+retry, second save succeeds.
            var result = await sut.ReserveAsync(1, 10, SessionS);

            // Assert — one throw + one success = exactly 2 save calls; the reserve ultimately succeeds.
            result.Outcome.Should().Be(ReservationOutcome.Success);
            context.SaveCallCount.Should().Be(2);
            _coordinator.AvailabilityPublications.Should().ContainSingle().Which.Should().Be((1, 90));
        }

        [Fact]
        public async Task ReserveAsync_WhenConcurrencyConflictPersists_ReturnsConflictAndDoesNotRetryMoreThanOnce()
        {
            // Arrange — both permitted attempts collide on the concurrency token. Shared-root options so the
            // seed StoreContext and the SaveConflictStoreContext subclass observe the same InMemory store.
            var options = SharedInMemoryOptions();
            using (var seed = new StoreContext(options))
            {
                seed.FlashSales.Add(ActiveSale(1, 100));
                await seed.SaveChangesAsync();
            }
            using var context = new SaveConflictStoreContext(options, throwCount: 2);
            var sut = CreateSut(context);

            // Act
            var result = await sut.ReserveAsync(1, 10, SessionS);

            // Assert — exactly 2 attempts (initial + ONE retry), never a 3rd; nothing persisted; no broadcast.
            result.Outcome.Should().Be(ReservationOutcome.Conflict);
            context.SaveCallCount.Should().Be(2);
            _coordinator.AvailabilityPublications.Should().BeEmpty();
            using var verify = new StoreContext(options);
            verify.InventoryReservations.Count().Should().Be(0);
        }

        // ---------------------------------------------------------------------------------------------
        // ReleaseAsync — ownership-checked explicit release (DELETE /api/inventory/reserve/{id})
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task ReleaseAsync_WhenReservationOwnedBySession_MarksReleasedRebroadcastsAndReturnsReleased()
        {
            // Arrange — an Active hold owned by session SessionS.
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            var sale = await SeedSaleAsync(context, productId: 1, stockAllocation: 100);
            var reservation = new InventoryReservation
            {
                FlashSaleId = sale.Id,
                ProductId = 1,
                Quantity = 10,
                SessionId = SessionS,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                Status = ReservationStatus.Active
            };
            context.InventoryReservations.Add(reservation);
            await context.SaveChangesAsync();
            var sut = CreateSut(context);

            // Act
            var outcome = await sut.ReleaseAsync(reservation.Id, SessionS);

            // Assert — released (not deleted): row stays but transitions to Released; freed stock rebroadcast.
            outcome.Should().Be(ReleaseOutcome.Released);
            context.InventoryReservations.Single().Status.Should().Be(ReservationStatus.Released);
            _coordinator.AvailabilityPublications.Should().ContainSingle().Which.Should().Be((1, 100));
        }

        [Fact]
        public async Task ReleaseAsync_WhenReservationMissing_ReturnsNotFoundAndDoesNotBroadcast()
        {
            // Arrange — no reservation with the requested id exists.
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            var sut = CreateSut(context);

            // Act
            var outcome = await sut.ReleaseAsync(9999, SessionS);

            // Assert
            outcome.Should().Be(ReleaseOutcome.NotFound);
            _coordinator.AvailabilityPublications.Should().BeEmpty();
        }

        [Fact]
        public async Task ReleaseAsync_WhenReservationOwnedByAnotherSession_ReturnsForbiddenAndDoesNotBroadcast()
        {
            // Arrange — a hold owned by SessionOwner; a different session attempts to release it.
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            var sale = await SeedSaleAsync(context, productId: 1, stockAllocation: 100);
            var reservation = new InventoryReservation
            {
                FlashSaleId = sale.Id,
                ProductId = 1,
                Quantity = 10,
                SessionId = SessionOwner,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                Status = ReservationStatus.Active
            };
            context.InventoryReservations.Add(reservation);
            await context.SaveChangesAsync();
            var sut = CreateSut(context);

            // Act — a caller can only release a hold it owns.
            var outcome = await sut.ReleaseAsync(reservation.Id, SessionAttacker);

            // Assert — forbidden, and the hold is left untouched (still Active), no broadcast.
            outcome.Should().Be(ReleaseOutcome.Forbidden);
            context.InventoryReservations.Single().Status.Should().Be(ReservationStatus.Active);
            _coordinator.AvailabilityPublications.Should().BeEmpty();
        }

        // ---------------------------------------------------------------------------------------------
        // ConsumeReservationsAsync — checkout hook (Active -> Consumed; never deletes)
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task ConsumeReservationsAsync_WhenSessionHasMatchingActiveReservations_MarksConsumedAndBroadcastsOncePerProduct()
        {
            // Arrange — two Active holds for the same session + product against a 100-unit sale.
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            var sale = await SeedSaleAsync(context, productId: 1, stockAllocation: 100);
            context.InventoryReservations.AddRange(
                new InventoryReservation { FlashSaleId = sale.Id, ProductId = 1, Quantity = 10, SessionId = SessionSess1, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5), Status = ReservationStatus.Active },
                new InventoryReservation { FlashSaleId = sale.Id, ProductId = 1, Quantity = 5, SessionId = SessionSess1, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5), Status = ReservationStatus.Active });
            await context.SaveChangesAsync();
            var sut = CreateSut(context);

            // Act — the ordered line matches product 1.
            await sut.ConsumeReservationsAsync(SessionSess1, new[] { new ReservationConsumeLine(1, 15) });

            // Assert — both holds transitioned to Consumed (never deleted); one broadcast per distinct product.
            // Consumed rows remain subtracted: 100 − (10 + 5) = 85 available.
            context.InventoryReservations.Count().Should().Be(2);
            context.InventoryReservations.Should().OnlyContain(r => r.Status == ReservationStatus.Consumed);
            _coordinator.AvailabilityPublications.Should().ContainSingle().Which.Should().Be((1, 85));
        }

        [Fact]
        public async Task ConsumeReservationsAsync_WhenNoMatchingReservations_IsNoOpAndDoesNotBroadcast()
        {
            // Arrange — an Active hold owned by a DIFFERENT session; consuming SessionNobody matches nothing.
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            var sale = await SeedSaleAsync(context, productId: 1, stockAllocation: 100);
            context.InventoryReservations.Add(new InventoryReservation
            {
                FlashSaleId = sale.Id,
                ProductId = 1,
                Quantity = 10,
                SessionId = SessionSomeoneElse,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                Status = ReservationStatus.Active
            });
            await context.SaveChangesAsync();
            var sut = CreateSut(context);

            // Act
            await sut.ConsumeReservationsAsync(SessionNobody, new[] { new ReservationConsumeLine(1, 10) });

            // Assert — the unrelated hold is untouched; no broadcast.
            context.InventoryReservations.Single().Status.Should().Be(ReservationStatus.Active);
            _coordinator.AvailabilityPublications.Should().BeEmpty();
        }

        [Fact]
        public async Task ConsumeReservationsAsync_WhenOrderedLinesNull_IsNoOpAndDoesNotThrowOrBroadcast()
        {
            // Arrange — invoked defensively after checkout; a null set must never throw or broadcast.
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            var sut = CreateSut(context);

            // Act
            Func<Task> act = async () => await sut.ConsumeReservationsAsync(SessionS, null);

            // Assert
            await act.Should().NotThrowAsync();
            _coordinator.AvailabilityPublications.Should().BeEmpty();
        }

        [Fact]
        public async Task ConsumeReservationsAsync_WhenOrderedLinesEmpty_IsNoOpAndDoesNotThrowOrBroadcast()
        {
            // Arrange — an empty ordered-line set is likewise a no-op.
            var dbName = Guid.NewGuid().ToString();
            using var context = TestStoreContextFactory.CreateInMemoryContext(dbName);
            var sut = CreateSut(context);

            // Act
            Func<Task> act = async () => await sut.ConsumeReservationsAsync(SessionS, new List<ReservationConsumeLine>());

            // Assert
            await act.Should().NotThrowAsync();
            _coordinator.AvailabilityPublications.Should().BeEmpty();
        }
    }
}
