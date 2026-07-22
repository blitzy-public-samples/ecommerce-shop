using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using API.Controllers;
using API.Dtos;
using API.Errors;                         // ApiResponse (400/404/409 outcome-branch assertions)
using API.Helpers;                        // CachedAttribute (reflection assertion)
using AutoMapper;
using Core.Entities;                      // FlashSale
using Core.Interfaces;                    // IFlashSaleService, ActiveFlashSale, FlashSaleScheduleResult/Outcome
using FluentAssertions;
using Microsoft.AspNetCore.Authorization; // AuthorizeAttribute (reflection assertion)
using Microsoft.AspNetCore.Http;          // DefaultHttpContext (so GetActiveSales can write the N1 no-store header)
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace API.Tests.Controllers
{
    /// <summary>
    /// Unit tests for <see cref="FlashSalesController"/> (Real-Time Inventory &amp; Flash Sale feature).
    /// <para>
    /// The controller is exercised in complete isolation: its two collaborators
    /// (<see cref="IFlashSaleService"/> and <see cref="IMapper"/>) are Moq test doubles, so no
    /// ASP.NET Core request pipeline, database, or AutoMapper configuration is involved. Because the
    /// action methods are invoked directly in-process, the action-level <c>[Authorize]</c> filter does
    /// not execute (it is verified structurally via reflection instead), and the deliberate ABSENCE of
    /// <c>[Cached]</c> on the active-sales query is likewise asserted via reflection.
    /// </para>
    /// <para>
    /// Contract note: the authored <see cref="IFlashSaleService.ScheduleAsync"/> returns a
    /// <see cref="FlashSaleScheduleResult"/> (a deterministic <see cref="FlashSaleScheduleOutcome"/>
    /// plus, on success, the persisted <see cref="FlashSale"/> and the authoritative post-commit
    /// <c>QuantityAvailable</c>). <c>CreateFlashSale</c> switches on that outcome and translates it into an
    /// exact HTTP status: <c>Success</c> -&gt; <c>200 OK</c> carrying the mapped <see cref="FlashSaleDto"/>;
    /// <c>ProductNotFound</c> -&gt; <c>404</c>; <c>Overlap</c> -&gt; <c>409</c>; and the four validation
    /// outcomes -&gt; <c>400</c>. Review finding M12: on success the controller wraps the new sale in an
    /// <see cref="ActiveFlashSale"/> whose <c>QuantityAvailable</c> is the service-computed value from the
    /// result (NOT fabricated from <c>StockAllocation</c>) before mapping.
    /// </para>
    /// <para>
    /// Conventions (mirroring <c>BasketControllerTests</c>/<c>ProductsControllerTests</c>/<c>OrdersControllerTests</c>):
    /// naming is <c>MethodName_StateUnderTest_ExpectedBehavior</c>; bodies are Arrange-Act-Assert with
    /// explicit markers; a fresh set of mocks is built per test via <see cref="CreateController"/>;
    /// assertions use FluentAssertions and Moq verification. No production code is modified by these tests.
    /// </para>
    /// <para>
    /// Result-type note: <c>CreateFlashSale</c> and <c>GetActiveSales</c> return their payload via
    /// <c>Ok(...)</c>/<c>NotFound(...)</c>/<c>Conflict(...)</c>/<c>BadRequest(...)</c> (all
    /// <see cref="IActionResult"/>s), so the value is surfaced through
    /// <see cref="ActionResult{TValue}.Result"/> (NOT through <c>.Value</c>).
    /// </para>
    /// </summary>
    public class FlashSalesControllerTests
    {
        /// <summary>
        /// Builds a <see cref="FlashSalesController"/> wired to a brand-new pair of mocks. A new instance
        /// is created on every invocation, guaranteeing tests never share mutable mock state.
        /// </summary>
        private static (FlashSalesController controller, Mock<IFlashSaleService> service, Mock<IMapper> mapper) CreateController()
        {
            var service = new Mock<IFlashSaleService>();
            var mapper = new Mock<IMapper>();
            var controller = new FlashSalesController(service.Object, mapper.Object)
            {
                // GetActiveSales writes a Cache-Control response header (review finding N1), which requires a
                // live HttpContext. A DefaultHttpContext supplies a real, writable Response.Headers collection
                // when the action is invoked directly in-process (no request pipeline runs in a unit test).
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };
            return (controller, service, mapper);
        }

        /// <summary>
        /// Produces a representative, well-formed <see cref="CreateFlashSaleDto"/>. The concrete field
        /// values are immaterial to the outcome-branch tests (the service is mocked to return a fixed
        /// outcome regardless of input), but a realistic payload keeps each Arrange block readable.
        /// </summary>
        private static CreateFlashSaleDto SampleValidDto()
        {
            return new CreateFlashSaleDto
            {
                ProductId = 5,
                StartAt = DateTimeOffset.UtcNow,
                EndAt = DateTimeOffset.UtcNow.AddHours(2),
                SalePrice = 9.99m,
                StockAllocation = 100
            };
        }

        // ------------------------------------------------------------------
        // CreateFlashSale (POST /api/flash-sales) — success path
        // ------------------------------------------------------------------

        /// <summary>
        /// Happy path: when the service reports <see cref="FlashSaleScheduleOutcome.Success"/>, the mapped
        /// <see cref="FlashSaleDto"/> is returned inside <c>200 OK</c> (surfaced via <c>result.Result</c>).
        /// </summary>
        [Fact]
        public async Task CreateFlashSale_WithSuccessOutcome_ReturnsOkWithMappedFlashSaleDto()
        {
            // Arrange
            var (controller, service, mapper) = CreateController();
            var dto = SampleValidDto();
            var sale = new FlashSale
            {
                Id = 1,
                ProductId = dto.ProductId,
                StartAt = dto.StartAt,
                EndAt = dto.EndAt,
                SalePrice = dto.SalePrice,
                StockAllocation = dto.StockAllocation
            };
            var mapped = new FlashSaleDto
            {
                Id = 1,
                ProductId = dto.ProductId,
                SalePrice = dto.SalePrice,
                StartAt = dto.StartAt,
                EndAt = dto.EndAt,
                StockAllocation = dto.StockAllocation,
                QuantityAvailable = dto.StockAllocation
            };

            service
                .Setup(s => s.ScheduleAsync(dto.ProductId, dto.StartAt, dto.EndAt, dto.SalePrice, dto.StockAllocation))
                .ReturnsAsync(new FlashSaleScheduleResult { Outcome = FlashSaleScheduleOutcome.Success, FlashSale = sale });
            mapper
                .Setup(m => m.Map<ActiveFlashSale, FlashSaleDto>(It.IsAny<ActiveFlashSale>()))
                .Returns(mapped);

            // Act
            var result = await controller.CreateFlashSale(dto);

            // Assert
            var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
            okResult.Value.Should().BeSameAs(mapped);
        }

        /// <summary>
        /// The controller must forward the DTO's fields to <c>ScheduleAsync</c> in the exact order
        /// <c>(ProductId, StartAt, EndAt, SalePrice, StockAllocation)</c>.
        /// </summary>
        [Fact]
        public async Task CreateFlashSale_WithValidDto_PassesExactDtoFieldsToScheduleAsync()
        {
            // Arrange
            var (controller, service, mapper) = CreateController();
            var dto = new CreateFlashSaleDto
            {
                ProductId = 42,
                StartAt = DateTimeOffset.UtcNow,
                EndAt = DateTimeOffset.UtcNow.AddHours(1),
                SalePrice = 12.50m,
                StockAllocation = 250
            };
            service
                .Setup(s => s.ScheduleAsync(It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<decimal>(), It.IsAny<int>()))
                .ReturnsAsync(new FlashSaleScheduleResult
                {
                    Outcome = FlashSaleScheduleOutcome.Success,
                    FlashSale = new FlashSale { Id = 1, ProductId = 42, StockAllocation = 250 }
                });
            mapper
                .Setup(m => m.Map<ActiveFlashSale, FlashSaleDto>(It.IsAny<ActiveFlashSale>()))
                .Returns(new FlashSaleDto());

            // Act
            await controller.CreateFlashSale(dto);

            // Assert
            service.Verify(
                s => s.ScheduleAsync(dto.ProductId, dto.StartAt, dto.EndAt, dto.SalePrice, dto.StockAllocation),
                Times.Once);
        }

        /// <summary>
        /// Review finding M12: the controller must wrap the scheduled sale in an <see cref="ActiveFlashSale"/>
        /// whose <c>QuantityAvailable</c> is the AUTHORITATIVE, service-computed value carried on
        /// <see cref="FlashSaleScheduleResult.QuantityAvailable"/> — NOT a figure the controller fabricates from
        /// <c>StockAllocation</c>. Here the service reports 90 (e.g. a concurrently-created hold already consumed
        /// 10 of the 100 units), and the controller must surface exactly that. The instance passed to the mapper
        /// is captured and inspected.
        /// </summary>
        [Fact]
        public async Task CreateFlashSale_WhenScheduled_MapsActiveFlashSaleWithServiceComputedQuantityAvailable()
        {
            // Arrange
            var (controller, service, mapper) = CreateController();
            var dto = SampleValidDto();
            var sale = new FlashSale { Id = 11, ProductId = dto.ProductId, StockAllocation = 100 };
            const int authoritativeAvailable = 90; // deliberately != StockAllocation to prove propagation, not fabrication
            ActiveFlashSale captured = null;

            service
                .Setup(s => s.ScheduleAsync(It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<decimal>(), It.IsAny<int>()))
                .ReturnsAsync(new FlashSaleScheduleResult
                {
                    Outcome = FlashSaleScheduleOutcome.Success,
                    FlashSale = sale,
                    QuantityAvailable = authoritativeAvailable
                });
            mapper
                .Setup(m => m.Map<ActiveFlashSale, FlashSaleDto>(It.IsAny<ActiveFlashSale>()))
                .Callback<ActiveFlashSale>(a => captured = a)
                .Returns(new FlashSaleDto());

            // Act
            await controller.CreateFlashSale(dto);

            // Assert
            captured.Should().NotBeNull();
            captured.Sale.Should().BeSameAs(sale);
            captured.QuantityAvailable.Should().Be(authoritativeAvailable);
        }

        // ------------------------------------------------------------------
        // CreateFlashSale (POST /api/flash-sales) — outcome-mapped failure paths
        // ------------------------------------------------------------------

        /// <summary>
        /// When the service reports <see cref="FlashSaleScheduleOutcome.ProductNotFound"/>, the action
        /// must respond <c>404 Not Found</c> with an <see cref="ApiResponse"/> carrying status code 404
        /// and the "Product not found" message.
        /// </summary>
        [Fact]
        public async Task CreateFlashSale_WhenProductNotFound_ReturnsNotFoundWithApiResponse404()
        {
            // Arrange
            var (controller, service, _) = CreateController();
            var dto = SampleValidDto();
            service
                .Setup(s => s.ScheduleAsync(It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<decimal>(), It.IsAny<int>()))
                .ReturnsAsync(new FlashSaleScheduleResult { Outcome = FlashSaleScheduleOutcome.ProductNotFound });

            // Act
            var result = await controller.CreateFlashSale(dto);

            // Assert
            var notFound = result.Result.Should().BeOfType<NotFoundObjectResult>().Subject;
            var apiResponse = notFound.Value.Should().BeOfType<ApiResponse>().Subject;
            apiResponse.StatusCode.Should().Be(404);
            apiResponse.Message.Should().Be("Product not found");
        }

        /// <summary>
        /// When the service reports <see cref="FlashSaleScheduleOutcome.Overlap"/> (another sale for the
        /// same product already occupies part of the requested window), the action must respond
        /// <c>409 Conflict</c> with an <see cref="ApiResponse"/> carrying status code 409.
        /// </summary>
        [Fact]
        public async Task CreateFlashSale_WhenScheduleOverlaps_ReturnsConflictWithApiResponse409()
        {
            // Arrange
            var (controller, service, _) = CreateController();
            var dto = SampleValidDto();
            service
                .Setup(s => s.ScheduleAsync(It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<decimal>(), It.IsAny<int>()))
                .ReturnsAsync(new FlashSaleScheduleResult { Outcome = FlashSaleScheduleOutcome.Overlap });

            // Act
            var result = await controller.CreateFlashSale(dto);

            // Assert
            var conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
            var apiResponse = conflict.Value.Should().BeOfType<ApiResponse>().Subject;
            apiResponse.StatusCode.Should().Be(409);
            apiResponse.Message.Should().Be("An overlapping flash sale already exists for this product");
        }

        /// <summary>
        /// Each of the four server-side validation outcomes must be translated into <c>400 Bad Request</c>
        /// with an <see cref="ApiResponse"/> carrying status code 400 and the rule-specific message. This
        /// parameterised test exhaustively covers every <see cref="FlashSaleScheduleOutcome"/> value the
        /// controller maps to a 400.
        /// </summary>
        [Theory]
        [InlineData(FlashSaleScheduleOutcome.InvalidWindow, "The flash-sale window is invalid; EndAt must be strictly after StartAt")]
        [InlineData(FlashSaleScheduleOutcome.InvalidAllocation, "StockAllocation must be greater than zero")]
        [InlineData(FlashSaleScheduleOutcome.InvalidSalePrice, "SalePrice must be greater than zero")]
        [InlineData(FlashSaleScheduleOutcome.SalePriceNotBelowBasePrice, "SalePrice must be below the product's base price")]
        public async Task CreateFlashSale_WhenValidationOutcome_ReturnsBadRequestWithApiResponse400(
            FlashSaleScheduleOutcome outcome, string expectedMessage)
        {
            // Arrange
            var (controller, service, _) = CreateController();
            var dto = SampleValidDto();
            service
                .Setup(s => s.ScheduleAsync(It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<decimal>(), It.IsAny<int>()))
                .ReturnsAsync(new FlashSaleScheduleResult { Outcome = outcome });

            // Act
            var result = await controller.CreateFlashSale(dto);

            // Assert
            var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
            var apiResponse = badRequest.Value.Should().BeOfType<ApiResponse>().Subject;
            apiResponse.StatusCode.Should().Be(400);
            apiResponse.Message.Should().Be(expectedMessage);
        }

        /// <summary>
        /// On any non-success outcome the controller short-circuits before mapping: the mapper must never
        /// be invoked. This isolates the failure branches from the success branch's mapping step.
        /// </summary>
        [Fact]
        public async Task CreateFlashSale_WhenOutcomeNotSuccess_DoesNotInvokeMapper()
        {
            // Arrange
            var (controller, service, mapper) = CreateController();
            var dto = SampleValidDto();
            service
                .Setup(s => s.ScheduleAsync(It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<decimal>(), It.IsAny<int>()))
                .ReturnsAsync(new FlashSaleScheduleResult { Outcome = FlashSaleScheduleOutcome.ProductNotFound });

            // Act
            await controller.CreateFlashSale(dto);

            // Assert
            mapper.Verify(
                m => m.Map<ActiveFlashSale, FlashSaleDto>(It.IsAny<ActiveFlashSale>()),
                Times.Never);
        }

        // ------------------------------------------------------------------
        // GetActiveSales (GET /api/flash-sales/active)
        // ------------------------------------------------------------------

        /// <summary>
        /// When active sales exist, the mapped list is returned inside <c>200 OK</c> and the service is
        /// queried exactly once.
        /// </summary>
        [Fact]
        public async Task GetActiveSales_WhenSalesActive_ReturnsOkWithMappedList()
        {
            // Arrange
            var (controller, service, mapper) = CreateController();
            IReadOnlyList<ActiveFlashSale> sales = new List<ActiveFlashSale>
            {
                new ActiveFlashSale { Sale = new FlashSale { Id = 1, ProductId = 5, StockAllocation = 100 }, QuantityAvailable = 80 },
                new ActiveFlashSale { Sale = new FlashSale { Id = 2, ProductId = 6, StockAllocation = 50 }, QuantityAvailable = 50 }
            };
            IReadOnlyList<FlashSaleDto> mapped = new List<FlashSaleDto>
            {
                new FlashSaleDto { Id = 1, ProductId = 5, StockAllocation = 100, QuantityAvailable = 80 },
                new FlashSaleDto { Id = 2, ProductId = 6, StockAllocation = 50, QuantityAvailable = 50 }
            };

            // N1: GetActiveSalesAsync now takes an optional productId; an explicit matcher is required because a
            // Moq expression tree cannot omit optional arguments (CS0854).
            service.Setup(s => s.GetActiveSalesAsync(It.IsAny<int?>())).ReturnsAsync(sales);
            mapper
                .Setup(m => m.Map<IReadOnlyList<ActiveFlashSale>, IReadOnlyList<FlashSaleDto>>(sales))
                .Returns(mapped);

            // Act
            var result = await controller.GetActiveSales();

            // Assert
            var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
            okResult.Value.Should().BeSameAs(mapped);
            var returned = okResult.Value.Should().BeAssignableTo<IReadOnlyList<FlashSaleDto>>().Subject;
            returned.Should().HaveCount(2);
            returned[0].QuantityAvailable.Should().Be(80);
            // N1: the action was called without a productId, so it must forward null (the full-list contract).
            service.Verify(s => s.GetActiveSalesAsync((int?)null), Times.Once);
        }

        /// <summary>
        /// When there are no active sales, the action still returns <c>200 OK</c> carrying a non-null,
        /// empty collection.
        /// </summary>
        [Fact]
        public async Task GetActiveSales_WhenNoActiveSales_ReturnsOkWithEmptyList()
        {
            // Arrange
            var (controller, service, mapper) = CreateController();
            IReadOnlyList<ActiveFlashSale> sales = new List<ActiveFlashSale>();
            IReadOnlyList<FlashSaleDto> mapped = new List<FlashSaleDto>();

            // N1: explicit matcher required (Moq expression tree cannot omit the new optional productId; CS0854).
            service.Setup(s => s.GetActiveSalesAsync(It.IsAny<int?>())).ReturnsAsync(sales);
            mapper
                .Setup(m => m.Map<IReadOnlyList<ActiveFlashSale>, IReadOnlyList<FlashSaleDto>>(sales))
                .Returns(mapped);

            // Act
            var result = await controller.GetActiveSales();

            // Assert
            var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
            var returned = okResult.Value.Should().BeAssignableTo<IReadOnlyList<FlashSaleDto>>().Subject;
            returned.Should().NotBeNull();
            returned.Should().BeEmpty();
        }

        /// <summary>
        /// N1: when a <c>?productId</c> query value is supplied, the controller must forward that EXACT value
        /// to <see cref="IFlashSaleService.GetActiveSalesAsync"/> so the result is narrowed to the single
        /// product (the product-details page subscribes per product). Proven by invoking the action with an
        /// explicit id and verifying the service received the same id — never the full-list <c>null</c>.
        /// </summary>
        [Fact]
        public async Task GetActiveSales_WhenProductIdProvided_ForwardsProductIdToService()
        {
            // Arrange
            var (controller, service, mapper) = CreateController();
            const int productId = 7;
            IReadOnlyList<ActiveFlashSale> sales = new List<ActiveFlashSale>
            {
                new ActiveFlashSale { Sale = new FlashSale { Id = 3, ProductId = productId, StockAllocation = 40 }, QuantityAvailable = 40 }
            };
            IReadOnlyList<FlashSaleDto> mapped = new List<FlashSaleDto>
            {
                new FlashSaleDto { Id = 3, ProductId = productId, StockAllocation = 40, QuantityAvailable = 40 }
            };
            // N1: explicit matcher required (Moq expression tree cannot omit the optional productId; CS0854).
            service.Setup(s => s.GetActiveSalesAsync(It.IsAny<int?>())).ReturnsAsync(sales);
            mapper
                .Setup(m => m.Map<IReadOnlyList<ActiveFlashSale>, IReadOnlyList<FlashSaleDto>>(sales))
                .Returns(mapped);

            // Act
            var result = await controller.GetActiveSales(productId);

            // Assert
            var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
            okResult.Value.Should().BeSameAs(mapped);
            // The exact supplied id must be forwarded (the single-product filter contract), never null.
            service.Verify(s => s.GetActiveSalesAsync(productId), Times.Once);
            service.Verify(s => s.GetActiveSalesAsync((int?)null), Times.Never);
        }

        /// <summary>
        /// N1: the active-sales response must carry <c>Cache-Control: no-store</c> so no shared proxy or
        /// browser cache retains the real-time price/stock payload (the endpoint is also deliberately NOT
        /// decorated with <c>[Cached]</c>). Proven by reading the header the action wrote onto the live
        /// response supplied by the test's <see cref="Microsoft.AspNetCore.Http.DefaultHttpContext"/>.
        /// </summary>
        [Fact]
        public async Task GetActiveSales_WritesNoStoreCacheControlHeader()
        {
            // Arrange
            var (controller, service, mapper) = CreateController();
            IReadOnlyList<ActiveFlashSale> sales = new List<ActiveFlashSale>();
            service.Setup(s => s.GetActiveSalesAsync(It.IsAny<int?>())).ReturnsAsync(sales);
            mapper
                .Setup(m => m.Map<IReadOnlyList<ActiveFlashSale>, IReadOnlyList<FlashSaleDto>>(sales))
                .Returns(new List<FlashSaleDto>());

            // Act
            await controller.GetActiveSales();

            // Assert — the action explicitly forbids any caching layer from retaining this real-time response.
            controller.Response.Headers.Should().ContainKey("Cache-Control");
            controller.Response.Headers["Cache-Control"].ToString().Should().Be("no-store");
        }

        // ------------------------------------------------------------------
        // Contract / reflection assertions (filters do not run in direct calls)
        // ------------------------------------------------------------------

        /// <summary>
        /// The active-sales query must NOT be response-cached: real-time price/stock accuracy requires it
        /// to always hit the service. Verified structurally because the <c>[Cached]</c> filter would not
        /// execute during a direct in-process call anyway.
        /// </summary>
        [Fact]
        public void GetActiveSales_IsNotDecoratedWithCachedAttribute()
        {
            // Arrange
            var method = typeof(FlashSalesController).GetMethod(nameof(FlashSalesController.GetActiveSales));
            method.Should().NotBeNull();

            // Act
            var cachedAttributes = method.GetCustomAttributes(typeof(CachedAttribute), inherit: true);

            // Assert
            cachedAttributes.Should().BeEmpty();
        }

        /// <summary>
        /// Scheduling a flash sale requires authentication (reuse of the existing JWT scheme), documented
        /// by the action-level <c>[Authorize]</c> attribute.
        /// </summary>
        [Fact]
        public void CreateFlashSale_IsDecoratedWithAuthorize()
        {
            // Arrange
            var method = typeof(FlashSalesController).GetMethod(nameof(FlashSalesController.CreateFlashSale));
            method.Should().NotBeNull();

            // Act
            var authorizeAttributes = method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true);

            // Assert
            authorizeAttributes.Should().NotBeEmpty();
        }
    }
}
