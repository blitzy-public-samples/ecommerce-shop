using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using API.Specifications;
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
    /// Unit tests for <see cref="SpecificationEvaluator{TEntity}"/>, the shared query-translation
    /// engine that <c>GenericRepository&lt;T&gt;</c> delegates to. Its single static method
    /// <c>GetQuery(IQueryable, ISpecification)</c> composes a query from a specification through
    /// five independent branches: Criteria (Where), OrderBy, OrderByDescending, paging (Skip/Take),
    /// and Includes. Each test below drives exactly one of those branches so coverage of every
    /// branch is explicit (AAP 0.5.1, coverage target &gt;=70% line, AAP 0.7.1).
    ///
    /// Design notes (verified against the production source):
    ///   - Namespace quirk (AAP 0.4.4/0.6.2): the production specifications live in
    ///     <c>namespace API.Specifications</c> (NOT Core.Specifications), so this file references
    ///     <see cref="BaseSpecification{T}"/>, <see cref="ISpecification{T}"/>, and
    ///     <see cref="OrdersWithItemsAndOrderingSpecification"/> via <c>using API.Specifications;</c>.
    ///   - <see cref="BaseSpecification{T}"/>'s ordering/paging helpers are <c>protected</c>, so the
    ///     ordering and paging branches are configured through a small test-only subclass
    ///     (<c>TestProductSpecification</c>) that surfaces those members via public wrappers.
    ///   - The EF Core InMemory provider is used through
    ///     <see cref="TestStoreContextFactory.CreateInMemoryContext"/>: a fresh, isolated store per
    ///     test (default GUID db name) guarantees no shared mutable state and parallel safety.
    ///
    /// Conventions (AAP 0.10.2): MethodName_StateUnderTest_ExpectedBehavior, Arrange-Act-Assert,
    /// and FluentAssertions 6.12.0. No real infrastructure, no production-code changes.
    /// </summary>
    public class SpecificationEvaluatorTests
    {
        // Test-only specification that surfaces BaseSpecification's protected
        // configuration helpers (AddOrderBy/AddOrderByDescending/ApplyPaging are
        // protected). Declared here so this file's `using API.Specifications;`
        // resolves the base type (namespace quirk: production specs live in
        // namespace API.Specifications, NOT Core.Specifications).
        private class TestProductSpecification : BaseSpecification<Product>
        {
            public TestProductSpecification()
            {
            }

            public TestProductSpecification(Expression<Func<Product, bool>> criteria) : base(criteria)
            {
            }

            public TestProductSpecification WithOrderBy(Expression<Func<Product, object>> orderBy)
            {
                AddOrderBy(orderBy);
                return this;
            }

            public TestProductSpecification WithOrderByDescending(Expression<Func<Product, object>> orderByDesc)
            {
                AddOrderByDescending(orderByDesc);
                return this;
            }

            public TestProductSpecification WithPaging(int skip, int take)
            {
                ApplyPaging(skip, take);
                return this;
            }
        }

        private static Product MakeProduct(string name, decimal price, int typeId = 1, int brandId = 1) =>
            new Product
            {
                Name = name,
                Description = name + " description",
                PictureUrl = "images/products/" + name + ".png",
                Price = price,
                ProductTypeId = typeId,
                ProductBrandId = brandId
            };

        [Fact]
        public async Task GetQuery_WithCriteria_ReturnsOnlyMatchingEntities()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateInMemoryContext();
            context.Products.AddRange(
                MakeProduct("Alpha", 10m, typeId: 1),
                MakeProduct("Bravo", 20m, typeId: 1),
                MakeProduct("Charlie", 30m, typeId: 2));
            await context.SaveChangesAsync();
            var spec = new BaseSpecification<Product>(p => p.ProductTypeId == 1);

            // Act
            var result = await SpecificationEvaluator<Product>
                .GetQuery(context.Set<Product>().AsQueryable(), spec)
                .ToListAsync();

            // Assert
            result.Should().HaveCount(2);
            result.Should().OnlyContain(p => p.ProductTypeId == 1);
        }

        [Fact]
        public async Task GetQuery_WithOrderBy_ReturnsEntitiesInAscendingOrder()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateInMemoryContext();
            context.Products.AddRange(
                MakeProduct("Product C", 30m),
                MakeProduct("Product A", 10m),
                MakeProduct("Product B", 20m));
            await context.SaveChangesAsync();
            var spec = new TestProductSpecification().WithOrderBy(p => p.Price);

            // Act
            var result = await SpecificationEvaluator<Product>
                .GetQuery(context.Set<Product>().AsQueryable(), spec)
                .ToListAsync();

            // Assert
            result.Should().HaveCount(3);
            result.Should().BeInAscendingOrder(p => p.Price);
            result.Select(p => p.Price).Should().ContainInOrder(10m, 20m, 30m);
        }

        [Fact]
        public async Task GetQuery_WithOrderByDescending_ReturnsEntitiesInDescendingOrder()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateInMemoryContext();
            context.Products.AddRange(
                MakeProduct("Product A", 10m),
                MakeProduct("Product C", 30m),
                MakeProduct("Product B", 20m));
            await context.SaveChangesAsync();
            var spec = new TestProductSpecification().WithOrderByDescending(p => p.Price);

            // Act
            var result = await SpecificationEvaluator<Product>
                .GetQuery(context.Set<Product>().AsQueryable(), spec)
                .ToListAsync();

            // Assert
            result.Should().HaveCount(3);
            result.Should().BeInDescendingOrder(p => p.Price);
            result.Select(p => p.Price).Should().ContainInOrder(30m, 20m, 10m);
        }

        [Fact]
        public async Task GetQuery_WithPagingEnabled_ReturnsRequestedWindow()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateInMemoryContext();
            context.Products.AddRange(
                MakeProduct("P1", 10m),
                MakeProduct("P2", 20m),
                MakeProduct("P3", 30m),
                MakeProduct("P4", 40m),
                MakeProduct("P5", 50m));
            await context.SaveChangesAsync();
            // Order by price ascending, then Skip(1).Take(2) => prices 20, 30.
            var spec = new TestProductSpecification().WithOrderBy(p => p.Price).WithPaging(1, 2);

            // Act
            var result = await SpecificationEvaluator<Product>
                .GetQuery(context.Set<Product>().AsQueryable(), spec)
                .ToListAsync();

            // Assert
            result.Should().HaveCount(2);
            result.Select(p => p.Price).Should().ContainInOrder(20m, 30m);
        }

        [Fact]
        public async Task GetQuery_WithPagingDisabled_ReturnsAllEntities()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateInMemoryContext();
            var seeded = await TestStoreContextFactory.SeedProductsAsync(context, 5);
            var spec = new BaseSpecification<Product>(); // no criteria/order/paging/includes

            // Act
            var result = await SpecificationEvaluator<Product>
                .GetQuery(context.Set<Product>().AsQueryable(), spec)
                .ToListAsync();

            // Assert
            result.Should().HaveCount(seeded.Count);
        }

        [Fact]
        public async Task GetQuery_WithIncludes_PopulatesNavigationProperties()
        {
            // Arrange - seed with one context, query with a second context that shares
            // the same InMemory store, so the query-side change tracker is cold. This
            // proves the Include (not identity-map fixup) populates the navigations.
            var dbName = Guid.NewGuid().ToString();
            using (var seedContext = TestStoreContextFactory.CreateInMemoryContext(dbName))
            {
                var deliveryMethod = new DeliveryMethod
                {
                    ShortName = "UPS1",
                    DeliveryTime = "1-2 Days",
                    Description = "Fast delivery",
                    Price = 10m
                };
                var items = new List<OrderItem>
                {
                    new OrderItem(new ProductItemOrdered(1, "Test Product", "test.png"), 100m, 2)
                };
                var address = new Address("Bob", "Bobbity", "10 The Street", "New York", "NY", "90210");
                var order = new Order(items, "bob@test.com", address, deliveryMethod, 200m, "pi_123");
                seedContext.Orders.Add(order);
                await seedContext.SaveChangesAsync();
            }

            using var queryContext = TestStoreContextFactory.CreateInMemoryContext(dbName);
            var spec = new OrdersWithItemsAndOrderingSpecification("bob@test.com");

            // Act
            var result = await SpecificationEvaluator<Order>
                .GetQuery(queryContext.Set<Order>().AsQueryable(), spec)
                .ToListAsync();

            // Assert
            result.Should().ContainSingle();
            result[0].OrderItems.Should().NotBeNullOrEmpty();
            result[0].DeliveryMethod.Should().NotBeNull();
            result[0].BuyerEmail.Should().Be("bob@test.com");
        }
    }
}
