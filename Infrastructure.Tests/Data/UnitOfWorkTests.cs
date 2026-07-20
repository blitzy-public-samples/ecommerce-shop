using System;
using System.Threading.Tasks;
using Core.Entities;
using Core.Entities.OrderAggregate;
using FluentAssertions;
using Infrastructure.Data;
using Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Infrastructure.Tests.Data
{
    /// <summary>
    /// Unit tests for <see cref="UnitOfWork"/> (AAP 0.5.1). Every test runs against a
    /// fresh EF Core InMemory <c>StoreContext</c> produced by
    /// <see cref="TestStoreContextFactory.CreateInMemoryContext"/>, so no test shares
    /// mutable state and the class is safe to run in parallel. The tests exercise all
    /// three public members of the unit of work — the repository factory/cache
    /// (<c>Repository&lt;T&gt;</c>), the atomic commit (<c>Complete</c>), and disposal
    /// (<c>Dispose</c>) — together with their branches, satisfying the >=70% line
    /// coverage target for the data-access layer (AAP 0.7.1).
    /// Conventions (AAP 0.10.2): MethodName_StateUnderTest_ExpectedBehavior naming,
    /// Arrange-Act-Assert structure, FluentAssertions, and one logical behavior per test.
    /// </summary>
    public class UnitOfWorkTests
    {
        /// <summary>
        /// Builds a <see cref="DeliveryMethod"/> for staging through a repository.
        /// DeliveryMethod is used in preference to Product deliberately: it is a
        /// relationally self-contained entity with no foreign keys, so the fixture is valid
        /// under every provider (the EF Core InMemory provider used here and the real
        /// PostgreSQL schema exercised by the integration suite). Foreign-key and
        /// relational-integrity behavior is proven separately against PostgreSQL and is
        /// intentionally out of scope for these provider-agnostic InMemory unit tests.
        /// </summary>
        private static DeliveryMethod MakeDeliveryMethod(string shortName, decimal price) =>
            new DeliveryMethod
            {
                ShortName = shortName,
                DeliveryTime = "1-2 Days",
                Description = shortName + " delivery",
                Price = price
            };

        [Fact]
        public void Repository_WhenCalledTwiceForSameType_ReturnsSameInstance()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateInMemoryContext();
            var unitOfWork = new UnitOfWork(context);

            // Act
            var first = unitOfWork.Repository<Product>();
            var second = unitOfWork.Repository<Product>();

            // Assert
            first.Should().BeSameAs(second);
        }

        [Fact]
        public void Repository_WhenCalledForDifferentTypes_ReturnsDifferentInstances()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateInMemoryContext();
            var unitOfWork = new UnitOfWork(context);

            // Act
            var productRepository = unitOfWork.Repository<Product>();
            var deliveryMethodRepository = unitOfWork.Repository<DeliveryMethod>();

            // Assert
            productRepository.Should().NotBeSameAs(deliveryMethodRepository);
        }

        [Fact]
        public async Task Complete_WhenChangesStaged_ReturnsNumberOfEntitiesWritten()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateInMemoryContext();
            var unitOfWork = new UnitOfWork(context);
            unitOfWork.Repository<DeliveryMethod>().Add(MakeDeliveryMethod("Complete Method", 12m));

            // Act
            var result = await unitOfWork.Complete();

            // Assert
            result.Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task Complete_WhenChangesStaged_ExclusivelyOwnsPersistence()
        {
            // Arrange - one InMemory store observed through independent context instances.
            // A shared database name lets separate contexts read the same backing store that
            // the unit of work writes through, so persistence is proven across a context
            // boundary rather than merely within the writer's own change tracker.
            var databaseName = Guid.NewGuid().ToString();
            using var writeContext = TestStoreContextFactory.CreateInMemoryContext(databaseName);
            var unitOfWork = new UnitOfWork(writeContext);
            unitOfWork.Repository<DeliveryMethod>().Add(MakeDeliveryMethod("Priority", 25m));

            // Assert (pre-Complete) - staging through the repository must NOT persist on its
            // own. An independent context sees an empty store, proving Repository<T>.Add does
            // not hide a SaveChanges call and that Complete() exclusively owns the commit.
            using (var beforeContext = TestStoreContextFactory.CreateInMemoryContext(databaseName))
            {
                var beforeComplete = await beforeContext.DeliveryMethods.ToListAsync();
                beforeComplete.Should().BeEmpty();
            }

            // Act - Complete() is the sole persistence operation.
            var written = await unitOfWork.Complete();

            // Assert (post-Complete) - the change is now visible to a fresh independent
            // context, confirming Complete() (and nothing before it) committed exactly one row.
            written.Should().Be(1);
            using (var afterContext = TestStoreContextFactory.CreateInMemoryContext(databaseName))
            {
                var afterComplete = await afterContext.DeliveryMethods.ToListAsync();
                afterComplete.Should().ContainSingle();
                afterComplete[0].ShortName.Should().Be("Priority");
            }
        }

        [Fact]
        public async Task Complete_WhenNoPendingChanges_ReturnsZero()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateInMemoryContext();
            var unitOfWork = new UnitOfWork(context);

            // Act
            var result = await unitOfWork.Complete();

            // Assert
            result.Should().Be(0);
        }

        [Fact]
        public async Task Dispose_WhenCalled_DisposesUnderlyingContext()
        {
            // Arrange
            var context = TestStoreContextFactory.CreateInMemoryContext();
            var unitOfWork = new UnitOfWork(context);

            // Act
            unitOfWork.Dispose();

            // Assert - the underlying context is disposed, so queries now throw.
            Func<Task> act = async () => await context.Set<Product>().ToListAsync();
            await act.Should().ThrowAsync<ObjectDisposedException>();
        }
    }
}
