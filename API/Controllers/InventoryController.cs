using System.Threading.Tasks;
using API.Dtos;
using API.Errors;
using API.Helpers;
using AutoMapper;
using Core.Entities;
using Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers
{
    // Flash-Sale feature (AAP §0.4.1 Group 3, R3 zero-oversell + R8 session-identity reuse): reservation-based
    // inventory endpoints. This is a brand-new, self-contained controller; no existing controller is modified.
    //
    // Route: [ApiController] and [Route("api/[controller]")] are inherited from BaseApiController (which derives
    // ControllerBase), so they are deliberately NOT re-declared here. The [controller] token resolves to
    // "Inventory" -> base route "api/inventory", which already matches the committed Angular client path
    // ("inventory/reserve"); no absolute route override is required (unlike FlashSalesController).
    //
    // Auth: intentionally NOT [Authorize]. A flash-sale reservation is keyed to the client basket UUID
    // (localStorage['basket_id']) reused as the session id (AAP R8 — no new identity concept), so anonymous
    // shoppers must be able to reserve. Per-session abuse is bounded by the SessionRateLimitFilter instead.
    public class InventoryController : BaseApiController
    {
        private readonly IInventoryReservationService _reservationService;
        private readonly IMapper _mapper;

        // Constructor injection into private readonly fields, matching the ProductsController/OrdersController
        // style. IInventoryReservationService is registered AddScoped in ApplicationServicesExtensions.
        public InventoryController(IInventoryReservationService reservationService, IMapper mapper)
        {
            _reservationService = reservationService;
            _mapper = mapper;
        }

        // POST /api/inventory/reserve
        //
        // Rate-limited to 10 requests / minute / session -> HTTP 429 (AAP §0.6). SessionRateLimitFilter is a
        // plain IAsyncActionFilter attribute (no DI dependencies), so it is applied directly as an attribute
        // ([SessionRateLimitFilter]) — NOT via [ServiceFilter(...)]. The 429 (body {"error":"RATE_LIMIT_EXCEEDED"})
        // is produced by the filter short-circuiting BEFORE this action executes; the controller itself NEVER
        // emits 429.
        [SessionRateLimitFilter]
        // Relative template combines with the inherited api/[controller] prefix -> /api/inventory/reserve.
        [HttpPost("reserve")]
        public async Task<ActionResult<ReservationToReturnDto>> Reserve(ReserveInventoryDto dto)
        {
            // ReserveInventoryDto is validated automatically by [ApiController] (Range/Required/UUID data
            // annotations); an invalid payload never reaches this body (400 is produced by the model-state filter).
            //
            // The single read-modify-write optimistic-concurrency retry lives INSIDE
            // InventoryReservationService (guarded by FlashSale.Version, retried EXACTLY once). The controller
            // NEVER retries; it only translates the ReservationResult into the exact HTTP contract below.
            // Argument order is exact: (ProductId, Quantity, SessionId).
            var result = await _reservationService.ReserveAsync(dto.ProductId, dto.Quantity, dto.SessionId);

            switch (result.Outcome)
            {
                case ReservationOutcome.Success:
                    // 200 OK with the persisted reservation (Id, ProductId, Quantity, SessionId, ExpiresAt).
                    return Ok(_mapper.Map<InventoryReservation, ReservationToReturnDto>(result.Reservation));

                case ReservationOutcome.InsufficientStock:
                    // EXACT body required by the user contract (§0.1.2): {"error":"INSUFFICIENT_STOCK","available":N}.
                    // Emitted via an anonymous object (NOT wrapped in ApiResponse) so it serializes to exactly the
                    // required shape. System.Text.Json's camelCase policy only affects property NAMES ("error" and
                    // "available" are already lowercase -> unchanged) and never string VALUES, so "INSUFFICIENT_STOCK"
                    // is preserved verbatim. Conflict(...) is the ControllerBase helper returning HTTP 409.
                    return Conflict(new { error = "INSUFFICIENT_STOCK", available = result.Available });

                case ReservationOutcome.Conflict:
                    // EXACT body required by the user contract (§0.1.2): {"error":"RESERVATION_CONFLICT"}.
                    // Returned after the service's single-retry optimistic-concurrency guard fails twice.
                    return Conflict(new { error = "RESERVATION_CONFLICT" });

                default:
                    // Defensive: switch exhaustiveness for any future ReservationOutcome value. Not expected to be hit.
                    return BadRequest(new ApiResponse(400));
            }
        }

        // DELETE /api/inventory/reserve/{id}
        //
        // Explicit, ownership-checked release of a reservation (interface review finding F02). The backing
        // IInventoryReservationService.ReleaseAsync(reservationId, sessionId) requires the owning sessionId so a
        // caller can only release a hold it owns; it returns a ReleaseOutcome that this controller maps to the
        // documented HTTP contract (204 Released / 404 NotFound / 403 Forbidden). The service internally
        // rebroadcasts InventoryUpdated when stock is freed, so no broadcast call is needed here.
        //
        // sessionId is supplied via the query string ([FromQuery]) rather than the route: this keeps the route
        // template exactly /api/inventory/reserve/{id} (matching the Angular client DELETE) while carrying the
        // ownership token, and is trivially produced by HttpClient.delete(url, { params }).
        [HttpDelete("reserve/{id}")]
        public async Task<ActionResult> ReleaseReservation(int id, [FromQuery] string sessionId)
        {
            // Guard: the ownership token is mandatory. Without it the release can never be authorized, so fail fast
            // with a clear 400 instead of leaking whether the reservation exists to a session-less caller.
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return BadRequest(new ApiResponse(400, "SessionId is required to release a reservation"));
            }

            var outcome = await _reservationService.ReleaseAsync(id, sessionId);

            switch (outcome)
            {
                case ReleaseOutcome.Released:
                    // 204 No Content: the hold was released (Status = Released, stock returned to the pool). A DELETE
                    // that succeeds carries no response body, so NoContent() is the REST-correct result.
                    return NoContent();

                case ReleaseOutcome.NotFound:
                    // 404: no reservation with that id exists.
                    return NotFound(new ApiResponse(404));

                case ReleaseOutcome.Forbidden:
                    // 403: the reservation exists but is owned by a different session; the caller may not release it.
                    return StatusCode(StatusCodes.Status403Forbidden,
                        new ApiResponse(StatusCodes.Status403Forbidden, "You do not own this reservation"));

                default:
                    // Defensive: switch exhaustiveness for any future ReleaseOutcome value. Not expected to be hit.
                    return BadRequest(new ApiResponse(400));
            }
        }
    }
}
