using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using API.Dtos;
using API.Errors;
using AutoMapper;
using Core.Entities;
using Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers
{
    /// <summary>
    /// HTTP surface for the Real-Time Inventory and Flash Sale feature (AAP section 0.4.1 Group 3, R2/R6).
    /// Exposes two actions:
    /// <list type="bullet">
    ///   <item><description><c>POST /api/flash-sales</c> — schedule a flash sale (authenticated).</description></item>
    ///   <item><description><c>GET /api/flash-sales/active</c> — list currently-active sales with live availability (deliberately NON-cached).</description></item>
    /// </list>
    /// Conventions mirror the existing controllers (ProductsController / OrdersController): the class derives
    /// from <see cref="BaseApiController"/>, which already supplies <c>[ApiController]</c> and
    /// <c>[Route("api/[controller]")]</c>, so those attributes are intentionally NOT re-declared here.
    /// Collaborators are injected into <c>private readonly</c> fields via the constructor.
    /// </summary>
    public class FlashSalesController : BaseApiController
    {
        // Feature service (interface in Core, implementation in Infrastructure, per Clean Architecture).
        // DI-registered through the existing AddApplicationServices() extension. AAP section 0.6.
        private readonly IFlashSaleService _flashSaleService;

        // AutoMapper projects the service's domain results onto the wire DTOs using the maps declared in
        // API/Helpers/MappingProfiles.cs (only ActiveFlashSale -> FlashSaleDto exists for this controller).
        private readonly IMapper _mapper;

        public FlashSalesController(IFlashSaleService flashSaleService, IMapper mapper)
        {
            _flashSaleService = flashSaleService;
            _mapper = mapper;
        }

        // Scheduling a flash sale requires an authenticated caller; reuse the existing JWT bearer scheme
        // (see API/Extension/IdentityServiceExtensions.cs) — same action-level [Authorize] pattern as
        // PaymentsController/AccountController. AAP R2/R6, section 0.6.
        [Authorize]
        // Absolute route template (leading '/') so the path is exactly '/api/flash-sales' (hyphenated) as
        // required by the AAP and the committed Angular client (client/src/app/shop/flash-sale.service.ts
        // calls 'flash-sales/active'). The inherited BaseApiController [Route("api/[controller]")] would
        // otherwise yield 'api/flashsales' (no hyphen). An absolute action route bypasses the controller
        // prefix deterministically.
        [HttpPost("/api/flash-sales")]
        public async Task<ActionResult<FlashSaleDto>> CreateFlashSale(CreateFlashSaleDto dto)
        {
            // Data-annotation + IValidatableObject rules on CreateFlashSaleDto are auto-enforced by the
            // inherited [ApiController] (returns 400 on an invalid model), so no manual ModelState check is
            // needed. The service is nonetheless a second trust boundary that re-validates the request and
            // reports a typed FlashSaleScheduleResult (product existence, window/allocation/price rules, and
            // schedule overlap are authority checks the DTO cannot express); this action translates that
            // outcome into the matching HTTP status. Argument order is the exact IFlashSaleService.ScheduleAsync
            // contract.
            var result = await _flashSaleService.ScheduleAsync(
                dto.ProductId, dto.StartAt, dto.EndAt, dto.SalePrice, dto.StockAllocation);

            switch (result.Outcome)
            {
                case FlashSaleScheduleOutcome.Success:
                {
                    // MappingProfiles has NO FlashSale->FlashSaleDto map; it only maps ActiveFlashSale->FlashSaleDto.
                    // Wrap the freshly-created sale in an ActiveFlashSale. Review finding M12: use the AUTHORITATIVE
                    // post-commit availability the service computed (result.QuantityAvailable) rather than fabricating
                    // it from StockAllocation here. For an immediately-active sale the service re-reads the same
                    // sale-scoped aggregate that GET /api/flash-sales/active uses, so the value this action returns
                    // is exactly what a subsequent active-list fetch would report — the controller no longer invents
                    // an availability figure or assumes zero reservations.
                    var mapped = _mapper.Map<ActiveFlashSale, FlashSaleDto>(
                        new ActiveFlashSale
                        {
                            Sale = result.FlashSale,
                            QuantityAvailable = result.QuantityAvailable
                        });
                    return Ok(mapped);
                }

                // The referenced product does not exist -> 404.
                case FlashSaleScheduleOutcome.ProductNotFound:
                    return NotFound(new ApiResponse(404, "Product not found"));

                // A sale already occupies part of the requested window for this product -> 409.
                case FlashSaleScheduleOutcome.Overlap:
                    return Conflict(new ApiResponse(409,
                        "An overlapping flash sale already exists for this product"));

                // Remaining outcomes are client input errors the service re-validated -> 400 with a message
                // naming the specific rule that failed.
                case FlashSaleScheduleOutcome.InvalidWindow:
                    return BadRequest(new ApiResponse(400,
                        "The flash-sale window is invalid; EndAt must be strictly after StartAt"));
                case FlashSaleScheduleOutcome.InvalidAllocation:
                    return BadRequest(new ApiResponse(400, "StockAllocation must be greater than zero"));
                case FlashSaleScheduleOutcome.InvalidSalePrice:
                    return BadRequest(new ApiResponse(400, "SalePrice must be greater than zero"));
                case FlashSaleScheduleOutcome.SalePriceNotBelowBasePrice:
                    return BadRequest(new ApiResponse(400,
                        "SalePrice must be below the product's base price"));

                default:
                    return BadRequest(new ApiResponse(400));
            }
        }

        // DELIBERATELY NOT decorated with [Cached]. The [Cached(600)] Redis response cache used on
        // ProductsController would mask live price/stock changes for up to 600s; real-time accuracy is a
        // hard requirement, so this active-sales query must always hit the service. AAP section 0.6.
        // Review finding N1: an optional ?productId query parameter narrows the result to a single product's
        // active sale(s) so the product page fetches only what it needs; omitting it preserves the full
        // active-list behaviour. A Cache-Control: no-store response header is also emitted so no shared or
        // browser cache retains this real-time payload (defence-in-depth alongside the absence of [Cached]).
        [HttpGet("/api/flash-sales/active")]
        public async Task<ActionResult<IReadOnlyList<FlashSaleDto>>> GetActiveSales([FromQuery] int? productId = null)
        {
            // N1: explicitly forbid any caching layer from retaining this real-time response.
            Response.Headers["Cache-Control"] = "no-store";

            var sales = await _flashSaleService.GetActiveSalesAsync(productId);
            return Ok(_mapper.Map<IReadOnlyList<ActiveFlashSale>, IReadOnlyList<FlashSaleDto>>(sales));
        }
    }
}
