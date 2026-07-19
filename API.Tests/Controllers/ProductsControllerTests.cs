using System.Collections.Generic;
using System.Threading.Tasks;
using API.Controllers;
using API.Dtos;
using API.Errors;
using API.Helpers;          // Pagination<T>
using API.Specifications;   // ProductSpecParams, ISpecification<T>  (repository namespace quirk)
using AutoMapper;
using Core.Entities;
using Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace API.Tests.Controllers
{
    /// <summary>
    /// Unit tests for <see cref="ProductsController"/>.
    /// <para>
    /// The controller is exercised in isolation: it is instantiated directly with three
    /// Moq-mocked <see cref="IGenericRepository{T}"/> collaborators
    /// (<see cref="Product"/>, <see cref="ProductBrand"/>, <see cref="ProductType"/>) and a
    /// mocked <see cref="IMapper"/>. No ASP.NET Core pipeline is involved, therefore the
    /// <c>[Cached(600)]</c> action filter decorating every action does not execute during these
    /// direct in-process method calls and is intentionally ignored.
    /// </para>
    /// <para>
    /// The controller builds its concrete specification objects
    /// (<c>ProductsWithTypesAndBrandsSpecification</c> / <c>ProductWithFiltersForCountSpecification</c>)
    /// internally, so the repository mocks are configured against
    /// <c>It.IsAny&lt;ISpecification&lt;Product&gt;&gt;()</c>. Note that <c>ISpecification&lt;T&gt;</c>
    /// and <c>ProductSpecParams</c> are declared in <c>namespace API.Specifications</c> even though
    /// they physically reside in the Core project — hence the mandatory
    /// <c>using API.Specifications;</c> directive above.
    /// </para>
    /// <para>
    /// Tests follow the <c>MethodName_StateUnderTest_ExpectedBehavior</c> convention with an
    /// Arrange-Act-Assert structure, allocate fresh mocks per test (no shared mutable state), and
    /// use FluentAssertions for expressive assertions. No production code is modified.
    /// </para>
    /// </summary>
    public class ProductsControllerTests
    {
        // ------------------------------------------------------------------
        // GetProducts — happy path (pagination)
        // ------------------------------------------------------------------

        /// <summary>
        /// GetProducts must project the request's paging parameters and the repository's total
        /// count into the returned <see cref="Pagination{T}"/>, and surface the mapper output as
        /// the page <c>Data</c>, all wrapped in an <see cref="OkObjectResult"/>.
        /// </summary>
        [Fact]
        public async Task GetProducts_WithSpecParams_ReturnsOkWithPaginationFromRepoCountAndParams()
        {
            // Arrange
            var productsRepo = new Mock<IGenericRepository<Product>>();
            var brandRepo = new Mock<IGenericRepository<ProductBrand>>();
            var typeRepo = new Mock<IGenericRepository<ProductType>>();
            var mapper = new Mock<IMapper>();

            var productParams = new ProductSpecParams { PageIndex = 2, PageSize = 5 };
            var products = new List<Product> { new Product { Id = 1 }, new Product { Id = 2 } };
            IReadOnlyList<ProductToReturnDto> data = new List<ProductToReturnDto>
            {
                new ProductToReturnDto { Id = 1 },
                new ProductToReturnDto { Id = 2 }
            };
            const int total = 20;

            productsRepo
                .Setup(r => r.CountAsync(It.IsAny<ISpecification<Product>>()))
                .ReturnsAsync(total);
            productsRepo
                .Setup(r => r.ListAsync(It.IsAny<ISpecification<Product>>()))
                .ReturnsAsync(products);
            mapper
                .Setup(m => m.Map<IReadOnlyList<Product>, IReadOnlyList<ProductToReturnDto>>(products))
                .Returns(data);

            var controller = new ProductsController(
                productsRepo.Object, brandRepo.Object, typeRepo.Object, mapper.Object);

            // Act
            var result = await controller.GetProducts(productParams);

            // Assert
            var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
            var pagination = okResult.Value.Should().BeOfType<Pagination<ProductToReturnDto>>().Subject;
            pagination.PageIndex.Should().Be(2);
            pagination.PageSize.Should().Be(5);
            pagination.Count.Should().Be(total);
            pagination.Data.Should().BeSameAs(data);
            pagination.Data.Should().HaveCount(2);

            productsRepo.Verify(r => r.CountAsync(It.IsAny<ISpecification<Product>>()), Times.Once);
            productsRepo.Verify(r => r.ListAsync(It.IsAny<ISpecification<Product>>()), Times.Once);
        }

        // ------------------------------------------------------------------
        // GetProducts — edge case (no matching products)
        // ------------------------------------------------------------------

        /// <summary>
        /// When the repository yields no products, GetProducts must still return an
        /// <see cref="OkObjectResult"/> carrying a well-formed <see cref="Pagination{T}"/> with a
        /// zero <c>Count</c> and an empty <c>Data</c> collection.
        /// </summary>
        [Fact]
        public async Task GetProducts_WhenNoProductsMatch_ReturnsOkWithEmptyPaginationData()
        {
            // Arrange
            var productsRepo = new Mock<IGenericRepository<Product>>();
            var brandRepo = new Mock<IGenericRepository<ProductBrand>>();
            var typeRepo = new Mock<IGenericRepository<ProductType>>();
            var mapper = new Mock<IMapper>();

            var productParams = new ProductSpecParams { PageIndex = 1, PageSize = 6 };
            var products = new List<Product>();
            IReadOnlyList<ProductToReturnDto> data = new List<ProductToReturnDto>();

            productsRepo
                .Setup(r => r.CountAsync(It.IsAny<ISpecification<Product>>()))
                .ReturnsAsync(0);
            productsRepo
                .Setup(r => r.ListAsync(It.IsAny<ISpecification<Product>>()))
                .ReturnsAsync(products);
            mapper
                .Setup(m => m.Map<IReadOnlyList<Product>, IReadOnlyList<ProductToReturnDto>>(products))
                .Returns(data);

            var controller = new ProductsController(
                productsRepo.Object, brandRepo.Object, typeRepo.Object, mapper.Object);

            // Act
            var result = await controller.GetProducts(productParams);

            // Assert
            var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
            var pagination = okResult.Value.Should().BeOfType<Pagination<ProductToReturnDto>>().Subject;
            pagination.PageIndex.Should().Be(1);
            pagination.PageSize.Should().Be(6);
            pagination.Count.Should().Be(0);
            pagination.Data.Should().NotBeNull();
            pagination.Data.Should().BeEmpty();
        }

        // ------------------------------------------------------------------
        // GetProduct — success (direct value, not an ActionResult wrapper)
        // ------------------------------------------------------------------

        /// <summary>
        /// When a product exists, GetProduct returns the mapped <see cref="ProductToReturnDto"/>
        /// directly. Because the action returns the DTO (rather than <c>Ok(dto)</c>), the value is
        /// surfaced through <see cref="ActionResult{TValue}.Value"/> while
        /// <see cref="ActionResult{TValue}.Result"/> remains <c>null</c>.
        /// </summary>
        [Fact]
        public async Task GetProduct_WhenProductExists_ReturnsMappedProductToReturnDto()
        {
            // Arrange
            var productsRepo = new Mock<IGenericRepository<Product>>();
            var brandRepo = new Mock<IGenericRepository<ProductBrand>>();
            var typeRepo = new Mock<IGenericRepository<ProductType>>();
            var mapper = new Mock<IMapper>();

            var product = new Product { Id = 7 };
            var dto = new ProductToReturnDto { Id = 7 };

            productsRepo
                .Setup(r => r.GetEntityWithSpec(It.IsAny<ISpecification<Product>>()))
                .ReturnsAsync(product);
            mapper
                .Setup(m => m.Map<Product, ProductToReturnDto>(product))
                .Returns(dto);

            var controller = new ProductsController(
                productsRepo.Object, brandRepo.Object, typeRepo.Object, mapper.Object);

            // Act
            var result = await controller.GetProduct(7);

            // Assert
            result.Value.Should().BeSameAs(dto);
            result.Result.Should().BeNull();
            mapper.Verify(m => m.Map<Product, ProductToReturnDto>(product), Times.Once);
        }

        // ------------------------------------------------------------------
        // GetProduct — not found (404)
        // ------------------------------------------------------------------

        /// <summary>
        /// When the repository returns no product, GetProduct must respond with a
        /// <see cref="NotFoundObjectResult"/> whose body is an <see cref="ApiResponse"/> carrying
        /// status code 404 and the default "Resource not found" message.
        /// </summary>
        [Fact]
        public async Task GetProduct_WhenProductNotFound_ReturnsNotFoundWithApiResponse404()
        {
            // Arrange
            var productsRepo = new Mock<IGenericRepository<Product>>();
            var brandRepo = new Mock<IGenericRepository<ProductBrand>>();
            var typeRepo = new Mock<IGenericRepository<ProductType>>();
            var mapper = new Mock<IMapper>();

            productsRepo
                .Setup(r => r.GetEntityWithSpec(It.IsAny<ISpecification<Product>>()))
                .ReturnsAsync((Product)null);

            var controller = new ProductsController(
                productsRepo.Object, brandRepo.Object, typeRepo.Object, mapper.Object);

            // Act
            var result = await controller.GetProduct(999);

            // Assert
            var notFound = result.Result.Should().BeOfType<NotFoundObjectResult>().Subject;
            var apiResponse = notFound.Value.Should().BeOfType<ApiResponse>().Subject;
            apiResponse.StatusCode.Should().Be(404);
            apiResponse.Message.Should().Be("Resource not found");
            result.Value.Should().BeNull();
        }

        /// <summary>
        /// The 404 branch must short-circuit before mapping: when no product is found the mapper is
        /// never invoked. This isolates the failure branch of GetProduct from the success branch.
        /// </summary>
        [Fact]
        public async Task GetProduct_WhenProductNotFound_DoesNotInvokeMapper()
        {
            // Arrange
            var productsRepo = new Mock<IGenericRepository<Product>>();
            var brandRepo = new Mock<IGenericRepository<ProductBrand>>();
            var typeRepo = new Mock<IGenericRepository<ProductType>>();
            var mapper = new Mock<IMapper>();

            productsRepo
                .Setup(r => r.GetEntityWithSpec(It.IsAny<ISpecification<Product>>()))
                .ReturnsAsync((Product)null);

            var controller = new ProductsController(
                productsRepo.Object, brandRepo.Object, typeRepo.Object, mapper.Object);

            // Act
            await controller.GetProduct(123);

            // Assert
            mapper.Verify(
                m => m.Map<Product, ProductToReturnDto>(It.IsAny<Product>()),
                Times.Never);
        }

        // ------------------------------------------------------------------
        // GetProductBrands / GetProductTypes
        // ------------------------------------------------------------------

        /// <summary>
        /// GetProductBrands must return an <see cref="OkObjectResult"/> whose body is exactly the
        /// brand collection produced by the brand repository's <c>ListAllAsync</c>.
        /// </summary>
        [Fact]
        public async Task GetProductBrands_WhenCalled_ReturnsOkWithBrandList()
        {
            // Arrange
            var productsRepo = new Mock<IGenericRepository<Product>>();
            var brandRepo = new Mock<IGenericRepository<ProductBrand>>();
            var typeRepo = new Mock<IGenericRepository<ProductType>>();
            var mapper = new Mock<IMapper>();

            var brands = new List<ProductBrand> { new ProductBrand { Id = 1, Name = "Boards" } };
            brandRepo.Setup(r => r.ListAllAsync()).ReturnsAsync(brands);

            var controller = new ProductsController(
                productsRepo.Object, brandRepo.Object, typeRepo.Object, mapper.Object);

            // Act
            var result = await controller.GetProductBrands();

            // Assert
            var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
            okResult.Value.Should().BeSameAs(brands);
            var returned = okResult.Value.Should().BeAssignableTo<IReadOnlyList<ProductBrand>>().Subject;
            returned.Should().HaveCount(1);
            returned[0].Name.Should().Be("Boards");
            brandRepo.Verify(r => r.ListAllAsync(), Times.Once);
        }

        /// <summary>
        /// GetProductTypes must return an <see cref="OkObjectResult"/> whose body is exactly the
        /// type collection produced by the type repository's <c>ListAllAsync</c>.
        /// </summary>
        [Fact]
        public async Task GetProductTypes_WhenCalled_ReturnsOkWithTypeList()
        {
            // Arrange
            var productsRepo = new Mock<IGenericRepository<Product>>();
            var brandRepo = new Mock<IGenericRepository<ProductBrand>>();
            var typeRepo = new Mock<IGenericRepository<ProductType>>();
            var mapper = new Mock<IMapper>();

            var types = new List<ProductType> { new ProductType { Id = 1, Name = "Boards" } };
            typeRepo.Setup(r => r.ListAllAsync()).ReturnsAsync(types);

            var controller = new ProductsController(
                productsRepo.Object, brandRepo.Object, typeRepo.Object, mapper.Object);

            // Act
            var result = await controller.GetProductTypes();

            // Assert
            var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
            okResult.Value.Should().BeSameAs(types);
            var returned = okResult.Value.Should().BeAssignableTo<IReadOnlyList<ProductType>>().Subject;
            returned.Should().HaveCount(1);
            returned[0].Name.Should().Be("Boards");
            typeRepo.Verify(r => r.ListAllAsync(), Times.Once);
        }
    }
}
