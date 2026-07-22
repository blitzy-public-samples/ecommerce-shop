using System;
using System.Threading.Tasks;
using Core.Entities;

namespace Core.Interfaces
{
    public interface IUnitOfWork : IDisposable
    {
        IGenericRepository<TEntity> Repository<TEntity>() where TEntity : BaseEntity;
        Task<int> Complete();

        // Explicit transaction lifecycle for multi-statement atomicity. The Real-Time Inventory &
        // Flash-Sale System requires order finalization to run under an explicit transaction so the
        // PostgreSQL "SELECT ... FOR UPDATE" row lock taken while committing a basket's reservations
        // (see InventoryService.CommitReservationAsync) is held until the order rows AND the staged
        // reservation/stock changes are flushed together. Concurrent order finalizations therefore
        // serialize on the Products row and cannot silently overwrite each other's stock decrement.
        //
        // The methods are deliberately declared with the non-generic Task and expose NO EF Core types,
        // so Core remains free of persistence dependencies. Implementations must be safe to call on a
        // non-relational provider (e.g. EF Core InMemory in unit tests), where they become no-ops.
        Task BeginTransactionAsync();
        Task CommitTransactionAsync();
        Task RollbackTransactionAsync();
    }
}