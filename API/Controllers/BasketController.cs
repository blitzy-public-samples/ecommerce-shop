using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using API.Dtos;
using API.Errors;
using AutoMapper;
using Core.Entities;
using Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers
{
    public class BasketController : BaseApiController
    {
        private readonly IBasketRepository _basketRepository;
        private readonly IMapper _mapper;
        private readonly IInventoryService _inventoryService;

        public BasketController(IBasketRepository basketRepository, IMapper mapper, IInventoryService inventoryService)
        {
            _basketRepository = basketRepository;
            _mapper = mapper;
            _inventoryService = inventoryService;
        }

        [HttpGet]
        public async Task<ActionResult<CustomerBasket>> GetBasketById(string id)
        {
            var basket = await _basketRepository.GetBasketAsync(id);
            return Ok(basket ?? new CustomerBasket(id));
        }

        [HttpPost]
        public async Task<ActionResult<CustomerBasket>> UpdateBasket(CustomerBasketDto basket)
        {
            var customerBasket = _mapper.Map<CustomerBasketDto, CustomerBasket>(basket);

            // Normalize duplicate lines: collapse repeated entries for the same product into a single line
            // whose quantity is the SUM of the duplicates. Without this, the last duplicate's quantity would
            // "win" for the reservation while the basket still claimed the combined total, so the persisted
            // basket could claim more than was actually reserved (one of the F2 sub-defects).
            var aggregatedItems = (customerBasket.Items ?? new List<BasketItem>())
                .GroupBy(i => i.Id)
                .Select(g =>
                {
                    var line = g.First();
                    line.Quantity = g.Sum(x => x.Quantity);
                    return line;
                })
                .ToList();
            customerBasket.Items = aggregatedItems;

            // Capture the currently-persisted basket BEFORE mutating anything so we can detect which product
            // lines were removed by this update and release their holds (F1).
            var priorBasket = await _basketRepository.GetBasketAsync(customerBasket.Id);
            var newProductIds = new HashSet<int>(aggregatedItems.Select(i => i.Id));
            var removedProductIds = priorBasket?.Items?
                .Select(i => i.Id)
                .Where(id => !newProductIds.Contains(id))
                .Distinct()
                .ToList() ?? new List<int>();

            // Also capture the quantity the PRIOR persisted basket claimed per product. If this update has
            // to be rejected (a line is unreservable), we use these to restore the holds this call granted
            // back to exactly the prior basket's claim (F6-D1 compensation below). Duplicate prior lines are
            // summed defensively so the prior claim is the true per-product total.
            var priorQuantities = (priorBasket?.Items ?? new List<BasketItem>())
                .GroupBy(i => i.Id)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

            // Reserve-BEFORE-persist: attempt to hold the requested total for every line first. Each call takes
            // a PostgreSQL row lock on the product and returns whether the full requested quantity was granted.
            // We never persist a basket that claims stock we could not safely reserve (F2).
            var unreservable = new List<int>();
            var grantedProductIds = new List<int>();
            foreach (var item in aggregatedItems)
            {
                var granted = await _inventoryService.ExtendReservationAsync(customerBasket.Id, item.Id, item.Quantity);
                if (granted) grantedProductIds.Add(item.Id);
                else unreservable.Add(item.Id);
            }

            if (unreservable.Count > 0)
            {
                // F6-D1: each ExtendReservationAsync above is self-committing (it opens and commits its own
                // transaction), so a partial failure — earlier lines granted, a later line short — would
                // otherwise leave the granted lines as ORPHANED Active holds (with their Redis counters
                // decremented) while this update is rejected and the basket is NOT persisted. That silently
                // violates the basket<->reservation consistency invariant and can transiently make otherwise
                // available stock unpurchasable for other shoppers. To keep the reserve step atomic at the
                // basket level, compensate by restoring every hold this call granted back to exactly what the
                // PRIOR persisted basket claimed (which remains the persisted state, since we reject below):
                //   - product present in the prior basket  -> shrink/grow the hold back to that prior total;
                //   - product absent from the prior basket -> release the freshly-created hold entirely.
                // All compensation flows through IInventoryService, the sole permitted stock writer.
                foreach (var productId in grantedProductIds)
                {
                    if (priorQuantities.TryGetValue(productId, out var priorQuantity) && priorQuantity > 0)
                    {
                        await _inventoryService.ExtendReservationAsync(customerBasket.Id, productId, priorQuantity);
                    }
                    else
                    {
                        await _inventoryService.ReleaseReservationAsync(customerBasket.Id, productId);
                    }
                }

                // At least one line could not be fully reserved: reject the whole update (treat the basket
                // atomically) and leave the previously-persisted basket untouched. The client learns exactly
                // which product(s) are short so it can adjust quantities.
                var ids = string.Join(", ", unreservable);
                return Conflict(new ApiResponse(409,
                    $"Insufficient stock to reserve the requested quantity for product(s): {ids}. The basket was not updated."));
            }

            // All lines reserved: persist the normalized basket, then release holds for any lines that were
            // removed by this update so their stock is returned to availability immediately (F1).
            var updatedBasket = await _basketRepository.UpdateBasketAsync(customerBasket);

            foreach (var removedProductId in removedProductIds)
            {
                await _inventoryService.ReleaseReservationAsync(customerBasket.Id, removedProductId);
            }

            return Ok(updatedBasket);
        }

        [HttpDelete]
        public async Task DeleteBasketAsync(string id)
        {
            // F1: release every Active hold for this basket BEFORE deleting it, so deleting a basket restores
            // available stock immediately and leaves no orphaned Active reservation behind.
            await _inventoryService.ReleaseAllReservationsForBasketAsync(id);
            await _basketRepository.DeleteBasketAsync(id);
        }
    }
}