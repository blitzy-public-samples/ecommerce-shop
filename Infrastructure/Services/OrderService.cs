using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using API.Specifications;
using Core.Entities;
using Core.Entities.OrderAggregate;
using Core.Interfaces;

namespace Infrastructure.Services
{
    public class OrderService : IOrderService
    {
        private readonly IBasketRepository _basketRepo;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IPaymentService _paymentService;
        // Flash-Sale feature: reservation service used to consume this session's inventory reservations after a successful order write.
        private readonly IInventoryReservationService _inventoryReservationService;

        // Flash-Sale feature: 4th parameter (inventoryReservationService) added for the post-commit reservation-consume hook.
        public OrderService(IBasketRepository basketRepo, IUnitOfWork unitOfWork, IPaymentService paymentService,
            IInventoryReservationService inventoryReservationService)
        {
            _basketRepo = basketRepo;
            _unitOfWork = unitOfWork;
            _paymentService = paymentService;
            _inventoryReservationService = inventoryReservationService; // Flash-Sale feature
        }

        public async Task<Order> CreateOrderAsync(string buyerEmail, int deliveryMethodId, string basketId, Address shippingAddress)
        {
            // get basket from the repo
            var basket = await _basketRepo.GetBasketAsync(basketId);
            // get items from the product repo
            var items = new List<OrderItem>();
            foreach (var item in basket.Items)
            {
                var productItem = await _unitOfWork.Repository<Product>().GetByIdAsync(item.Id);
                var itemOrdered = new ProductItemOrdered(productItem.Id, productItem.Name, productItem.PictureUrl);
                var orderItem = new OrderItem(itemOrdered, productItem.Price, item.Quantity);
                items.Add(orderItem);
            }
            // get delivery method from repo
            var deliveryMethod = await _unitOfWork.Repository<DeliveryMethod>().GetByIdAsync(deliveryMethodId);
            // calc subtotal
            var subtotal = items.Sum(item => item.Price * item.Quantity);
            
            // check to see if order exists
            var spec = new OrderByPaymentIntentIdSpecification(basket.PaymentIntentId);
            var existingOrder = await _unitOfWork.Repository<Order>().GetEntityWithSpec(spec);

            if (existingOrder != null)
            {
                _unitOfWork.Repository<Order>().Delete(existingOrder);
                await _paymentService.CreateOrUpdatePaymentIntent(basket.PaymentIntentId);
            }
            // create order
            var order = new Order(items, buyerEmail, shippingAddress, deliveryMethod, subtotal, basket.PaymentIntentId);
            _unitOfWork.Repository<Order>().Add(order);
            // save to db
            var result = await _unitOfWork.Complete();

            if (result <= 0) return null;

            // Flash-Sale feature: consume this session's inventory reservations after a successful order write.
            // Defensive - a missing reservation must never break the existing order flow. Totals stay based on products.price.
            // basketId is the reservation sessionId (client basket UUID from localStorage['basket_id'], reused per AAP R8).
            try
            {
                // Build the ordered lines from the basket (item.Id = productId, item.Quantity). The reservation
                // service matches this session's ACTIVE holds by (SessionId, ProductId) and marks them Consumed
                // (never deletes), keeping the sold units subtracted from availability (zero-oversell, AAP R3).
                var consumeLines = basket.Items
                    .Select(i => new ReservationConsumeLine(i.Id, i.Quantity))
                    .ToList();
                await _inventoryReservationService.ConsumeReservationsAsync(basketId, consumeLines);
            }
            catch { /* swallow: reservation consumption is best-effort and must not fail checkout */ }

            // return order
            return order;
        }

        public async Task<IReadOnlyList<Order>> GetOrdersForUserAsync(string buyerEmail)
        {
            var spec = new OrdersWithItemsAndOrderingSpecification(buyerEmail);
            return await _unitOfWork.Repository<Order>().ListAsync(spec);
        }

        public async Task<Order> GetOrderByIdAsync(int id, string buyerEmail)
        {
            var spec = new OrdersWithItemsAndOrderingSpecification(id, buyerEmail);
            return await _unitOfWork.Repository<Order>().GetEntityWithSpec(spec);
        }

        public async Task<IReadOnlyList<DeliveryMethod>> GetDeliveryMethodsAsync()
        {
            return await _unitOfWork.Repository<DeliveryMethod>().ListAllAsync();
        }
    }
}