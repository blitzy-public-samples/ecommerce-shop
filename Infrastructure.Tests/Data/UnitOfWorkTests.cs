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
        /// Builds a minimal, valid <see cref="Product"/> for staging through a repository.
        /// Only the columns needed to persist a bare product are populated; under the
        /// InMemory provider no foreign keys are enforced, so brand/type are omitted.
        /// </summary>
        private static Product MakeProduct(string name, decimal price) =>
            new Product
            {
                Name = name,
                Description = name + " description",
                PictureUrl = "images/products/" + name + ".png",
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
            unitOfWork.Repository<Product>().Add(MakeProduct("Complete Product", 12m));

            // Act
            var result = await unitOfWork.Complete();

            // Assert
            result.Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task Complete_WhenChangesStaged_PersistsRowsQueryableAfterwards()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateInMemoryContext();
            var unitOfWork = new UnitOfWork(context);
            unitOfWork.Repository<Product>().Add(MakeProduct("Persisted Product", 20m));

            // Act
            await unitOfWork.Complete();

            // Assert
            var all = await unitOfWork.Repository<Product>().ListAllAsync();
            all.Should().HaveCount(1);
            all[0].Name.Should().Be("Persisted Product");
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
