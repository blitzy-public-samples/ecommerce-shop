using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using API.IntegrationTests.Infrastructure;       // ContainerFixture — shared, real Testcontainers PG+Redis harness
using Core.Entities.OrderAggregate;              // Order, OrderItem, ProductItemOrdered, Address
using FluentAssertions;                          // Fluent, diagnostic assertions
using Infrastructure.Data;                       // StoreContext + UnitOfWork — the system under test
using Microsoft.EntityFrameworkCore;             // CountAsync, DbUpdateException
using Microsoft.Extensions.DependencyInjection;  // CreateScope, GetRequiredService
using Npgsql;                                     // PostgresException — SqlState / ConstraintName (MD-02)
using Xunit;

namespace API.IntegrationTests.Concurrency
{
    /// <summary>
    /// Integration tests proving the <b>transactional atomicity</b> of
    /// <see cref="Infrastructure.Data.UnitOfWork.Complete"/> <b>across multiple repositories</b>, exercised
    /// against a <b>real</b> PostgreSQL database provisioned by Testcontainers through this class's own
    /// <see cref="ContainerFixture"/> (an <c>IClassFixture</c> — CR-01) (AAP §0.10.1 — real infrastructure
    /// only; never the shared <c>docker-compose</c> stack, never a mock).
    ///
    /// <para>
    /// <b>SUT behaviour under test.</b> <c>UnitOfWork.Repository&lt;TEntity&gt;()</c> lazily creates and
    /// caches a <c>GenericRepository&lt;TEntity&gt;</c> that stages inserts on a single shared
    /// <see cref="StoreContext"/>; <c>UnitOfWork.Complete()</c> is simply
    /// <c>await _context.SaveChangesAsync()</c>. A single <c>SaveChangesAsync</c> executes inside <b>one
    /// implicit EF Core / Npgsql transaction</b>, so if ANY statement in the batch fails, EVERY pending
    /// insert staged through EVERY repository in that unit is rolled back atomically. These tests verify
    /// exactly that guarantee — a failure in one repository's insert must leave no orphaned rows from the
    /// other repository's insert.
    /// </para>
    ///
    /// <para>
    /// <b>Deterministic failure strategy.</b> The failure is forced with a genuine PostgreSQL
    /// <b>foreign-key violation (SQLSTATE 23503)</b>. The migration ground truth
    /// (<c>Infrastructure/Data/Migrations/*_PostGres initial.cs</c>) declares a NULLABLE
    /// <c>DeliveryMethodId</c> <b>shadow</b> foreign key on <c>Orders</c> with constraint
    /// <c>FK_Orders_DeliveryMethods_DeliveryMethodId</c> (<c>onDelete: Restrict</c>). The
    /// <see cref="Order"/> CLR type has NO explicit <c>DeliveryMethodId</c> property — it is populated
    /// from the <c>DeliveryMethod</c> navigation. By staging an <see cref="Order"/> whose shadow
    /// <c>DeliveryMethodId</c> points at an id that does NOT exist, the INSERT is rejected by the database
    /// and surfaces as a <see cref="DbUpdateException"/> (Npgsql wraps the underlying
    /// <c>PostgresException</c>). Staging a perfectly valid <b>standalone</b> <see cref="OrderItem"/>
    /// (whose <c>OrderId</c> foreign key is nullable) through a SECOND repository in the SAME unit proves
    /// the rollback spans repositories, not just the failing entity.
    /// </para>
    ///
    /// <para>
    /// <b>Why the sibling is an <see cref="OrderItem"/> and not a <c>DeliveryMethod</c>.</b> The
    /// <c>Orders</c> and <c>OrderItems</c> tables are NOT seeded, so their PostgreSQL identity sequences
    /// are in sync and a freshly-inserted row receives a brand-new, non-colliding key. The seeded tables
    /// (<c>DeliveryMethods</c>, <c>Products</c>, brands, types) were populated from JSON with EXPLICIT ids
    /// under an <c>IDENTITY BY DEFAULT</c> column, which does not advance the sequence — so inserting a NEW
    /// row into any of them would collide on the primary key (SQLSTATE 23505) rather than commit cleanly.
    /// Using an unseeded entity for the "valid" inserts keeps the only intentional failure the order's FK
    /// violation, so the rollback assertions are unambiguous.
    /// </para>
    ///
    /// <para>
    /// <b>DELTA assertions + deterministic cleanup.</b> Under CR-01 this class owns its own isolated,
    /// disposable database (an <c>IClassFixture&lt;ContainerFixture&gt;</c>), but its two tests still share
    /// that one database and xUnit runs a class's tests sequentially. To stay robust to execution order and
    /// re-runs these tests therefore NEVER assert absolute row counts; they compare a <i>before</i> snapshot
    /// to an <i>after</i> snapshot (taken in a FRESH scope, so no stale change-tracking masks the persisted
    /// truth) and assert the deltas. The positive-control test additionally removes the row it commits in a
    /// <c>finally</c> block (MJ-04 deterministic cleanup) so the sibling test's before/after deltas are
    /// unaffected regardless of intra-class execution order.
    /// </para>
    ///
    /// <para>
    /// Naming follows the repository convention <c>MethodName_StateUnderTest_ExpectedBehavior</c>, with an
    /// Arrange-Act-Assert structure and FluentAssertions. No production code is modified; no
    /// <c>Thread.Sleep</c> or retry loop is used (a running Docker daemon is required for Testcontainers).
    /// </para>
    /// </summary>
    public class UnitOfWorkAtomicityTests : IClassFixture<ContainerFixture>
    {
        /// <summary>
        /// A delivery-method identifier that is guaranteed NOT to exist in the seeded
        /// <c>DeliveryMethods</c> table (which is seeded with the low ids 1-4). Pointing an order's shadow
        /// <c>DeliveryMethodId</c> at this value triggers the
        /// <c>FK_Orders_DeliveryMethods_DeliveryMethodId</c> foreign-key violation on commit.
        /// </summary>
        private const int NonExistentDeliveryMethodId = 999999;

        /// <summary>
        /// The name of the SHADOW foreign-key property EF Core maintains on <see cref="Order"/> for the
        /// delivery-method relationship. There is no matching CLR property, so it can only be addressed by
        /// name via <c>ChangeTracker</c>'s <c>Entry(...).Property(name)</c> API.
        /// </summary>
        private const string DeliveryMethodIdShadowProperty = "DeliveryMethodId";

        /// <summary>
        /// This class's dedicated fixture exposing the wired <c>CustomWebApplicationFactory</c> (and thus
        /// the application's real service provider) over the Testcontainers PostgreSQL/Redis endpoints.
        /// Injected by xUnit because this class implements <c>IClassFixture&lt;ContainerFixture&gt;</c>.
        /// </summary>
        private readonly ContainerFixture _fixture;

        /// <summary>
        /// xUnit injects this class's dedicated <see cref="ContainerFixture"/>
        /// (<c>IClassFixture&lt;ContainerFixture&gt;</c>) so this class runs against its own already-started,
        /// migrated and seeded containers.
        /// </summary>
        /// <param name="fixture">This class's isolated container fixture.</param>
        public UnitOfWorkAtomicityTests(ContainerFixture fixture)
        {
            _fixture = fixture;
        }

        /// <summary>
        /// Reads the current persisted row counts for <c>Orders</c>, <c>OrderItems</c> and
        /// <c>DeliveryMethods</c> using a FRESH DI scope (hence a fresh <see cref="StoreContext"/> with an
        /// empty change tracker), so the snapshot reflects only what is actually committed to PostgreSQL —
        /// never entities left in a failed unit's tracker. Callers compare two snapshots and assert on the
        /// deltas, which is safe on the database shared across the collection.
        /// </summary>
        /// <returns>A tuple of the committed row counts at the moment of the call.</returns>
        private async Task<(int orders, int orderItems, int deliveryMethods)> SnapshotCountsAsync()
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            return (
                await context.Orders.CountAsync(),
                await context.OrderItems.CountAsync(),
                await context.DeliveryMethods.CountAsync());
        }

        /// <summary>
        /// When a single <see cref="UnitOfWork"/> stages an INVALID insert through one repository (an
        /// <see cref="Order"/> whose shadow <c>DeliveryMethodId</c> references a non-existent delivery
        /// method) alongside a perfectly VALID insert through a SECOND repository (a standalone
        /// <see cref="OrderItem"/>), calling <c>Complete()</c> must throw a <see cref="DbUpdateException"/>
        /// and roll back the <b>entire</b> unit — leaving no orphaned <c>Orders</c>/<c>OrderItems</c> AND
        /// not committing the valid sibling row — because <c>SaveChangesAsync</c> runs as one atomic
        /// transaction.
        /// </summary>
        [Fact]
        public async Task Complete_WhenOneEntityViolatesForeignKey_RollsBackAllInsertsAcrossRepositories()
        {
            // Arrange — baseline snapshot captured BEFORE staging anything, in its own fresh scope.
            var before = await SnapshotCountsAsync();

            // Stage two inserts through TWO repositories on ONE unit of work over the REAL StoreContext.
            using (var scope = _fixture.Factory.Services.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

                // Exercise the UnitOfWork class directly over the real, scoped context. The scope owns the
                // context's lifetime, so the UnitOfWork is intentionally NOT disposed here (that would
                // dispose the context out from under the scope).
                var uow = new UnitOfWork(context);

                // EF Core requires the owned ShipToAddress (on Order) and ItemOrdered (on OrderItem) to be
                // non-null, so build a fully-valid owned graph. Constructor argument order verified against
                // Core/Entities/OrderAggregate: Address(firstName,lastName,street,city,state,zipCode),
                // ProductItemOrdered(productItemId,productName,pictureUrl), OrderItem(itemOrdered,price,quantity),
                // Order(orderItems,buyerEmail,shipToAddress,deliveryMethod,subtotal,paymentId).
                var shipToAddress = new Address("Bob", "Tester", "10 High St", "Testville", "TS", "12345");
                var itemOrdered = new ProductItemOrdered(1, "Atomicity Test Product", "test.png");
                var orderItem = new OrderItem(itemOrdered, 10m, 1);

                // The delivery-method navigation is deliberately null; the shadow FK is set explicitly below.
                var invalidOrder = new Order(
                    new List<OrderItem> { orderItem },
                    "atomicity@test.com",
                    shipToAddress,
                    deliveryMethod: null,
                    subtotal: 10m,
                    paymentId: $"pi_atomicity_{Guid.NewGuid():N}");

                uow.Repository<Order>().Add(invalidOrder);

                // Point the SHADOW foreign key (no CLR property exists) at an id that is absent from
                // DeliveryMethods. This is the deterministic trigger for FK violation 23503 on commit.
                context.Entry(invalidOrder)
                       .Property(DeliveryMethodIdShadowProperty)
                       .CurrentValue = NonExistentDeliveryMethodId;

                // VALID sibling insert through a SECOND repository in the SAME unit: a standalone
                // OrderItem (its OrderId FK is nullable, and OrderItems is unseeded so its identity
                // sequence is in sync — this row would commit cleanly on its own). If atomicity holds it
                // must be rolled back together with the failing order, proving the rollback spans
                // repositories rather than just the failing entity.
                var siblingItemOrdered = new ProductItemOrdered(2, "Sibling Probe Product", "sibling.png");
                var validSiblingOrderItem = new OrderItem(siblingItemOrdered, 20m, 2);
                uow.Repository<OrderItem>().Add(validSiblingOrderItem);

                // Act & Assert (failure surfaced) — one SaveChangesAsync => one transaction. The order's FK
                // violation is the only possible failure (the sibling is valid), and it must bubble up as a
                // DbUpdateException; it is captured (not swallowed) so its ROOT CAUSE can be interrogated.
                Func<Task> act = async () => await uow.Complete();
                var thrown = await act.Should().ThrowAsync<DbUpdateException>(
                    "the order's DeliveryMethodId references a non-existent DeliveryMethod, so PostgreSQL " +
                    "rejects the INSERT with foreign-key violation 23503");

                // MD-02: assert the failure is PRECISELY the intended delivery-method foreign-key violation,
                // not some incidental error that merely happens to surface as a DbUpdateException. Npgsql
                // wraps the underlying database failure as a PostgresException on InnerException, exposing
                // both the SQLSTATE code and the exact violated constraint name — so both are asserted.
                var postgresException = thrown.Which.InnerException
                    .Should().BeOfType<PostgresException>(
                        "Npgsql surfaces the underlying database failure as the DbUpdateException's inner " +
                        "PostgresException")
                    .Which;
                postgresException.SqlState.Should().Be("23503",
                    "SQLSTATE 23503 is PostgreSQL's foreign-key-violation code " +
                    "(Npgsql PostgresErrorCodes.ForeignKeyViolation)");
                postgresException.ConstraintName.Should().Be("FK_Orders_DeliveryMethods_DeliveryMethodId",
                    "the violated constraint is the Orders->DeliveryMethods shadow foreign key, confirming " +
                    "the failure is the intended non-existent delivery-method reference and not an " +
                    "unrelated constraint");
            }

            // Assert (rollback / no orphans) — snapshot AFTER in a FRESH scope so the persisted truth is
            // read, not the failed unit's stale tracker. Because SaveChangesAsync is a single transaction,
            // the FK violation on the order rolls back the valid sibling OrderItem too — proving atomicity
            // ACROSS repositories. Had atomicity been broken, the valid sibling would have leaked and the
            // OrderItems delta would be +1 rather than 0.
            var after = await SnapshotCountsAsync();

            after.orders.Should().Be(before.orders,
                "the failing order must not be persisted (no orphaned order)");
            after.orderItems.Should().Be(before.orderItems,
                "the failing order's own item AND the valid sibling item staged in the same unit must both " +
                "be rolled back (no orphaned order items)");
            after.deliveryMethods.Should().Be(before.deliveryMethods,
                "the unit staged no delivery methods, so their count is unchanged");
        }

        /// <summary>
        /// Positive control: when every entity staged in the unit is VALID, <c>Complete()</c> commits them
        /// and returns a positive write count. This guards against a false positive in the rollback test —
        /// it proves the SAME code path (<see cref="UnitOfWork"/> + repository <c>Add</c> + <c>Complete</c>)
        /// genuinely persists rows when nothing violates a constraint, so the rollback test's zero-delta
        /// result is meaningful (something really was being inserted) rather than a no-op. The committed
        /// order is removed afterwards to keep this class's database pristine.
        /// </summary>
        [Fact]
        public async Task Complete_WhenAllEntitiesValid_CommitsAllAndReturnsPositive()
        {
            // Arrange — baseline snapshot in its own fresh scope.
            var before = await SnapshotCountsAsync();

            // Primary key of the order committed by this test, captured for deterministic cleanup.
            var committedOrderId = 0;

            using (var scope = _fixture.Factory.Services.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<StoreContext>();
                var uow = new UnitOfWork(context);

                // A fully-valid order with a NULL delivery method (the shadow FK column is nullable, so the
                // row commits cleanly) and one order item. This mirrors the rollback test's graph WITHOUT
                // the poisoned shadow FK, proving the very same Complete() path persists when nothing
                // violates a constraint. Orders/OrderItems are unseeded, so their keys never collide.
                var shipToAddress = new Address("Val", "Id", "1 Valid Way", "Commitville", "CV", "00001");
                var itemOrdered = new ProductItemOrdered(1, "Positive Control Product", "ok.png");
                var orderItem = new OrderItem(itemOrdered, 5m, 1);

                var validOrder = new Order(
                    new List<OrderItem> { orderItem },
                    "positive-control@test.com",
                    shipToAddress,
                    deliveryMethod: null,
                    subtotal: 5m,
                    paymentId: $"pi_positive_{Guid.NewGuid():N}");

                uow.Repository<Order>().Add(validOrder);

                // Act — the identical Complete() path the rollback test uses, but with valid data.
                var writeCount = await uow.Complete();

                // Assert — a successful SaveChangesAsync reports the number of state entries written.
                writeCount.Should().BeGreaterThan(0,
                    "a successful Complete() returns the number of state entries written to the database");

                // EF Core has populated the identity key after the commit; record it for cleanup.
                committedOrderId = validOrder.Id;
            }

            try
            {
                // Assert (commit) — snapshot AFTER in a FRESH scope; exactly one order and one order item
                // were committed and no delivery methods were touched by this unit.
                var after = await SnapshotCountsAsync();

                after.orders.Should().Be(before.orders + 1,
                    "the valid order was committed");
                after.orderItems.Should().Be(before.orderItems + 1,
                    "the valid order's single item was committed");
                after.deliveryMethods.Should().Be(before.deliveryMethods,
                    "this unit staged no delivery methods");
            }
            finally
            {
                // MJ-04 deterministic cleanup: remove the committed order so this class's own database is
                // restored between its sequentially-run tests, keeping the sibling test's before/after
                // deltas unaffected regardless of intra-class execution order.
                await CleanupOrderAsync(committedOrderId);
            }
        }

        /// <summary>
        /// Removes the given order (by primary key) in a fresh scope, restoring this class's database to its
        /// pre-test state. Deleting the order also removes its order items: the
        /// <c>FK_OrderItems_Orders_OrderId</c> constraint is declared <c>onDelete: Cascade</c>, so
        /// PostgreSQL cascades the delete.
        /// </summary>
        /// <param name="orderId">Primary key of the order to remove; <c>0</c> (never committed) is a no-op.</param>
        private async Task CleanupOrderAsync(int orderId)
        {
            if (orderId == 0)
            {
                return;
            }

            using var scope = _fixture.Factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<StoreContext>();

            var order = await context.Orders.FindAsync(orderId);
            if (order == null)
            {
                return;
            }

            context.Orders.Remove(order);
            await context.SaveChangesAsync();
        }
    }
}
