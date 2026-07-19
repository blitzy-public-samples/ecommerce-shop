using System.Threading.Tasks;
using API.Controllers;
using API.Dtos;
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
    /// Unit tests for <see cref="BasketController"/>.
    /// <para>
    /// The controller is exercised in complete isolation: its two collaborators
    /// (<see cref="IBasketRepository"/> and <see cref="IMapper"/>) are replaced with
    /// Moq test doubles, so no Redis, HTTP pipeline, or AutoMapper configuration is
    /// touched. This keeps the tests fast, deterministic, and focused purely on the
    /// controller's own branching/return logic.
    /// </para>
    /// <para>
    /// Conventions (per AAP §0.10):
    /// <list type="bullet">
    ///   <item><description>Naming: <c>MethodName_StateUnderTest_ExpectedBehavior</c>.</description></item>
    ///   <item><description>Structure: Arrange-Act-Assert with explicit markers.</description></item>
    ///   <item><description>Isolation: a <b>fresh</b> set of mocks is built for every test via
    ///   <see cref="CreateController"/> so no mutable state is shared between tests.</description></item>
    ///   <item><description>Assertions: FluentAssertions throughout.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Result-type note: every read action wraps its payload in <c>Ok(...)</c>, which
    /// populates <see cref="ActionResult{TValue}.Result"/> with an
    /// <see cref="OkObjectResult"/> (it does <b>not</b> populate <c>.Value</c>). The
    /// payload is therefore read from <c>((OkObjectResult)result.Result).Value</c>.
    /// </para>
    /// </summary>
    public class BasketControllerTests
    {
        /// <summary>
        /// Builds a <see cref="BasketController"/> wired to a brand-new pair of mocks.
        /// A new instance is created on every invocation, guaranteeing that individual
        /// tests never share mutable mock state (AAP §0.7.2 test isolation).
        /// </summary>
        /// <returns>
        /// A tuple of the controller under test together with the underlying
        /// <see cref="Mock{IBasketRepository}"/> and <see cref="Mock{IMapper}"/> so each
        /// test can arrange setups and verify interactions.
        /// </returns>
        private static (BasketController controller, Mock<IBasketRepository> repo, Mock<IMapper> mapper) CreateController()
        {
            var repo = new Mock<IBasketRepository>();
            var mapper = new Mock<IMapper>();
            var controller = new BasketController(repo.Object, mapper.Object);
            return (controller, repo, mapper);
        }

        /// <summary>
        /// Happy path: when the repository returns an existing basket for the requested
        /// id, the action returns <c>200 OK</c> carrying that exact basket instance
        /// (the null-coalescing fallback is not taken).
        /// </summary>
        [Fact]
        public async Task GetBasketById_WhenBasketExists_ReturnsOkWithExistingBasket()
        {
            // Arrange
            var (controller, repo, _) = CreateController();
            var existing = new CustomerBasket("basket-1");
            existing.Items.Add(new BasketItem
            {
                Id = 1,
                ProductName = "Angular Speedster Board 2000",
                Price = 200m,
                Quantity = 2
            });
            repo.Setup(r => r.GetBasketAsync("basket-1")).ReturnsAsync(existing);

            // Act
            var result = await controller.GetBasketById("basket-1");

            // Assert
            result.Result.Should().BeOfType<OkObjectResult>();
            var okResult = (OkObjectResult)result.Result;
            okResult.Value.Should().BeSameAs(existing);
            repo.Verify(r => r.GetBasketAsync("basket-1"), Times.Once);
        }

        /// <summary>
        /// Edge case: when the repository has no basket for the requested id (returns
        /// <c>null</c>), the action's <c>?? new CustomerBasket(id)</c> fallback kicks in
        /// and a fresh, empty basket carrying the requested id is returned inside
        /// <c>200 OK</c>.
        /// </summary>
        [Fact]
        public async Task GetBasketById_WhenBasketNotFound_ReturnsOkWithNewEmptyBasketForId()
        {
            // Arrange
            var (controller, repo, _) = CreateController();
            repo.Setup(r => r.GetBasketAsync("missing")).ReturnsAsync((CustomerBasket)null);

            // Act
            var result = await controller.GetBasketById("missing");

            // Assert
            result.Result.Should().BeOfType<OkObjectResult>();
            var okResult = (OkObjectResult)result.Result;
            okResult.Value.Should().BeOfType<CustomerBasket>();
            var basket = (CustomerBasket)okResult.Value;
            basket.Id.Should().Be("missing");
            basket.Items.Should().BeEmpty();
        }

        /// <summary>
        /// Happy path: <c>UpdateBasket</c> maps the incoming DTO to a domain
        /// <see cref="CustomerBasket"/>, forwards the mapped instance to the repository,
        /// and returns the repository's persisted result inside <c>200 OK</c>.
        /// Verifies both the mapping call and that the repository received the mapped
        /// (not the raw DTO) instance.
        /// </summary>
        [Fact]
        public async Task UpdateBasket_WithValidDto_MapsAndReturnsOkWithUpdatedBasket()
        {
            // Arrange
            var (controller, repo, mapper) = CreateController();
            var dto = new CustomerBasketDto { Id = "basket-1" };
            var mapped = new CustomerBasket("basket-1");
            var updated = new CustomerBasket("basket-1");
            mapper.Setup(m => m.Map<CustomerBasketDto, CustomerBasket>(dto)).Returns(mapped);
            repo.Setup(r => r.UpdateBasketAsync(mapped)).ReturnsAsync(updated);

            // Act
            var result = await controller.UpdateBasket(dto);

            // Assert
            result.Result.Should().BeOfType<OkObjectResult>();
            var okResult = (OkObjectResult)result.Result;
            okResult.Value.Should().BeSameAs(updated);
            mapper.Verify(m => m.Map<CustomerBasketDto, CustomerBasket>(dto), Times.Once);
            repo.Verify(r => r.UpdateBasketAsync(mapped), Times.Once);
        }

        /// <summary>
        /// Verification: <c>DeleteBasketAsync</c> returns a plain <see cref="Task"/> (no
        /// action result), so the observable behavior is the single delegated call to
        /// the repository. This asserts the repository's delete is invoked exactly once
        /// with the supplied id.
        /// </summary>
        [Fact]
        public async Task DeleteBasketAsync_WhenCalled_InvokesRepositoryDeleteOnce()
        {
            // Arrange
            var (controller, repo, _) = CreateController();
            repo.Setup(r => r.DeleteBasketAsync("basket-1")).ReturnsAsync(true);

            // Act
            await controller.DeleteBasketAsync("basket-1");

            // Assert
            repo.Verify(r => r.DeleteBasketAsync("basket-1"), Times.Once);
        }
    }
}
