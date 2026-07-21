using System.Threading.Tasks;
using Core.Entities;

namespace Core.Interfaces
{
    public interface IFlashSaleService
    {
        // Returns the flash sale whose Status = Active and window satisfies StartsAt <= now < EndsAt
        // for the given product, or null when no active sale applies (reservation then binds to general pool).
        Task<FlashSale> GetActiveFlashSaleForProductAsync(int productId);

        // Advance flash-sale statuses by time (Scheduled -> Active -> Ended); driven by the reconciliation service.
        Task AdvanceFlashSaleStatusesAsync();
    }
}
