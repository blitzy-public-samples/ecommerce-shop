using System; // Flash-Sale feature (review finding C10): Exception in the structured-logging consume hook.
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using API.Specifications;
using Core.Entities;
using Core.Entities.OrderAggregate;
using Core.Interfaces;
using Microsoft.Extensions.Logging; // Flash-Sale feature (review finding C10): structured error logging.

namespace Infrastructure.Services
{
    public class OrderService : IOrderService
    {
        private readonly IBasketRepository _basketRepo;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IPaymentService _paymentService;
        // Flash-Sale feature: reservation service used to consume this session's inventory reservations after a successful order write.
        private readonly IInventoryReservationService _inventoryReservationService;
        // Flash-Sale feature (review finding C10): logger for the post-commit consume hook so a failure is
        // observably recorded (structured, no session/token leakage) instead of silently discarded.
        private readonly ILogger<OrderService> _logger;

        // Flash-Sale feature: 4th parameter (inventoryReservationService) added for the post-commit
        // reservation-consume hook; 5th parameter (logger, review finding C10) added for structured hook logging.
        public OrderService(IBasketRepository basketRepo, IUnitOfWork unitOfWork, IPaymentService paymentService,
            IInventoryReservationService inventoryReservationService, ILogger<OrderService> logger)
        {
            _basketRepo = basketRepo;
            _unitOfWork = unitOfWork;
            _paymentService = paymentService;
            _inventoryReservationService = inventoryReservationService; // Flash-Sale feature
            _logger = logger; // Flash-Sale feature (review finding C10)
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
            //
            // Build the ordered lines OUTSIDE the try so they are available for the failure log below. item.Id is the
            // productId and item.Quantity the ordered quantity; the reservation service (review finding C08) matches
            // this session's ACTIVE holds by the exact sale authority and ordered quantity and marks them Consumed
            // (never deletes), keeping the sold units subtracted from availability (zero-oversell, AAP R3).
            var consumeLines = basket.Items
                .Select(i => new ReservationConsumeLine(i.Id, i.Quantity))
                .ToList();
            try
            {
                await _inventoryReservationService.ConsumeReservationsAsync(basketId, consumeLines);
            }
            catch (Exception ex)
            {
                // Review finding C10: the hook stays DEFENSIVE (a genuinely missing reservation must never break the
                // already-committed order), but the failure is NO LONGER silently discarded. Emit a STRUCTURED error
                // carrying ONLY the order id and the ordered product ids — NEVER the session id / basket UUID or any
                // auth token — so the event is diagnosable without leaking a session identifier or credential.
                //
                // Durable-repair posture (AAP §0.5.2 minimal-change / single-instance; §0.3.1 no new infrastructure):
                // a dedicated durable outbox + retry job is intentionally OUT OF SCOPE — the AAP permits exactly ONE
                // checkout hook and adds no new persistence/background infrastructure. The in-scope durability
                // guarantees that keep this safe are: (1) an un-consumed hold stays Active and therefore STILL
                // subtracts from availability, so sold units cannot be resold until that hold's TTL lapses;
                // (2) this structured error gives operations the exact order + products to reconcile within that TTL
                // window; and (3) the expiry sweep never reverts a Consumed hold (review finding C09), so any hold
                // that WAS consumed is honored permanently.
                _logger.LogError(ex,
                    "Flash-Sale: failed to consume inventory reservations after committing order {OrderId} " +
                    "(ordered products: {ProductIds}). The order is durable; reconcile the session's reservations " +
                    "before their TTL expires so released stock is not resold.",
                    order.Id, string.Join(",", consumeLines.Select(l => l.ProductId)));
            }

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