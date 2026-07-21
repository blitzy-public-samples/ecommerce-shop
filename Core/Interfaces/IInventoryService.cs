using System.Threading.Tasks;

namespace Core.Interfaces
{
    public interface IInventoryService
    {
        // Create an Active reservation for (basketId, productId, quantity) at basket add/update.
        // Opens an explicit transaction, resolves the active pool (general vs flash-sale) via IFlashSaleService,
        // takes SELECT ... FOR UPDATE on the Products row, verifies available stock, inserts the reservation,
        // sets the Redis hold key with TTL, atomically DECRs stock:product:{productId}, publishes to stock-updates.
        // Returns true when the hold was granted, false when insufficient stock (fail-closed on Redis outage:
        // rely on the PostgreSQL row-locked check and never assume stock on a cache miss).
        Task<bool> CreateReservationAsync(string basketId, int productId, int quantity);

        // Extend / refresh an existing Active hold's ExpiresAt (and Redis hold-key TTL) on repeated basket updates.
        Task ExtendReservationAsync(string basketId, int productId, int quantity);

        // Commit ALL Active reservations for a basket (Active -> Committed): permanently decrement the bound
        // pool (Products.StockQuantity or FlashSales.SaleStockQuantity) and delete the Redis hold key.
        // Called by OrderService.CreateOrderAsync INSIDE the same order-persistence transaction.
        Task CommitReservationAsync(string basketId);

        // Release/cancel an Active hold (Active -> Cancelled): INCR the Redis counter back and publish.
        Task ReleaseReservationAsync(string basketId, int productId);

        // Available stock = committed pool stock minus the sum of Active reservations for the product.
        Task<int> GetAvailableStockAsync(int productId);

        // Seed the Redis stock counters (stock:product:{id}) from committed PostgreSQL stock.
        // Invoked at startup (API/Program.cs) and re-invoked after each reconciliation pass.
        Task SeedStockCountersAsync();
    }
}