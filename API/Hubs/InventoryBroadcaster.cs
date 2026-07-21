using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace API.Hubs
{
    // Flash-Sale feature: adapter implementing the Core-defined IInventoryBroadcaster over
    // IHubContext<InventoryHub>. This is the ONLY class in the feature that depends on SignalR, so
    // Infrastructure services (FlashSaleService, InventoryReservationService, ReservationExpirySweepService)
    // stay SignalR-free and unit-testable against the Core abstraction. Registered as
    // AddScoped<IInventoryBroadcaster, InventoryBroadcaster>() in API/Extension/ApplicationServicesExtensions.cs;
    // IHubContext<InventoryHub> is supplied automatically by AddSignalR().
    public class InventoryBroadcaster : IInventoryBroadcaster
    {
        private readonly IHubContext<InventoryHub> _hubContext;

        public InventoryBroadcaster(IHubContext<InventoryHub> hubContext)
        {
            _hubContext = hubContext;
        }

        // Event names + single-object camelCase payloads MUST match the Angular inventory-hub.service.ts
        // handlers and models (IInventoryUpdate { productId, quantityAvailable }). Sent to the per-product
        // group the client joined via JoinProductGroup.
        public Task BroadcastInventoryUpdatedAsync(int productId, int quantityAvailable) =>
            _hubContext.Clients.Group(InventoryHub.ProductGroup(productId))
                .SendAsync("InventoryUpdated", new { productId, quantityAvailable });

        // Payload projected to match the client IFlashSale { id, productId, startAt, endAt, salePrice,
        // stockAllocation, quantityAvailable }. IMPORTANT: sale.Version (the internal optimistic-concurrency
        // token) is deliberately NOT projected. No API/Dtos type is referenced — projected inline.
        public Task BroadcastFlashSaleStartedAsync(FlashSale sale, int quantityAvailable) =>
            _hubContext.Clients.Group(InventoryHub.ProductGroup(sale.ProductId))
                .SendAsync("FlashSaleStarted", new
                {
                    id = sale.Id,
                    productId = sale.ProductId,
                    startAt = sale.StartAt,
                    endAt = sale.EndAt,
                    salePrice = sale.SalePrice,
                    stockAllocation = sale.StockAllocation,
                    quantityAvailable
                });

        public Task BroadcastFlashSaleEndedAsync(int productId) =>
            _hubContext.Clients.Group(InventoryHub.ProductGroup(productId))
                .SendAsync("FlashSaleEnded", new { productId });
    }
}
