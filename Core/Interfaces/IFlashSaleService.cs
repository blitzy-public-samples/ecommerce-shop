using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Core.Entities;

namespace Core.Interfaces
{
    public interface IFlashSaleService
    {
        // Create/schedule a flash sale; returns the persisted entity.
        Task<FlashSale> ScheduleAsync(int productId, DateTimeOffset startAt, DateTimeOffset endAt,
            decimal salePrice, int stockAllocation);

        // Sales active now (now ∈ [StartAt, EndAt]) with computed live availability.
        Task<IReadOnlyList<ActiveFlashSale>> GetActiveSalesAsync();
    }

    // Plain result: an active sale plus its computed live availability
    // (StockAllocation − SUM(active non-expired reservations)). Mapped to FlashSaleDto in the API layer.
    public class ActiveFlashSale
    {
        public FlashSale Sale { get; set; }
        public int QuantityAvailable { get; set; }
    }
}
