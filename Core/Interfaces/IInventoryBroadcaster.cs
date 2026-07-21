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
        Task BroadcastFlashSaleEndedAsync(int productId);
    }
}
