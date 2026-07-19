using System.Collections.Generic;
using System.Threading.Tasks;
using API.Controllers;
using API.Dtos;
using API.Errors;
using API.Tests.Helpers;              // ControllerTestHelpers (GetClaimsPrincipal / WithUser)
using AutoMapper;
using Core.Entities.OrderAggregate;   // Order, DeliveryMethod, Address (NOT Core.Entities.Identity.Address)
using Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace API.Tests.Controllers
{
    /// <summary>
    /// Unit tests for <see cref="OrdersController"/>.
    /// <para>
    /// The controller is instantiated directly with Moq-mocked <see cref="IOrderService"/> and
    /// <see cref="IMapper"/> collaborators (no ASP.NET Core request pipeline). Every action reads the
    /// caller's email from <c>HttpContext.User</c> via the <c>RetrieveEmailFromPrincipal()</c>
    /// extension, so each test first attaches an authenticated <see cref="System.Security.Claims.ClaimsPrincipal"/>
    /// (carrying a <c>ClaimTypes.Email</c> claim) using
    /// <see cref="ControllerTestHelpers.WithUser{TController}"/>. Without an attached
    /// <c>HttpContext</c>, accessing <c>HttpContext.User</c> throws <see cref="System.NullReferenceException"/>.
    /// </para>
    /// <para>
    /// Tests follow the repository convention <c>MethodName_StateUnderTest_ExpectedBehavior</c>, use a
    /// fresh set of mocks per test (guaranteeing isolation), and assert with FluentAssertions.
    /// No production code is modified by these tests.
    /// </para>
    /// </summary>
    public class OrdersControllerTests
    {
        // Canonical authenticated caller. Mirrors the seeded identity user (bob@test.com) so the
        // email value flowing through HttpContext.User -> RetrieveEmailFromPrincipal() is explicit.
        private const string Email = "bob@test.com";

        // ------------------------------------------------------------------
        // CreateOrder
        // ------------------------------------------------------------------

        [Fact]
        public async Task CreateOrder_WithValidOrderAndAuthenticatedUser_ReturnsOkWithCreatedOrder()
        {
            // Arrange
            var orderService = new Mock<IOrderService>();
            var mapper = new Mock<IMapper>();
            var controller = new OrdersController(orderService.Object, mapper.Object)
                .WithUser(ControllerTestHelpers.GetClaimsPrincipal(Email));

            var dto = new OrderDto { BasketId = "b1", DeliveryMethodId = 1, ShipToAddress = new AddressDto() };
            var mappedAddress = new Address();
            var order = new Order();

            mapper.Setup(m => m.Map<AddressDto, Address>(dto.ShipToAddress)).Returns(mappedAddress);
            orderService.Setup(s => s.CreateOrderAsync(Email, 1, "b1", mappedAddress)).ReturnsAsync(order);

            // Act
            var result = await controller.CreateOrder(dto);

            // Assert
            var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
            okResult.Value.Should().BeSameAs(order);
        }

        [Fact]
        public async Task CreateOrder_WithAuthenticatedUser_PassesPrincipalEmailAndDtoValuesToOrderService()
        {
            // Arrange
            var orderService = new Mock<IOrderService>();
            var mapper = new Mock<IMapper>();
            var controller = new OrdersController(orderService.Object, mapper.Object)
                .WithUser(ControllerTestHelpers.GetClaimsPrincipal(Email));

            var dto = new OrderDto { BasketId = "basket-42", DeliveryMethodId = 3, ShipToAddress = new AddressDto() };
            var mappedAddress = new Address();

            mapper.Setup(m => m.Map<AddressDto, Address>(dto.ShipToAddress)).Returns(mappedAddress);
            orderService
                .Setup(s => s.CreateOrderAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<Address>()))
                .ReturnsAsync(new Order());

            // Act
            await controller.CreateOrder(dto);

            // Assert
            // The controller must forward the email resolved from the authenticated principal together
            // with the DTO's delivery-method id, basket id, and the mapped shipping address.
            orderService.Verify(
                s => s.CreateOrderAsync(Email, 3, "basket-42", mappedAddress),
                Times.Once);
        }

        [Fact]
        public async Task CreateOrder_WhenOrderServiceReturnsNull_ReturnsBadRequestWithApiResponse()
        {
            // Arrange
            var orderService = new Mock<IOrderService>();
            var mapper = new Mock<IMapper>();
            var controller = new OrdersController(orderService.Object, mapper.Object)
                .WithUser(ControllerTestHelpers.GetClaimsPrincipal(Email));

            var dto = new OrderDto { BasketId = "b1", DeliveryMethodId = 1, ShipToAddress = new AddressDto() };

            mapper.Setup(m => m.Map<AddressDto, Address>(It.IsAny<AddressDto>())).Returns(new Address());
            orderService
                .Setup(s => s.CreateOrderAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<Address>()))
                .ReturnsAsync((Order)null);

            // Act
            var result = await controller.CreateOrder(dto);

            // Assert
            var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
            var apiResponse = badRequest.Value.Should().BeOfType<ApiResponse>().Subject;
            apiResponse.StatusCode.Should().Be(400);
            apiResponse.Message.Should().Be("Problem creating order");
        }

        // ------------------------------------------------------------------
        // GetOrderForUser
        // ------------------------------------------------------------------

        [Fact]
        public async Task GetOrderForUser_WhenCalled_ReturnsOkWithMappedOrderList()
        {
            // Arrange
            var orderService = new Mock<IOrderService>();
            var mapper = new Mock<IMapper>();
            var controller = new OrdersController(orderService.Object, mapper.Object)
                .WithUser(ControllerTestHelpers.GetClaimsPrincipal(Email));

            IReadOnlyList<Order> orders = new List<Order> { new Order() };
            IReadOnlyList<OrderToReturnDto> mapped = new List<OrderToReturnDto> { new OrderToReturnDto() };

            orderService.Setup(s => s.GetOrdersForUserAsync(Email)).ReturnsAsync(orders);
            mapper.Setup(m => m.Map<IReadOnlyList<OrderToReturnDto>>(orders)).Returns(mapped);

            // Act
            var result = await controller.GetOrderForUser();

            // Assert
            var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
            okResult.Value.Should().BeSameAs(mapped);
        }

        // ------------------------------------------------------------------
        // GetOrderByIdForUser
        // ------------------------------------------------------------------

        [Fact]
        public async Task GetOrderByIdForUser_WhenOrderExists_ReturnsMappedOrderToReturnDto()
        {
            // Arrange
            var orderService = new Mock<IOrderService>();
            var mapper = new Mock<IMapper>();
            var controller = new OrdersController(orderService.Object, mapper.Object)
                .WithUser(ControllerTestHelpers.GetClaimsPrincipal(Email));

            var order = new Order();
            var dto = new OrderToReturnDto();

            orderService.Setup(s => s.GetOrderByIdAsync(5, Email)).ReturnsAsync(order);
            mapper.Setup(m => m.Map<Order, OrderToReturnDto>(It.IsAny<Order>())).Returns(dto);

            // Act
            var result = await controller.GetOrderByIdForUser(5);

            // Assert
            // Success path returns the mapped DTO directly, so ActionResult<T>.Value is populated
            // and ActionResult<T>.Result is null (no wrapping IActionResult).
            result.Value.Should().BeSameAs(dto);
            result.Result.Should().BeNull();
        }

        [Fact]
        public async Task GetOrderByIdForUser_WhenOrderNotFound_ReturnsNotFoundWithApiResponse404()
        {
            // Arrange
            var orderService = new Mock<IOrderService>();
            var mapper = new Mock<IMapper>();
            var controller = new OrdersController(orderService.Object, mapper.Object)
                .WithUser(ControllerTestHelpers.GetClaimsPrincipal(Email));

            orderService.Setup(s => s.GetOrderByIdAsync(999, Email)).ReturnsAsync((Order)null);

            // Act
            var result = await controller.GetOrderByIdForUser(999);

            // Assert
            var notFound = result.Result.Should().BeOfType<NotFoundObjectResult>().Subject;
            var apiResponse = notFound.Value.Should().BeOfType<ApiResponse>().Subject;
            apiResponse.StatusCode.Should().Be(404);
        }

        // ------------------------------------------------------------------
        // GetDeliveryMethods
        // ------------------------------------------------------------------

        [Fact]
        public async Task GetDeliveryMethods_WhenCalled_ReturnsOkWithDeliveryMethods()
        {
            // Arrange
            var orderService = new Mock<IOrderService>();
            var mapper = new Mock<IMapper>();
            var controller = new OrdersController(orderService.Object, mapper.Object)
                .WithUser(ControllerTestHelpers.GetClaimsPrincipal(Email));

            IReadOnlyList<DeliveryMethod> methods = new List<DeliveryMethod> { new DeliveryMethod() };

            orderService.Setup(s => s.GetDeliveryMethodsAsync()).ReturnsAsync(methods);

            // Act
            var result = await controller.GetDeliveryMethods();

            // Assert
            var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
            okResult.Value.Should().BeSameAs(methods);
        }
    }
}
