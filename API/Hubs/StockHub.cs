using System.Threading.Tasks;
using Core.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace API.Hubs
{
    public class StockHub : Hub
    {
        private readonly IInventoryService _inventoryService;

        public StockHub(IInventoryService inventoryService)
        {
            _inventoryService = inventoryService;
        }

        // Client-invoked: join the per-product group and return the current stock to the caller.
        // The Angular StockService relies on the returned int for the initial badge value.
        public async Task<int> SubscribeToProduct(int productId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, productId.ToString());

            var stock = await _inventoryService.GetAvailableStockAsync(productId);

            // Also push the initial value to the caller (optional; the returned int is authoritative).
            await Clients.Caller.SendAsync("StockChanged", productId, stock);

            return stock;
        }
    }
}
