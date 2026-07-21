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
    /// The controller is exercised in complete isolation: its collaborators
    /// (<see cref="IBasketRepository"/>, <see cref="IMapper"/> and
    /// <see cref="IInventoryService"/>) are replaced with Moq test doubles, so no Redis,
    /// HTTP pipeline, PostgreSQL, or AutoMapper configuration is touched. This keeps the
    /// tests fast, deterministic, and focused purely on the controller's own
    /// branching/return logic and its collaborator forwarding.
    /// </para>
    /// <para>
    /// Conventions:
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
    /// <para>
    /// Reservation note (Real-Time Inventory &amp; Flash-Sale System): <c>UpdateBasket</c>
    /// now creates/extends a stock reservation for each line of the <b>persisted</b> basket
    /// via <see cref="IInventoryService.ExtendReservationAsync(string,int,int)"/>. This is a
    /// side effect layered onto the action; it must not change the HTTP response contract,
    /// which remains <c>200 OK</c> carrying the persisted basket.
    /// </para>
    /// </summary>
    public class BasketControllerTests
    {
        /// <summary>
        /// Builds a <see cref="BasketController"/> wired to a brand-new set of mocks
        /// (<see cref="IBasketRepository"/>, <see cref="IMapper"/>, <see cref="IInventoryService"/>).
        /// A new instance is created on every invocation, guaranteeing that individual
        /// tests never share mutable mock state.
        /// </summary>
        private static (BasketController controller, Mock<IBasketRepository> repo, Mock<IMapper> mapper, Mock<IInventoryService> inventory) CreateController()
        {
            var repo = new Mock<IBasketRepository>();
            var mapper = new Mock<IMapper>();
            var inventory = new Mock<IInventoryService>();
            var controller = new BasketController(repo.Object, mapper.Object, inventory.Object);
            return (controller, repo, mapper, inventory);
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
            var (controller, repo, _, _) = CreateController();
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
            var (controller, repo, _, _) = CreateController();
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
        /// (not the raw DTO) instance. With no persisted lines, no reservation is created.
        /// </summary>
        [Fact]
        public async Task UpdateBasket_WithValidDto_MapsAndReturnsOkWithUpdatedBasket()
        {
            // Arrange
            var (controller, repo, mapper, _) = CreateController();
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
            var (controller, repo, _, _) = CreateController();
            repo.Setup(r => r.DeleteBasketAsync("basket-1")).ReturnsAsync(true);

            // Act
            await controller.DeleteBasketAsync("basket-1");

            // Assert
            repo.Verify(r => r.DeleteBasketAsync("basket-1"), Times.Once);
        }

        // ---------------------------------------------------------------------------------
        // Reservation call site (Real-Time Inventory & Flash-Sale System feature).
        // UpdateBasket creates/extends a reservation for EACH line of the PERSISTED basket
        // (the value returned by IBasketRepository.UpdateBasketAsync) via
        // IInventoryService.ExtendReservationAsync(basketId, productId, quantity), as a pure
        // side effect that must not alter the HTTP response contract.
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// When the persisted basket has lines, <c>UpdateBasket</c> creates/extends a
        /// reservation for each line — passing the basket id, the line's product id
        /// (<see cref="BasketItem.Id"/>), and its quantity — and still returns <c>200 OK</c>
        /// carrying the persisted basket (response contract unchanged).
        /// </summary>
        [Fact]
        public async Task UpdateBasket_WithItemsInPersistedBasket_ExtendsReservationForEachItem()
        {
            // Arrange
            var (controller, repo, mapper, inventory) = CreateController();
            var dto = new CustomerBasketDto { Id = "basket-1" };
            var mapped = new CustomerBasket("basket-1");
            var updated = new CustomerBasket("basket-1");
            updated.Items.Add(new BasketItem { Id = 1, ProductName = "Angular Speedster Board 2000", Price = 200m, Quantity = 2 });
            updated.Items.Add(new BasketItem { Id = 5, ProductName = "Green Angular Boots", Price = 150m, Quantity = 3 });
            mapper.Setup(m => m.Map<CustomerBasketDto, CustomerBasket>(dto)).Returns(mapped);
            repo.Setup(r => r.UpdateBasketAsync(mapped)).ReturnsAsync(updated);

            // Act
            var result = await controller.UpdateBasket(dto);

            // Assert — a reservation is created/extended per basket line using the basket id
            // (CustomerBasket.Id), the line's product id (BasketItem.Id), and its quantity.
            inventory.Verify(i => i.ExtendReservationAsync("basket-1", 1, 2), Times.Once);
            inventory.Verify(i => i.ExtendReservationAsync("basket-1", 5, 3), Times.Once);
            inventory.Verify(
                i => i.ExtendReservationAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()),
                Times.Exactly(2));

            // Response contract unchanged: still 200 OK carrying the persisted basket instance.
            result.Result.Should().BeOfType<OkObjectResult>();
            ((OkObjectResult)result.Result).Value.Should().BeSameAs(updated);
        }

        /// <summary>
        /// When the persisted basket has no lines, <c>UpdateBasket</c> makes no reservation
        /// call and still returns <c>200 OK</c> carrying the persisted basket. Guards the
        /// invariant that the reservation logic is a per-line side effect only.
        /// </summary>
        [Fact]
        public async Task UpdateBasket_WithNoItemsInPersistedBasket_MakesNoReservationAndReturnsOk()
        {
            // Arrange
            var (controller, repo, mapper, inventory) = CreateController();
            var dto = new CustomerBasketDto { Id = "basket-1" };
            var mapped = new CustomerBasket("basket-1");
            var updated = new CustomerBasket("basket-1"); // no items
            mapper.Setup(m => m.Map<CustomerBasketDto, CustomerBasket>(dto)).Returns(mapped);
            repo.Setup(r => r.UpdateBasketAsync(mapped)).ReturnsAsync(updated);

            // Act
            var result = await controller.UpdateBasket(dto);

            // Assert — no basket lines => no reservation side effect; response unchanged.
            inventory.Verify(
                i => i.ExtendReservationAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()),
                Times.Never);
            result.Result.Should().BeOfType<OkObjectResult>();
            ((OkObjectResult)result.Result).Value.Should().BeSameAs(updated);
        }
    }
}
