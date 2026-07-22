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

        // Create-or-extend an Active hold for (basketId, productId) so the hold reflects the desired
        // total quantity for that basket line, refreshing ExpiresAt (and the Redis hold-key TTL).
        // The grow-capacity check is evaluated against the pool the reservation is BOUND to
        // (general product stock, or the specific flash sale captured at creation) so a hold can never
        // grow beyond its bound pool. Returns true when the requested total was granted, false when the
        // hold could not be grown to the requested total (insufficient stock in the bound pool);
        // fail-closed on a Redis outage (the PostgreSQL row-locked check remains authoritative).
        Task<bool> ExtendReservationAsync(string basketId, int productId, int quantity);

        // Commit ALL Active reservations for a basket (Active -> Committed): permanently decrement the bound
        // pool (Products.StockQuantity or FlashSales.SaleStockQuantity). STAGES ONLY — it performs NO Redis
        // hold-key mutation, so that if the order-persistence flush (OrderService's _unitOfWork.Complete())
        // rolls back, the still-present Redis hold key stays consistent with the rolled-back (still-Active)
        // reservation. Called by OrderService.CreateOrderAsync INSIDE the same order-persistence transaction,
        // BEFORE the flush.
        Task CommitReservationAsync(string basketId);

        // Delete the Redis hold keys for a basket's Committed reservations (best-effort, fail-closed on a Redis
        // outage). Called by OrderService.CreateOrderAsync ONLY AFTER the order-persistence flush succeeds, so a
        // rolled-back order never orphans Redis by deleting a hold key whose reservation reverted to Active. This
        // is the deferred, post-commit counterpart to the hold-key deletion that CommitReservationAsync no longer
        // performs during staging.
        Task FinalizeCommittedHoldsAsync(string basketId);

        // Release/cancel an Active hold (Active -> Cancelled): INCR the Redis counter back and publish.
        Task ReleaseReservationAsync(string basketId, int productId);

        // Release/cancel EVERY Active hold for a basket (Active -> Cancelled): restore each product's Redis
        // counter, delete the per-product hold keys, and publish the corrected stock. Called when a basket is
        // deleted or cleared so no orphaned Active hold survives and available stock is restored immediately.
        Task ReleaseAllReservationsForBasketAsync(string basketId);

        // Available stock = committed pool stock minus the sum of Active reservations for the product.
        Task<int> GetAvailableStockAsync(int productId);

        // Seed the Redis stock counters (stock:product:{id}) from committed PostgreSQL stock.
        // Invoked at startup (API/Program.cs) and re-invoked after each reconciliation pass.
        Task SeedStockCountersAsync();

        // Reclaim Active reservations whose hold window has elapsed (ExpiresAt < now): transition each
        // Active -> Expired, return the held quantity to the Redis counter (INCR), and publish the corrected
        // stock, then persist the transitions. Also opportunistically deletes any residual Redis hold keys
        // left behind for non-Active reservations still inside their TTL window (a hold-key deletion that a
        // prior Redis outage may have skipped), converging Redis with committed PostgreSQL state.
        //
        // This is the SOLE-WRITER seam for reservation expiry: PostgreSQL has no native row TTL, so expiry is
        // enforced exclusively here. The StockReconciliationService background loop ORCHESTRATES by invoking
        // this method (it no longer mutates the Reservations table or the Redis stock keys itself), preserving
        // the invariant that InventoryService is the only component that writes reservations and stock keys.
        // Best-effort / fail-closed on a Redis outage: the DB reclaim (the authority) always proceeds.
        Task ReclaimExpiredReservationsAsync();
    }
}