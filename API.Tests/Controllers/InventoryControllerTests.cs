using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using API.Controllers;
using API.Dtos;
using API.Errors;                              // ApiResponse
using API.Helpers;                             // SessionRateLimitFilter (reflection + isolated filter test)
using AutoMapper;
using Core.Entities;                           // InventoryReservation
using Core.Interfaces;                         // IInventoryReservationService, ReservationResult, ReservationOutcome, ReleaseOutcome
using FluentAssertions;
using Microsoft.AspNetCore.Http;               // DefaultHttpContext (isolated filter test)
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;   // ActionDescriptor (isolated filter test)
using Microsoft.AspNetCore.Mvc.Filters;        // IFilterMetadata / ActionExecuting|ExecutedContext (isolated filter test)
using Microsoft.AspNetCore.Mvc.ModelBinding;   // ModelStateDictionary (isolated filter test)
using Microsoft.AspNetCore.Routing;            // RouteData (isolated filter test)
using Moq;
using Xunit;

namespace API.Tests.Controllers
{
    /// <summary>
    /// Unit tests for <see cref="InventoryController"/> — the reservation surface of the Real-Time
    /// Inventory &amp; Flash Sale feature (AAP §0.4.1 Group 3 / §0.5.1 Tests).
    /// <para>
    /// The controller is exercised in complete isolation: its two collaborators
    /// (<see cref="IInventoryReservationService"/> and <see cref="IMapper"/>) are replaced with Moq
    /// doubles, so no database, SignalR hub, HTTP pipeline, or AutoMapper configuration is touched. A
    /// fresh set of mocks is created for every test via <see cref="CreateController"/> so no mutable
    /// state is shared between tests.
    /// </para>
    /// <para>
    /// Because these are direct in-process method calls, the action-level
    /// <c>[SessionRateLimitFilter]</c> does NOT run (the ASP.NET Core filter pipeline is not involved).
    /// Its <em>presence</em> on <see cref="InventoryController.Reserve"/> is therefore asserted
    /// structurally via reflection, and its 429 short-circuit behaviour is exercised by driving the
    /// filter in isolation in the region at the bottom of this fixture. The reservation
    /// <c>session_id</c> is taken from the request DTO (the client basket UUID reused as the session
    /// key — AAP R8), so no <c>HttpContext.User</c>/claims setup is required.
    /// </para>
    /// <para>
    /// The exact HTTP contracts under test are the binding user examples (AAP §0.1.2): a successful
    /// reservation returns the mapped DTO inside <c>200 OK</c>; insufficient stock returns
    /// <c>409 {"error":"INSUFFICIENT_STOCK","available":N}</c>; an optimistic-concurrency conflict
    /// returns <c>409 {"error":"RESERVATION_CONFLICT"}</c>; and the DELETE release maps the service's
    /// <see cref="ReleaseOutcome"/> to <c>204 No Content</c> / <c>404 Not Found</c> / <c>403 Forbidden</c>
    /// / <c>400 Bad Request</c>. Tests follow the repository convention
    /// <c>MethodName_StateUnderTest_ExpectedBehavior</c> with an Arrange-Act-Assert structure and assert
    /// with FluentAssertions plus Moq verification. No production code is modified by these tests.
    /// </para>
    /// </summary>
    public class InventoryControllerTests
    {
        /// <summary>
        /// Builds an <see cref="InventoryController"/> wired to a brand-new pair of mocks (fresh per
        /// invocation, guaranteeing that individual tests never share mutable mock state).
        /// </summary>
        /// <returns>
        /// A tuple of the controller under test together with its underlying
        /// <see cref="Mock{IInventoryReservationService}"/> and <see cref="Mock{IMapper}"/> so each test
        /// can arrange setups and verify interactions.
        /// </returns>
        private static (InventoryController controller, Mock<IInventoryReservationService> service, Mock<IMapper> mapper) CreateController()
        {
            var service = new Mock<IInventoryReservationService>();
            var mapper = new Mock<IMapper>();
            var controller = new InventoryController(service.Object, mapper.Object);
            return (controller, service, mapper);
        }

        // ------------------------------------------------------------------
        // Reserve (POST /api/inventory/reserve)
        // ------------------------------------------------------------------

        /// <summary>
        /// Happy path: on a successful reservation the controller maps the persisted
        /// <see cref="InventoryReservation"/> and returns the resulting
        /// <see cref="ReservationToReturnDto"/> inside <c>200 OK</c> (an <see cref="OkObjectResult"/>,
        /// which populates <see cref="ActionResult{TValue}.Result"/> rather than <c>.Value</c>).
        /// </summary>
        [Fact]
        public async Task Reserve_WhenReservationSucceeds_ReturnsOkWithMappedReservationDto()
        {
            // Arrange
            var (controller, service, mapper) = CreateController();
            var dto = new ReserveInventoryDto { ProductId = 42, Quantity = 3, SessionId = Guid.NewGuid().ToString() };
            var reservation = new InventoryReservation
            {
                Id = 7,
                ProductId = 42,
                Quantity = 3,
                SessionId = dto.SessionId,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            var reservationResult = new ReservationResult
            {
                Outcome = ReservationOutcome.Success,
                Reservation = reservation,
                Available = 97
            };
            var mapped = new ReservationToReturnDto
            {
                Id = 7,
                ProductId = 42,
                Quantity = 3,
                SessionId = dto.SessionId,
                ExpiresAt = reservation.ExpiresAt
            };

            service.Setup(s => s.ReserveAsync(42, 3, dto.SessionId)).ReturnsAsync(reservationResult);
            mapper.Setup(m => m.Map<InventoryReservation, ReservationToReturnDto>(reservation)).Returns(mapped);

            // Act
            var result = await controller.Reserve(dto);

            // Assert
            var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
            okResult.Value.Should().BeSameAs(mapped);
        }

        /// <summary>
        /// The reservation <c>session_id</c> must be the value supplied on the request DTO (the client
        /// basket UUID — AAP R8), forwarded verbatim to the service together with the DTO's product id
        /// and quantity, in that exact argument order.
        /// </summary>
        [Fact]
        public async Task Reserve_WhenSuccessful_ForwardsRequestSessionIdToService()
        {
            // Arrange
            var (controller, service, mapper) = CreateController();
            var sessionId = Guid.NewGuid().ToString();
            var dto = new ReserveInventoryDto { ProductId = 10, Quantity = 2, SessionId = sessionId };
            var reservation = new InventoryReservation { Id = 1, ProductId = 10, Quantity = 2, SessionId = sessionId };

            service
                .Setup(s => s.ReserveAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
                .ReturnsAsync(new ReservationResult { Outcome = ReservationOutcome.Success, Reservation = reservation, Available = 5 });
            mapper
                .Setup(m => m.Map<InventoryReservation, ReservationToReturnDto>(It.IsAny<InventoryReservation>()))
                .Returns(new ReservationToReturnDto { SessionId = sessionId });

            // Act
            await controller.Reserve(dto);

            // Assert
            service.Verify(s => s.ReserveAsync(dto.ProductId, dto.Quantity, sessionId), Times.Once);
        }

        /// <summary>
        /// Insufficient stock must produce exactly <c>HTTP 409</c> with the binding user-contract body
        /// <c>{"error":"INSUFFICIENT_STOCK","available":N}</c>. The body is an anonymous object, so the
        /// container (<see cref="ConflictObjectResult"/>) and status code are asserted, and the value is
        /// matched structurally with <c>BeEquivalentTo</c>.
        /// </summary>
        [Fact]
        public async Task Reserve_WhenInsufficientStock_Returns409WithInsufficientStockBodyAndAvailableCount()
        {
            // Arrange
            var (controller, service, _) = CreateController();
            var dto = new ReserveInventoryDto { ProductId = 1, Quantity = 10, SessionId = Guid.NewGuid().ToString() };
            service
                .Setup(s => s.ReserveAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
                .ReturnsAsync(new ReservationResult { Outcome = ReservationOutcome.InsufficientStock, Available = 5 });

            // Act
            var result = await controller.Reserve(dto);

            // Assert
            var conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
            conflict.StatusCode.Should().Be(409);
            conflict.Value.Should().BeEquivalentTo(new { error = "INSUFFICIENT_STOCK", available = 5 });
        }

        /// <summary>
        /// The insufficient-stock branch must NOT create/return a reservation: no mapping occurs and the
        /// result is not an <see cref="OkObjectResult"/> (proves the "no partial reservation" guarantee).
        /// </summary>
        [Fact]
        public async Task Reserve_WhenInsufficientStock_DoesNotMapOrReturnAnyReservation()
        {
            // Arrange
            var (controller, service, mapper) = CreateController();
            var dto = new ReserveInventoryDto { ProductId = 1, Quantity = 10, SessionId = Guid.NewGuid().ToString() };
            service
                .Setup(s => s.ReserveAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
                .ReturnsAsync(new ReservationResult { Outcome = ReservationOutcome.InsufficientStock, Available = 5 });

            // Act
            var result = await controller.Reserve(dto);

            // Assert
            result.Result.Should().NotBeOfType<OkObjectResult>();
            mapper.Verify(m => m.Map<InventoryReservation, ReservationToReturnDto>(It.IsAny<InventoryReservation>()), Times.Never);
        }

        /// <summary>
        /// A version/optimistic-concurrency conflict must produce exactly <c>HTTP 409</c> with the
        /// binding user-contract body <c>{"error":"RESERVATION_CONFLICT"}</c> (no <c>available</c> field).
        /// </summary>
        [Fact]
        public async Task Reserve_WhenVersionConflict_Returns409WithReservationConflictBody()
        {
            // Arrange
            var (controller, service, _) = CreateController();
            var dto = new ReserveInventoryDto { ProductId = 1, Quantity = 2, SessionId = Guid.NewGuid().ToString() };
            service
                .Setup(s => s.ReserveAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
                .ReturnsAsync(new ReservationResult { Outcome = ReservationOutcome.Conflict });

            // Act
            var result = await controller.Reserve(dto);

            // Assert
            var conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
            conflict.StatusCode.Should().Be(409);
            conflict.Value.Should().BeEquivalentTo(new { error = "RESERVATION_CONFLICT" });
        }

        /// <summary>
        /// The controller performs the single service call and never retries server-side (the one
        /// read-modify-write retry lives inside the Infrastructure reservation service, guarded by the
        /// flash-sale concurrency token). On a conflict the controller issues exactly one
        /// <see cref="IInventoryReservationService.ReserveAsync"/> call.
        /// </summary>
        [Fact]
        public async Task Reserve_WhenVersionConflict_DoesNotRetryServiceSideMoreThanOnce()
        {
            // Arrange
            var (controller, service, _) = CreateController();
            var dto = new ReserveInventoryDto { ProductId = 1, Quantity = 2, SessionId = Guid.NewGuid().ToString() };
            service
                .Setup(s => s.ReserveAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
                .ReturnsAsync(new ReservationResult { Outcome = ReservationOutcome.Conflict });

            // Act
            await controller.Reserve(dto);

            // Assert
            service.Verify(s => s.ReserveAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()), Times.Once);
        }

        /// <summary>
        /// Defensive branch: when the service reports <see cref="ReservationOutcome.Invalid"/> (a
        /// service-boundary validation failure), the controller's <c>default</c> switch arm returns
        /// <c>400 Bad Request</c> carrying an <see cref="ApiResponse"/> with status 400.
        /// </summary>
        [Fact]
        public async Task Reserve_WhenServiceReportsInvalid_ReturnsBadRequestWithApiResponse400()
        {
            // Arrange
            var (controller, service, mapper) = CreateController();
            var dto = new ReserveInventoryDto { ProductId = 1, Quantity = 1, SessionId = Guid.NewGuid().ToString() };
            service
                .Setup(s => s.ReserveAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
                .ReturnsAsync(new ReservationResult { Outcome = ReservationOutcome.Invalid });

            // Act
            var result = await controller.Reserve(dto);

            // Assert
            var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
            var api = badRequest.Value.Should().BeOfType<ApiResponse>().Subject;
            api.StatusCode.Should().Be(400);
            // No reservation is produced on the defensive branch, so the mapper is never invoked.
            mapper.Verify(m => m.Map<InventoryReservation, ReservationToReturnDto>(It.IsAny<InventoryReservation>()), Times.Never);
        }

        // ------------------------------------------------------------------
        // ReleaseReservation (DELETE /api/inventory/reserve/{id})
        // ------------------------------------------------------------------

        /// <summary>
        /// When the owning session releases an active reservation, the service reports
        /// <see cref="ReleaseOutcome.Released"/> and the action returns <c>204 No Content</c>
        /// (a <see cref="NoContentResult"/>, no body), delegating exactly once to
        /// <see cref="IInventoryReservationService.ReleaseAsync"/> with the reservation id and the
        /// owning session id.
        /// </summary>
        [Fact]
        public async Task ReleaseReservation_WhenReservationReleased_ReturnsNoContent()
        {
            // Arrange
            var (controller, service, _) = CreateController();
            var sessionId = Guid.NewGuid().ToString();
            service.Setup(s => s.ReleaseAsync(10, sessionId)).ReturnsAsync(ReleaseOutcome.Released);

            // Act
            var result = await controller.ReleaseReservation(10, sessionId);

            // Assert
            result.Should().BeOfType<NoContentResult>();
            service.Verify(s => s.ReleaseAsync(10, sessionId), Times.Once);
        }

        /// <summary>
        /// When no reservation with the supplied id exists, the service reports
        /// <see cref="ReleaseOutcome.NotFound"/> and the action returns <c>404 Not Found</c> whose body
        /// is an <see cref="ApiResponse"/> with status 404 and the default "Resource not found" message.
        /// </summary>
        [Fact]
        public async Task ReleaseReservation_WhenReservationNotFound_ReturnsNotFoundWithApiResponse404()
        {
            // Arrange
            var (controller, service, _) = CreateController();
            var sessionId = Guid.NewGuid().ToString();
            service.Setup(s => s.ReleaseAsync(999, sessionId)).ReturnsAsync(ReleaseOutcome.NotFound);

            // Act
            var result = await controller.ReleaseReservation(999, sessionId);

            // Assert
            var notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
            var api = notFound.Value.Should().BeOfType<ApiResponse>().Subject;
            api.StatusCode.Should().Be(404);
            api.Message.Should().Be("Resource not found");
        }

        /// <summary>
        /// When the reservation exists but is owned by a different session, the service reports
        /// <see cref="ReleaseOutcome.Forbidden"/> and the action returns <c>403 Forbidden</c> (an
        /// <see cref="ObjectResult"/> with status 403) carrying an <see cref="ApiResponse"/> explaining
        /// the caller does not own the reservation. This proves ownership enforcement on release.
        /// </summary>
        [Fact]
        public async Task ReleaseReservation_WhenCallerDoesNotOwnReservation_ReturnsForbiddenWithApiResponse403()
        {
            // Arrange
            var (controller, service, _) = CreateController();
            var sessionId = Guid.NewGuid().ToString();
            service.Setup(s => s.ReleaseAsync(5, sessionId)).ReturnsAsync(ReleaseOutcome.Forbidden);

            // Act
            var result = await controller.ReleaseReservation(5, sessionId);

            // Assert
            var forbidden = result.Should().BeOfType<ObjectResult>().Subject;
            forbidden.StatusCode.Should().Be(403);
            var api = forbidden.Value.Should().BeOfType<ApiResponse>().Subject;
            api.StatusCode.Should().Be(403);
            api.Message.Should().Be("You do not own this reservation");
        }

        /// <summary>
        /// When the reservation is owned by the caller but is no longer in a releasable (Active) state —
        /// e.g. already Consumed, Released, or Expired — the service reports
        /// <see cref="ReleaseOutcome.Conflict"/>, which the controller's defensive <c>default</c> switch
        /// arm maps to <c>400 Bad Request</c> with an <see cref="ApiResponse"/> (status 400), refusing to
        /// double-return already-sold or already-freed stock.
        /// </summary>
        [Fact]
        public async Task ReleaseReservation_WhenReservationNotActive_ReturnsBadRequestWithApiResponse400()
        {
            // Arrange
            var (controller, service, _) = CreateController();
            var sessionId = Guid.NewGuid().ToString();
            service.Setup(s => s.ReleaseAsync(3, sessionId)).ReturnsAsync(ReleaseOutcome.Conflict);

            // Act
            var result = await controller.ReleaseReservation(3, sessionId);

            // Assert
            var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
            var api = badRequest.Value.Should().BeOfType<ApiResponse>().Subject;
            api.StatusCode.Should().Be(400);
        }

        /// <summary>
        /// The ownership token is mandatory: when no session id is supplied the action fails fast with
        /// <c>400 Bad Request</c> (an <see cref="ApiResponse"/> stating the session id is required) and
        /// never consults the service — the release can never be authorized without an owner, and the
        /// existence of the reservation must not leak to a session-less caller.
        /// </summary>
        [Fact]
        public async Task ReleaseReservation_WhenSessionIdMissing_ReturnsBadRequestAndDoesNotCallService()
        {
            // Arrange
            var (controller, service, _) = CreateController();

            // Act
            var result = await controller.ReleaseReservation(1, "   ");

            // Assert
            var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
            var api = badRequest.Value.Should().BeOfType<ApiResponse>().Subject;
            api.StatusCode.Should().Be(400);
            api.Message.Should().Be("SessionId is required to release a reservation");
            service.Verify(s => s.ReleaseAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
        }

        // ------------------------------------------------------------------
        // Contract / reflection assertion
        // ------------------------------------------------------------------

        /// <summary>
        /// The reserve endpoint must be guarded by the per-session rate-limit filter
        /// (10 req/min/session → 429). Because the filter does not execute during a direct in-process
        /// call, its application to <see cref="InventoryController.Reserve"/> is verified structurally
        /// via reflection.
        /// </summary>
        [Fact]
        public void Reserve_IsDecoratedWithSessionRateLimitFilter()
        {
            // Arrange
            MethodInfo method = typeof(InventoryController).GetMethod(nameof(InventoryController.Reserve));

            // Act
            var attributes = method.GetCustomAttributes(typeof(SessionRateLimitFilter), inherit: true);

            // Assert
            attributes.Should().NotBeEmpty("Reserve must be rate-limited per session to satisfy the 10 req/min/session → 429 contract");
        }

        #region Isolated SessionRateLimitFilter behaviour (429 short-circuit / 400 invalid session)
        // The rate-limit behaviour is a FILTER concern that does not run when the controller action is
        // invoked directly, so it is exercised here by driving the filter in isolation. The filter keeps
        // its counters in process-static state keyed by the canonical session UUID, so every test below
        // uses a UNIQUE fresh GUID to stay hermetic regardless of any other test or prior run.

        /// <summary>
        /// The 11th request for one session within the 60-second window must be short-circuited with an
        /// HTTP 429 <see cref="ContentResult"/> (body <c>{"error":"RATE_LIMIT_EXCEEDED"}</c>) WITHOUT
        /// invoking the downstream action delegate, while the first 10 are admitted (their result stays
        /// null and <c>next()</c> is invoked). A unique GUID session key keeps the process-static counter
        /// isolated from every other test.
        /// </summary>
        [Fact]
        public async Task SessionRateLimitFilter_WhenSessionExceeds10RequestsPerMinute_ShortCircuitsWith429()
        {
            // Arrange
            var filter = new SessionRateLimitFilter();
            var sessionId = Guid.NewGuid().ToString();
            var nextInvocationCount = 0;

            // Act & Assert — the first 10 requests are within the limit and invoke next().
            for (var i = 0; i < 10; i++)
            {
                var ctx = BuildExecutingContext(sessionId);
                await filter.OnActionExecutionAsync(ctx, () =>
                {
                    nextInvocationCount++;
                    return Task.FromResult(new ActionExecutedContext(ctx, ctx.Filters, ctx.Controller));
                });
                ctx.Result.Should().BeNull("request #{0} is within the 10/min limit", i + 1);
            }

            // The 11th request within the window is rejected with 429 and must NOT invoke next().
            var blockedCtx = BuildExecutingContext(sessionId);
            await filter.OnActionExecutionAsync(blockedCtx, () =>
            {
                nextInvocationCount++;
                return Task.FromResult(new ActionExecutedContext(blockedCtx, blockedCtx.Filters, blockedCtx.Controller));
            });

            // Assert
            nextInvocationCount.Should().Be(10, "only the first 10 requests within the window are admitted");
            var content = blockedCtx.Result.Should().BeOfType<ContentResult>().Subject;
            content.StatusCode.Should().Be(429);
            content.Content.Should().Be("{\"error\":\"RATE_LIMIT_EXCEEDED\"}");
        }

        /// <summary>
        /// A request whose session id is not a canonical v4 UUID cannot be attributed to a per-basket
        /// bucket, so the filter refuses it up front with an HTTP 400 <see cref="ContentResult"/> (body
        /// <c>{"error":"INVALID_SESSION"}</c>) and never invokes the downstream action delegate. This
        /// proves the hardened identity resolution (basket-UUID only; no header/IP fallback).
        /// </summary>
        [Fact]
        public async Task SessionRateLimitFilter_WhenSessionIdIsNotCanonicalUuid_ShortCircuitsWith400InvalidSession()
        {
            // Arrange
            var filter = new SessionRateLimitFilter();
            var nextInvoked = false;
            var ctx = BuildExecutingContext("not-a-canonical-uuid");

            // Act
            await filter.OnActionExecutionAsync(ctx, () =>
            {
                nextInvoked = true;
                return Task.FromResult(new ActionExecutedContext(ctx, ctx.Filters, ctx.Controller));
            });

            // Assert
            nextInvoked.Should().BeFalse("an unidentifiable session must never reach the downstream action");
            var content = ctx.Result.Should().BeOfType<ContentResult>().Subject;
            content.StatusCode.Should().Be(400);
            content.Content.Should().Be("{\"error\":\"INVALID_SESSION\"}");
        }

        /// <summary>
        /// Builds a minimal <see cref="ActionExecutingContext"/> (mirroring the construction pattern in
        /// <c>API.Tests/Helpers/CachedAttributeTests.cs</c>) whose bound action arguments contain a
        /// <see cref="ReserveInventoryDto"/> carrying the supplied <paramref name="sessionId"/>, so the
        /// filter resolves the per-session key from the DTO exactly as it does at runtime.
        /// </summary>
        /// <param name="sessionId">The session id to place on the bound <see cref="ReserveInventoryDto"/>.</param>
        /// <returns>A fully-wired <see cref="ActionExecutingContext"/> ready to pass to the filter.</returns>
        private static ActionExecutingContext BuildExecutingContext(string sessionId)
        {
            var httpContext = new DefaultHttpContext();
            var actionContext = new ActionContext(
                httpContext,
                new RouteData(),
                new ActionDescriptor(),
                new ModelStateDictionary());

            return new ActionExecutingContext(
                actionContext,
                new List<IFilterMetadata>(),
                new Dictionary<string, object>
                {
                    { "dto", new ReserveInventoryDto { ProductId = 1, Quantity = 1, SessionId = sessionId } }
                },
                controller: null);
        }
        #endregion
    }
}
