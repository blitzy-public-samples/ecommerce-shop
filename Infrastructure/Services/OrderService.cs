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
        private readonly IInventoryService _inventoryService;

        public OrderService(IBasketRepository basketRepo, IUnitOfWork unitOfWork, IPaymentService paymentService, IInventoryService inventoryService)
        {
            _basketRepo = basketRepo;
            _unitOfWork = unitOfWork;
            _paymentService = paymentService;
            _inventoryService = inventoryService;
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

            // Open an explicit transaction on the shared scoped StoreContext so order finalization is
            // atomic AND serialized against concurrent finalizations. CommitReservationAsync below takes
            // a PostgreSQL "SELECT ... FOR UPDATE" row lock on each reserved Product over this SAME
            // context; because the lock lives inside this transaction it is held until CommitTransactionAsync,
            // so two shoppers finalizing orders for the same product cannot both read the pre-decrement
            // stock and silently overwrite each other (the oversell / lost-update defect). On the EF Core
            // InMemory provider this is a no-op and behavior is unchanged.
            await _unitOfWork.BeginTransactionAsync();
            try
            {
                _unitOfWork.Repository<Order>().Add(order);
                // commit the basket's stock reservations (Active -> Committed + permanent pool decrement)
                // staged on the SAME scoped StoreContext as the order, so the single Complete() below
                // flushes order rows and reservation/stock changes together atomically under the row lock.
                await _inventoryService.CommitReservationAsync(basketId);
                // save to db — single atomic flush of the order rows AND the staged reservation/stock changes
                var result = await _unitOfWork.Complete();

                if (result <= 0)
                {
                    // Nothing was written: roll back (releasing the row lock) and abort. The staged
                    // reservation transition to Committed is discarded with the rollback, so the holds
                    // remain Active and consistent with the un-decremented stock.
                    await _unitOfWork.RollbackTransactionAsync();
                    return null;
                }

                // Commit the row lock + all staged changes together, making the stock decrement durable.
                await _unitOfWork.CommitTransactionAsync();
            }
            catch
            {
                // Any failure (DB error, FK violation, etc.) rolls back the whole unit — order rows and
                // the staged reservation/stock changes revert together — then the error propagates.
                await _unitOfWork.RollbackTransactionAsync();
                throw;
            }

            // Flush succeeded and the transaction committed: now (and only now) delete the Redis hold keys
            // for the just-committed reservations. Deferring this until after a successful, committed flush
            // means a rolled-back order never deletes a hold key whose reservation reverted to Active,
            // keeping PostgreSQL and Redis consistent.
            await _inventoryService.FinalizeCommittedHoldsAsync(basketId);

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