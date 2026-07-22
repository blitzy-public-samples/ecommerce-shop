using System.Threading.Tasks;
using Core.Entities;

namespace Core.Interfaces
{
    // Hub-agnostic broadcast contract (implemented by API/Hubs/InventoryBroadcaster over IHubContext<InventoryHub>).
    // Keeps Infrastructure services free of any SignalR dependency and unit-testable.
    public interface IInventoryBroadcaster
    {
        Task BroadcastInventoryUpdatedAsync(int productId, int quantityAvailable);
        Task BroadcastFlashSaleStartedAsync(FlashSale sale, int quantityAvailable);

        // Flash-Sale feature (review finding M9): FlashSaleEnded carries BOTH the productId (for group routing)
        // AND the saleId, so a client clears ONLY the sale that actually ended — never a newer sale that a
        // racing FlashSaleStarted just delivered for the same product within the same tick.
        Task BroadcastFlashSaleEndedAsync(int productId, int saleId);
    }
}
