using System;
using System.Collections;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Infrastructure.Data
{
    public class UnitOfWork: IUnitOfWork
    {
        private readonly StoreContext _context;
        private Hashtable _repositories;

        // The currently-open explicit transaction, or null when none is active. Only relational
        // providers (PostgreSQL in production, SQLite in relational tests) open a real transaction;
        // on the EF Core InMemory provider the begin/commit/rollback calls are no-ops and this field
        // stays null. It is disposed defensively in Dispose() so an abandoned transaction (e.g. an
        // exception thrown before commit/rollback) never leaks a database connection.
        private IDbContextTransaction _transaction;

        public UnitOfWork(StoreContext context)
        {
            _context = context;
        }

        public void Dispose()
        {
            // Dispose any still-open transaction before the context so no connection is leaked if a
            // caller failed to commit or roll back. Disposing an already-committed/rolled-back
            // transaction is safe, and this is a no-op when no transaction was ever opened.
            _transaction?.Dispose();
            _transaction = null;
            _context.Dispose();
        }

        // ---------- explicit transaction lifecycle (AAP: order finalization atomicity) ----------
        // Opens an explicit database transaction on the shared scoped StoreContext so a subsequent
        // "SELECT ... FOR UPDATE" (taken by InventoryService.CommitReservationAsync over the SAME
        // context) enlists in it and its row lock is held until CommitTransactionAsync. Guarded by
        // IsRelational() so the EF Core InMemory provider (which does not support transactions) is a
        // safe no-op; re-entrancy is guarded so a second Begin without a matching Commit/Rollback does
        // not silently discard the first transaction.
        public async Task BeginTransactionAsync()
        {
            if (_transaction == null && _context.Database.IsRelational())
            {
                _transaction = await _context.Database.BeginTransactionAsync();
            }
        }

        public async Task CommitTransactionAsync()
        {
            if (_transaction != null)
            {
                await _transaction.CommitAsync();
                await _transaction.DisposeAsync();
                _transaction = null;
            }
        }

        public async Task RollbackTransactionAsync()
        {
            if (_transaction != null)
            {
                await _transaction.RollbackAsync();
                await _transaction.DisposeAsync();
                _transaction = null;
            }
        }

        public IGenericRepository<TEntity> Repository<TEntity>() where TEntity : BaseEntity
        {
            if (_repositories == null) _repositories = new Hashtable();

            var type = typeof(TEntity).Name;

            if (!_repositories.ContainsKey(type))
            {
                var repositoryType = typeof(GenericRepository<>);
                var repositoryInstance = Activator.CreateInstance(repositoryType.MakeGenericType(typeof(TEntity)), _context);
                _repositories.Add(type, repositoryInstance);
            }

            return (IGenericRepository<TEntity>) _repositories[type];
        }

        public async Task<int> Complete()
        {
            return await _context.SaveChangesAsync();
        }
    }
}