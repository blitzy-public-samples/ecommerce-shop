using Core.Entities;
using FluentAssertions;
using Xunit;

namespace Core.Tests.Entities
{
    /// <summary>
    /// Pure unit tests (no mocking, no I/O) for the catalog entities
    /// <see cref="Product"/>, <see cref="ProductBrand"/> and <see cref="ProductType"/>.
    /// These entities are plain data holders that inherit <see cref="BaseEntity"/>
    /// (which supplies the <c>int Id</c> key), so the tests focus on:
    /// property round-trips, navigation-property assignment, <see cref="BaseEntity"/>
    /// inheritance, and default constructor state. Each test constructs fresh objects
    /// so the class carries no shared mutable state and is safe to run in parallel.
    /// </summary>
    public class ProductTests
    {
        [Fact]
        public void Properties_WhenSet_RoundTripValuesAndNavigation()
        {
            // Arrange
            var brand = new ProductBrand { Id = 1, Name = "Angular" };
            var type = new ProductType { Id = 2, Name = "Boards" };

            // Act
            var product = new Product
            {
                Id = 10,
                Name = "Core Board 500",
                Description = "A high performance board",
                Price = 180.00m,
                PictureUrl = "https://test.com/images/p.png",
                ProductType = type,
                ProductTypeId = 2,
                ProductBrand = brand,
                ProductBrandId = 1
            };

            // Assert
            product.Id.Should().Be(10);
            product.Name.Should().Be("Core Board 500");
            product.Description.Should().Be("A high performance board");
            product.Price.Should().Be(180.00m);
            product.PictureUrl.Should().Be("https://test.com/images/p.png");
            product.ProductType.Should().BeSameAs(type);
            product.ProductTypeId.Should().Be(2);
            product.ProductBrand.Should().BeSameAs(brand);
            product.ProductBrandId.Should().Be(1);
        }

        [Fact]
        public void Product_ShouldBeAssignableTo_BaseEntity()
        {
            // Arrange & Act
            var product = new Product();

            // Assert
            product.Should().BeAssignableTo<BaseEntity>();
        }

        [Fact]
        public void Constructor_Default_HasNullNavigationAndZeroIds()
        {
            // Arrange & Act
            var product = new Product();

            // Assert
            product.Id.Should().Be(0);
            product.Name.Should().BeNull();
            product.Description.Should().BeNull();
            product.PictureUrl.Should().BeNull();
            product.ProductType.Should().BeNull();
            product.ProductBrand.Should().BeNull();
            product.ProductTypeId.Should().Be(0);
            product.ProductBrandId.Should().Be(0);
        }

        [Fact]
        public void ProductBrand_Properties_RoundTrip()
        {
            // Arrange & Act
            var brand = new ProductBrand { Id = 3, Name = "NB" };

            // Assert
            brand.Id.Should().Be(3);
            brand.Name.Should().Be("NB");
            brand.Should().BeAssignableTo<BaseEntity>();
        }

        [Fact]
        public void ProductType_Properties_RoundTrip()
        {
            // Arrange & Act
            var type = new ProductType { Id = 4, Name = "Gloves" };

            // Assert
            type.Id.Should().Be(4);
            type.Name.Should().Be("Gloves");
            type.Should().BeAssignableTo<BaseEntity>();
        }
    }
}
