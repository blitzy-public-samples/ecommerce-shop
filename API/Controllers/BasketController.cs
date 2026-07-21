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

            // Reserve-BEFORE-persist: attempt to hold the requested total for every line first. Each call takes
            // a PostgreSQL row lock on the product and returns whether the full requested quantity was granted.
            // We never persist a basket that claims stock we could not safely reserve (F2).
            var unreservable = new List<int>();
            foreach (var item in aggregatedItems)
            {
                var granted = await _inventoryService.ExtendReservationAsync(customerBasket.Id, item.Id, item.Quantity);
                if (!granted) unreservable.Add(item.Id);
            }

            if (unreservable.Count > 0)
            {
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