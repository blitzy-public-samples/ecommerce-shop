using System.Threading.Tasks;
using API.Dtos;
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
            var updatedBasket = await _basketRepository.UpdateBasketAsync(customerBasket);

            if (updatedBasket?.Items != null)
            {
                foreach (var item in updatedBasket.Items)
                {
                    await _inventoryService.ExtendReservationAsync(updatedBasket.Id, item.Id, item.Quantity);
                }
            }

            return Ok(updatedBasket);
        }

        [HttpDelete]
        public async Task DeleteBasketAsync(string id)
        {
            await _basketRepository.DeleteBasketAsync(id);
        }
    }
}