using System;
using System.Threading.Tasks;
using Core.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;

namespace API.Hubs
{
    public class StockHub : Hub
    {
        private readonly IInventoryService _inventoryService;
        private readonly IConfiguration _config;

        // IConfiguration is an OPTIONAL trailing dependency (defaults to null): the DI container always
        // supplies it in production, and making it optional keeps the hub constructible without config
        // (e.g., in a focused test). When null, LowStockThreshold falls back to the documented default.
        public StockHub(IInventoryService inventoryService, IConfiguration config = null)
        {
            _inventoryService = inventoryService;
            _config = config;
        }

        // Backend-managed low-stock threshold surfaced to clients so the "Only N left!" badge uses a
        // single source of truth (the AAP-mandated Inventory:LowStockThreshold key, default 5) instead
        // of a hardcoded client constant. Clamped to a minimum of 1; a missing/unparseable key or a null
        // IConfiguration falls back to the default of 5.
        private int LowStockThreshold =>
            int.TryParse(_config?["Inventory:LowStockThreshold"], out var v) ? Math.Max(1, v) : 5;

        // Client-invoked: join the per-product group and return the current stock to the caller.
        // The Angular StockService relies on the returned int for the initial badge value.
        public async Task<int> SubscribeToProduct(int productId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, productId.ToString());

            var stock = await _inventoryService.GetAvailableStockAsync(productId);

            // Also push the initial value to the caller (optional; the returned int is authoritative).
            await Clients.Caller.SendAsync("StockChanged", productId, stock);

            // Additively surface the backend low-stock threshold to the caller so the client can render
            // the "Only N left!" badge from a single source of truth. This is a NEW, optional client
            // message: it does NOT change the SubscribeToProduct signature or the StockChanged contract,
            // and a client with no "LowStockThreshold" handler simply ignores it.
            await Clients.Caller.SendAsync("LowStockThreshold", LowStockThreshold);

            return stock;
        }
    }
}
