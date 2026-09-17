using System.Threading.Tasks;
using API.Controllers;
using API.Dtos;
using API.Errors;
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
    /// reserves stock <b>before</b> persisting. It normalizes duplicate product lines (summing
    /// their quantities), then creates/extends a reservation for each line of the <b>incoming</b>
    /// basket via <see cref="IInventoryService.ExtendReservationAsync(string,int,int)"/>. If every
    /// line is granted, the normalized basket is persisted and holds for any removed lines are
    /// released; the response is <c>200 OK</c> carrying the persisted basket. If any line cannot be
    /// reserved, the basket is <b>not</b> persisted and the action returns <c>409 Conflict</c> so the
    /// persisted basket can never claim stock that was not safely held.
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

            // Sensible default reservation behavior so happy-path tests read cleanly:
            //  - ExtendReservationAsync grants by default (returns true). Moq otherwise returns
            //    Task.FromResult(false) for a Task<bool>, which the controller would treat as
            //    "insufficient stock" and reject with 409 — so this default must be explicit.
            //    Tests exercising the reject path override this for a specific product to return false.
            //  - Release / ReleaseAll return a completed Task so the awaited side effects are no-ops.
            inventory
                .Setup(i => i.ExtendReservationAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
                .ReturnsAsync(true);
            inventory
                .Setup(i => i.ReleaseReservationAsync(It.IsAny<string>(), It.IsAny<int>()))
                .Returns(Task.CompletedTask);
            inventory
                .Setup(i => i.ReleaseAllReservationsForBasketAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask);

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
        /// F1 (delete releases holds): <c>DeleteBasketAsync</c> releases every Active hold for the
        /// basket (restoring available stock) BEFORE deleting the basket, then deletes it. Asserts
        /// both collaborators are invoked exactly once with the supplied id.
        /// </summary>
        [Fact]
        public async Task DeleteBasketAsync_WhenCalled_ReleasesAllHoldsThenDeletesBasket()
        {
            // Arrange
            var (controller, repo, _, inventory) = CreateController();
            repo.Setup(r => r.DeleteBasketAsync("basket-1")).ReturnsAsync(true);

            // Act
            await controller.DeleteBasketAsync("basket-1");

            // Assert — holds released for the basket, and the basket deleted, each exactly once.
            inventory.Verify(i => i.ReleaseAllReservationsForBasketAsync("basket-1"), Times.Once);
            repo.Verify(r => r.DeleteBasketAsync("basket-1"), Times.Once);
        }

        // ---------------------------------------------------------------------------------
        // Reservation call site (Real-Time Inventory & Flash-Sale System feature).
        // UpdateBasket reserves BEFORE persisting: it normalizes duplicate lines, then
        // creates/extends a reservation for EACH line of the INCOMING (mapped) basket via
        // IInventoryService.ExtendReservationAsync(basketId, productId, quantity). Only when
        // every line is granted is the basket persisted (and removed-line holds released);
        // if any line is denied the basket is NOT persisted and the action returns 409.
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// When the incoming basket has lines and all are reservable, <c>UpdateBasket</c>
        /// creates/extends a reservation for each line — passing the basket id, the line's
        /// product id (<see cref="BasketItem.Id"/>), and its quantity — persists the basket, and
        /// returns <c>200 OK</c> carrying the persisted basket.
        /// </summary>
        [Fact]
        public async Task UpdateBasket_WithReservableItems_ExtendsReservationForEachItemAndReturnsOk()
        {
            // Arrange — the INCOMING (mapped) basket carries the lines, because reservation happens
            // BEFORE persistence. The persisted echo returned by the repo is what the client receives.
            var (controller, repo, mapper, inventory) = CreateController();
            var dto = new CustomerBasketDto { Id = "basket-1" };
            var mapped = new CustomerBasket("basket-1");
            mapped.Items.Add(new BasketItem { Id = 1, ProductName = "Angular Speedster Board 2000", Price = 200m, Quantity = 2 });
            mapped.Items.Add(new BasketItem { Id = 5, ProductName = "Green Angular Boots", Price = 150m, Quantity = 3 });
            var updated = new CustomerBasket("basket-1");
            updated.Items.Add(new BasketItem { Id = 1, ProductName = "Angular Speedster Board 2000", Price = 200m, Quantity = 2 });
            updated.Items.Add(new BasketItem { Id = 5, ProductName = "Green Angular Boots", Price = 150m, Quantity = 3 });
            mapper.Setup(m => m.Map<CustomerBasketDto, CustomerBasket>(dto)).Returns(mapped);
            repo.Setup(r => r.UpdateBasketAsync(mapped)).ReturnsAsync(updated);

            // Act
            var result = await controller.UpdateBasket(dto);

            // Assert — a reservation is created/extended per incoming line using the basket id
            // (CustomerBasket.Id), the line's product id (BasketItem.Id), and its quantity.
            inventory.Verify(i => i.ExtendReservationAsync("basket-1", 1, 2), Times.Once);
            inventory.Verify(i => i.ExtendReservationAsync("basket-1", 5, 3), Times.Once);
            inventory.Verify(
                i => i.ExtendReservationAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()),
                Times.Exactly(2));

            // Reserve happened BEFORE persist and the basket was persisted; response is 200 OK.
            repo.Verify(r => r.UpdateBasketAsync(mapped), Times.Once);
            result.Result.Should().BeOfType<OkObjectResult>();
            ((OkObjectResult)result.Result).Value.Should().BeSameAs(updated);
        }

        /// <summary>
        /// When the incoming basket has no lines, <c>UpdateBasket</c> makes no reservation call
        /// and still returns <c>200 OK</c> carrying the persisted basket.
        /// </summary>
        [Fact]
        public async Task UpdateBasket_WithNoItems_MakesNoReservationAndReturnsOk()
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

            // Assert — no basket lines => no reservation call; response is 200 OK carrying the basket.
            inventory.Verify(
                i => i.ExtendReservationAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()),
                Times.Never);
            result.Result.Should().BeOfType<OkObjectResult>();
            ((OkObjectResult)result.Result).Value.Should().BeSameAs(updated);
        }

        /// <summary>
        /// F2 (reserve-before-persist): when at least one line cannot be fully reserved,
        /// <c>UpdateBasket</c> returns <c>409 Conflict</c> and does <b>not</b> persist the basket,
        /// so the persisted basket can never claim stock that was not safely held.
        /// </summary>
        [Fact]
        public async Task UpdateBasket_WhenALineCannotBeReserved_Returns409AndDoesNotPersist()
        {
            // Arrange — product 1 is over-requested (stock short), so its reservation is denied.
            var (controller, repo, mapper, inventory) = CreateController();
            var dto = new CustomerBasketDto { Id = "basket-1" };
            var mapped = new CustomerBasket("basket-1");
            mapped.Items.Add(new BasketItem { Id = 1, ProductName = "Angular Speedster Board 2000", Price = 200m, Quantity = 15 });
            mapper.Setup(m => m.Map<CustomerBasketDto, CustomerBasket>(dto)).Returns(mapped);
            inventory
                .Setup(i => i.ExtendReservationAsync("basket-1", 1, 15))
                .ReturnsAsync(false); // insufficient stock

            // Act
            var result = await controller.UpdateBasket(dto);

            // Assert — 409 Conflict carrying an ApiResponse, and the basket was NOT persisted.
            result.Result.Should().BeOfType<ConflictObjectResult>();
            var conflict = (ConflictObjectResult)result.Result;
            conflict.StatusCode.Should().Be(409);
            conflict.Value.Should().BeOfType<ApiResponse>();
            ((ApiResponse)conflict.Value).StatusCode.Should().Be(409);
            repo.Verify(r => r.UpdateBasketAsync(It.IsAny<CustomerBasket>()), Times.Never);
        }

        /// <summary>
        /// F6-D1 (compensating release on a FRESH basket): on a multi-line partial failure, a hold that
        /// WAS granted earlier in the same call must not be left orphaned when the update is rejected.
        /// With no prior persisted basket, the granted line's freshly-created hold is released before the
        /// action returns <c>409</c>, so no reservation survives a basket that was never persisted.
        /// </summary>
        [Fact]
        public async Task UpdateBasket_WhenPartialFailureOnFreshBasket_ReleasesGrantedLineBeforeReturning409()
        {
            // Arrange — fresh basket (no prior). Line 1 (qty 3) reserves OK; line 2 (qty 1) is short, so the
            // whole update is rejected. This is the deterministic F6-D1 repro (product granted, then a later
            // short line) that previously left product 1 as an orphaned Active hold.
            var (controller, repo, mapper, inventory) = CreateController();
            var dto = new CustomerBasketDto { Id = "basket-1" };
            var mapped = new CustomerBasket("basket-1");
            mapped.Items.Add(new BasketItem { Id = 1, ProductName = "Angular Speedster Board 2000", Price = 200m, Quantity = 3 });
            mapped.Items.Add(new BasketItem { Id = 2, ProductName = "Typescript Entry Board", Price = 120m, Quantity = 1 });
            mapper.Setup(m => m.Map<CustomerBasketDto, CustomerBasket>(dto)).Returns(mapped);
            // No prior basket (GetBasketAsync returns null by default) => nothing to restore to.
            inventory.Setup(i => i.ExtendReservationAsync("basket-1", 2, 1)).ReturnsAsync(false);

            // Act
            var result = await controller.UpdateBasket(dto);

            // Assert — 409, NOT persisted, and the granted line (product 1) hold is released (no orphan),
            // while the denied line (product 2) — never granted — is not released.
            result.Result.Should().BeOfType<ConflictObjectResult>();
            ((ConflictObjectResult)result.Result).StatusCode.Should().Be(409);
            repo.Verify(r => r.UpdateBasketAsync(It.IsAny<CustomerBasket>()), Times.Never);
            inventory.Verify(i => i.ReleaseReservationAsync("basket-1", 1), Times.Once);
            inventory.Verify(i => i.ReleaseReservationAsync("basket-1", 2), Times.Never);
        }

        /// <summary>
        /// F6-D1 (compensating restore with a PRIOR hold): on a multi-line partial failure where a granted
        /// product already held stock from the previously-persisted basket, the hold is restored to that
        /// prior quantity (not released) before returning <c>409</c>, so the reservation state keeps
        /// mirroring the still-persisted prior basket rather than the rejected larger request.
        /// </summary>
        [Fact]
        public async Task UpdateBasket_WhenPartialFailureWithPriorHold_RestoresGrantedLineToPriorQuantity()
        {
            // Arrange — prior basket held product 1 at qty 2. Incoming grows product 1 to qty 5 (granted)
            // and adds product 2 qty 1 (short), so the update is rejected.
            var (controller, repo, mapper, inventory) = CreateController();
            var dto = new CustomerBasketDto { Id = "basket-1" };
            var mapped = new CustomerBasket("basket-1");
            mapped.Items.Add(new BasketItem { Id = 1, ProductName = "Angular Speedster Board 2000", Price = 200m, Quantity = 5 });
            mapped.Items.Add(new BasketItem { Id = 2, ProductName = "Typescript Entry Board", Price = 120m, Quantity = 1 });
            var prior = new CustomerBasket("basket-1");
            prior.Items.Add(new BasketItem { Id = 1, ProductName = "Angular Speedster Board 2000", Price = 200m, Quantity = 2 });
            mapper.Setup(m => m.Map<CustomerBasketDto, CustomerBasket>(dto)).Returns(mapped);
            repo.Setup(r => r.GetBasketAsync("basket-1")).ReturnsAsync(prior);
            inventory.Setup(i => i.ExtendReservationAsync("basket-1", 2, 1)).ReturnsAsync(false);

            // Act
            var result = await controller.UpdateBasket(dto);

            // Assert — 409, NOT persisted, product 1's hold restored to the prior total (2) rather than
            // released, and the kept product is not released.
            result.Result.Should().BeOfType<ConflictObjectResult>();
            ((ConflictObjectResult)result.Result).StatusCode.Should().Be(409);
            repo.Verify(r => r.UpdateBasketAsync(It.IsAny<CustomerBasket>()), Times.Never);
            inventory.Verify(i => i.ExtendReservationAsync("basket-1", 1, 2), Times.Once);
            inventory.Verify(i => i.ReleaseReservationAsync("basket-1", 1), Times.Never);
        }

        /// <summary>
        /// F2 (duplicate-line normalization): repeated lines for the same product are collapsed
        /// into a single line whose quantity is the SUM, and the reservation is made for that
        /// combined total (not the last duplicate's quantity).
        /// </summary>
        [Fact]
        public async Task UpdateBasket_WithDuplicateLines_ReservesTheSummedQuantityOnce()
        {
            // Arrange — two lines for product 1 (qty 2 and qty 3) should reserve a total of 5.
            var (controller, repo, mapper, inventory) = CreateController();
            var dto = new CustomerBasketDto { Id = "basket-1" };
            var mapped = new CustomerBasket("basket-1");
            mapped.Items.Add(new BasketItem { Id = 1, ProductName = "Angular Speedster Board 2000", Price = 200m, Quantity = 2 });
            mapped.Items.Add(new BasketItem { Id = 1, ProductName = "Angular Speedster Board 2000", Price = 200m, Quantity = 3 });
            var updated = new CustomerBasket("basket-1");
            updated.Items.Add(new BasketItem { Id = 1, ProductName = "Angular Speedster Board 2000", Price = 200m, Quantity = 5 });
            mapper.Setup(m => m.Map<CustomerBasketDto, CustomerBasket>(dto)).Returns(mapped);
            repo.Setup(r => r.UpdateBasketAsync(It.IsAny<CustomerBasket>())).ReturnsAsync(updated);

            // Act
            var result = await controller.UpdateBasket(dto);

            // Assert — exactly one reservation for the summed quantity (5), never the raw duplicates.
            inventory.Verify(i => i.ExtendReservationAsync("basket-1", 1, 5), Times.Once);
            inventory.Verify(
                i => i.ExtendReservationAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()),
                Times.Once);
            result.Result.Should().BeOfType<OkObjectResult>();
        }

        /// <summary>
        /// F1 (removed-line release): when a product present in the previously-persisted basket is
        /// absent from the incoming basket, <c>UpdateBasket</c> releases that product's hold (and
        /// only that product's) after persisting the new basket.
        /// </summary>
        [Fact]
        public async Task UpdateBasket_WhenLineRemoved_ReleasesRemovedProductHoldOnly()
        {
            // Arrange — prior basket held products 1 and 5; the incoming basket keeps only product 1,
            // so product 5's hold must be released.
            var (controller, repo, mapper, inventory) = CreateController();
            var dto = new CustomerBasketDto { Id = "basket-1" };
            var mapped = new CustomerBasket("basket-1");
            mapped.Items.Add(new BasketItem { Id = 1, ProductName = "Angular Speedster Board 2000", Price = 200m, Quantity = 2 });
            var prior = new CustomerBasket("basket-1");
            prior.Items.Add(new BasketItem { Id = 1, ProductName = "Angular Speedster Board 2000", Price = 200m, Quantity = 2 });
            prior.Items.Add(new BasketItem { Id = 5, ProductName = "Green Angular Boots", Price = 150m, Quantity = 3 });
            var updated = new CustomerBasket("basket-1");
            updated.Items.Add(new BasketItem { Id = 1, ProductName = "Angular Speedster Board 2000", Price = 200m, Quantity = 2 });
            mapper.Setup(m => m.Map<CustomerBasketDto, CustomerBasket>(dto)).Returns(mapped);
            repo.Setup(r => r.GetBasketAsync("basket-1")).ReturnsAsync(prior);
            repo.Setup(r => r.UpdateBasketAsync(mapped)).ReturnsAsync(updated);

            // Act
            var result = await controller.UpdateBasket(dto);

            // Assert — product 5 (removed) is released exactly once; product 1 (kept) is not released.
            inventory.Verify(i => i.ReleaseReservationAsync("basket-1", 5), Times.Once);
            inventory.Verify(i => i.ReleaseReservationAsync("basket-1", 1), Times.Never);
            result.Result.Should().BeOfType<OkObjectResult>();
        }
    }
}
