using System;
using System.Linq;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services
{
    // Infrastructure-layer implementation of IFlashSaleService. This service resolves which
    // time-windowed flash-sale stock pool (if any) is currently active for a product and advances
    // flash-sale statuses over time. It is a foundational service consumed by InventoryService
    // (to bind a reservation to the correct pool at creation time) and by StockReconciliationService
    // (to drive Scheduled -> Active -> Ended transitions on each reconciliation pass).
    //
    // Architecture: this file lives in the Infrastructure layer, which references ONLY Core. It never
    // references API-layer types (SignalR / hubs) and performs no Redis work — real-time broadcasting
    // is decoupled through the Redis "stock-updates" Pub/Sub bridge owned by InventoryService and the
    // API-layer StockBroadcastBackgroundService, not by this service.
    //
    // Persistence: StoreContext is injected directly (the same pattern used by UnitOfWork and
    // GenericRepository, which each hold a StoreContext). All queries are pure LINQ + SaveChangesAsync
    // with no raw SQL and no explicit transaction, so the service behaves identically on PostgreSQL
    // (Npgsql), SQLite, and the EF Core in-memory provider used by the test suite.
    public class FlashSaleService : IFlashSaleService
    {
        private readonly StoreContext _context;

        public FlashSaleService(StoreContext context)
        {
            _context = context;
        }

        // Returns the flash sale for the product that is Active AND whose window satisfies
        // StartsAt <= now < EndsAt (start inclusive, end exclusive). Returns null when no active sale
        // applies, in which case InventoryService binds the reservation to the general product pool.
        // The window predicate is authoritative and matches ActiveFlashSalesSpecification.
        public async Task<FlashSale> GetActiveFlashSaleForProductAsync(int productId)
        {
            var now = DateTimeOffset.UtcNow;

            return await _context.Set<FlashSale>()
                .Where(f => f.ProductId == productId
                            && f.Status == FlashSaleStatus.Active
                            && f.StartsAt <= now
                            && now < f.EndsAt)
                .FirstOrDefaultAsync();
        }

        // Advances flash-sale statuses purely by time (Scheduled -> Active -> Ended). Invoked once per
        // pass by StockReconciliationService; this is the ONLY place flash-sale statuses transition.
        // A sale whose window has fully elapsed goes straight to Ended (this also covers a Scheduled
        // sale that was never observed while Active); otherwise a Scheduled sale whose start has been
        // reached becomes Active.
        public async Task AdvanceFlashSaleStatusesAsync()
        {
            var now = DateTimeOffset.UtcNow;

            var sales = await _context.Set<FlashSale>()
                .Where(f => f.Status != FlashSaleStatus.Ended)
                .ToListAsync();

            foreach (var sale in sales)
            {
                if (now >= sale.EndsAt)
                {
                    sale.Status = FlashSaleStatus.Ended;
                }
                else if (sale.Status == FlashSaleStatus.Scheduled && now >= sale.StartsAt)
                {
                    sale.Status = FlashSaleStatus.Active;
                }
            }

            await _context.SaveChangesAsync();
        }
    }
}
