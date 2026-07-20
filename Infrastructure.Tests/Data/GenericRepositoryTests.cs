using System.Threading.Tasks;
using API.Specifications;
using Core.Entities;
using FluentAssertions;
using Infrastructure.Data;
using Infrastructure.Tests.Helpers;
using Xunit;

namespace Infrastructure.Tests.Data
{
    public class GenericRepositoryTests
    {
        private static Product MakeProduct(string name, decimal price) =>
            new Product
            {
                Name = name,
                Description = name + " description",
                PictureUrl = "images/products/" + name + ".png",
                Price = price
            };

        [Fact]
        public async Task GetByIdAsync_WhenEntityExists_ReturnsEntity()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateInMemoryContext();
            var seeded = await TestStoreContextFactory.SeedProductsAsync(context, 3);
            var repository = new GenericRepository<Product>(context);
            var target = seeded[0];

            // Act
            var result = await repository.GetByIdAsync(target.Id);

            // Assert
            result.Should().NotBeNull();
            result.Id.Should().Be(target.Id);
            result.Name.Should().Be(target.Name);
        }

        [Fact]
        public async Task GetByIdAsync_WhenEntityDoesNotExist_ReturnsNull()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateInMemoryContext();
            var repository = new GenericRepository<Product>(context);

            // Act
            var result = await repository.GetByIdAsync(999);

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task ListAllAsync_WhenEntitiesExist_ReturnsAllEntities()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateInMemoryContext();
            await TestStoreContextFactory.SeedProductsAsync(context, 3);
            var repository = new GenericRepository<Product>(context);

            // Act
            var result = await repository.ListAllAsync();

            // Assert
            result.Should().HaveCount(3);
        }

        [Fact]
        public async Task ListAllAsync_WhenNoEntities_ReturnsEmptyList()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateInMemoryContext();
            var repository = new GenericRepository<Product>(context);

            // Act
            var result = await repository.ListAllAsync();

            // Assert
            result.Should().BeEmpty();
        }

        [Fact]
        public async Task Add_WhenSaveChangesCalled_PersistsEntity()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateInMemoryContext();
            var repository = new GenericRepository<Product>(context);
            var product = MakeProduct("New Product", 15m);

            // Act
            repository.Add(product);
            await context.SaveChangesAsync();

            // Assert
            var all = await repository.ListAllAsync();
            all.Should().HaveCount(1);
            all[0].Name.Should().Be("New Product");
        }

        [Fact]
        public async Task Update_WhenEntityModified_PersistsChanges()
        {
            // Arrange - seed with one context, update a DETACHED entity with a second
            // context sharing the same InMemory store, then verify with a third context.
            var dbName = System.Guid.NewGuid().ToString();
            int id;
            int brandId;
            int typeId;
            using (var seedContext = TestStoreContextFactory.CreateInMemoryContext(dbName))
            {
                var seeded = await TestStoreContextFactory.SeedProductsAsync(seedContext, 1);
                id = seeded[0].Id;
                brandId = seeded[0].ProductBrandId;
                typeId = seeded[0].ProductTypeId;
            }

            // Act
            using (var updateContext = TestStoreContextFactory.CreateInMemoryContext(dbName))
            {
                var repository = new GenericRepository<Product>(updateContext);
                var detached = new Product
                {
                    Id = id,
                    Name = "Updated Name",
                    Description = "Updated description",
                    PictureUrl = "images/products/updated.png",
                    Price = 999.99m,
                    ProductBrandId = brandId,
                    ProductTypeId = typeId
                };
                repository.Update(detached);
                await updateContext.SaveChangesAsync();
            }

            // Assert
            using (var verifyContext = TestStoreContextFactory.CreateInMemoryContext(dbName))
            {
                var repository = new GenericRepository<Product>(verifyContext);
                var reloaded = await repository.GetByIdAsync(id);
                reloaded.Should().NotBeNull();
                reloaded.Name.Should().Be("Updated Name");
                reloaded.Price.Should().Be(999.99m);
            }
        }

        [Fact]
        public async Task Delete_WhenEntityExists_RemovesEntity()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateInMemoryContext();
            var seeded = await TestStoreContextFactory.SeedProductsAsync(context, 1);
            var repository = new GenericRepository<Product>(context);
            var entity = await repository.GetByIdAsync(seeded[0].Id);

            // Act
            repository.Delete(entity);
            await context.SaveChangesAsync();

            // Assert
            (await repository.GetByIdAsync(seeded[0].Id)).Should().BeNull();
            (await repository.ListAllAsync()).Should().BeEmpty();
        }

        [Fact]
        public async Task GetEntityWithSpec_WhenSpecMatches_ReturnsMatchingEntity()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateInMemoryContext();
            var seeded = await TestStoreContextFactory.SeedProductsAsync(context, 3);
            var repository = new GenericRepository<Product>(context);
            var target = seeded[1];
            var spec = new BaseSpecification<Product>(p => p.Id == target.Id);

            // Act
            var result = await repository.GetEntityWithSpec(spec);

            // Assert
            result.Should().NotBeNull();
            result.Id.Should().Be(target.Id);
            result.Name.Should().Be(target.Name);
        }

        [Fact]
        public async Task ListAsync_WhenSpecMatches_ReturnsMatchingEntities()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateInMemoryContext();
            var seeded = await TestStoreContextFactory.SeedProductsAsync(context, 3);
            var repository = new GenericRepository<Product>(context);
            var typeId = seeded[0].ProductTypeId; // factory seeds share one ProductType
            var spec = new BaseSpecification<Product>(p => p.ProductTypeId == typeId);

            // Act
            var result = await repository.ListAsync(spec);

            // Assert
            result.Should().HaveCount(3);
            result.Should().OnlyContain(p => p.ProductTypeId == typeId);
        }

        [Fact]
        public async Task CountAsync_WhenSpecMatches_ReturnsMatchCount()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateInMemoryContext();
            var seeded = await TestStoreContextFactory.SeedProductsAsync(context, 3);
            var repository = new GenericRepository<Product>(context);
            var typeId = seeded[0].ProductTypeId;
            var spec = new BaseSpecification<Product>(p => p.ProductTypeId == typeId);

            // Act
            var count = await repository.CountAsync(spec);

            // Assert
            count.Should().Be(3);
        }
    }
}
