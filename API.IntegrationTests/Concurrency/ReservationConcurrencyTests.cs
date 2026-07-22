using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;                          // Barrier — genuine concurrent "starting gun"
using System.Threading.Tasks;
using API.IntegrationTests.Infrastructure;
using Core.Entities;
using Core.Interfaces;                            // IInventoryService — the authoritative stock writer under test
using FluentAssertions;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;                        // IConnectionMultiplexer — deterministic Redis key cleanup
using Xunit;

namespace API.IntegrationTests.Concurrency
{
    /// <summary>
    /// FLAGSHIP oversell integration test. Validates that <c>InventoryService.CreateReservationAsync</c>'s
    /// PostgreSQL <c>SELECT ... FOR UPDATE</c> row lock (inside an explicit EF Core transaction) prevents
    /// oversell under genuine concurrency: with <c>Products.StockQuantity = 10</c>, firing 50 simultaneous
    /// single-unit reservation attempts must grant EXACTLY 10 and deny 40, with zero oversell.
    /// Runs against the REAL Testcontainers PostgreSQL + Redis owned by this class's <see cref="ContainerFixture"/>.
    /// </summary>
    public class ReservationConcurrencyTests : IClassFixture<ContainerFixture>
    {
        private const int Attempts = 50;
        private const int InitialStock = 10;

        private readonly ContainerFixture _fixture;

        public ReservationConcurrencyTests(ContainerFixture fixture) => _fixture = fixture;

        [Fact]
        public async Task CreateReservation_50ConcurrentAttemptsAgainstStock10_ExactlyTenSucceedAndNoOversell()
        {
            // Arrange -----------------------------------------------------------------------------------
            var productId = await GetSeededProductIdAsync();
            var originalStock = await SetProductStockAsync(productId, InitialStock);
            await DeleteReservationsForProductAsync(productId);

            // Redis counters are NOT pre-seeded by the harness (Program.Main never runs under
            // WebApplicationFactory), so seed the hot-path counter from committed PostgreSQL stock. This
            // sequential call is also the first Redis operation, so it warms the lazily-connected
            // multiplexer singleton before the concurrent burst.
            using (var seedScope = _fixture.Factory.Services.CreateScope())
            {
                await seedScope.ServiceProvider.GetRequiredService<IInventoryService>().SeedStockCountersAsync();
            }

            var basketIds = Enumerable.Range(0, Attempts)
                .Select(_ => $"resv-concurrency-{Guid.NewGuid():N}")
                .ToList();

            // Guarantee the thread pool can host all 50 barrier participants at once so the "starting gun"
            // releases promptly and deterministically (default min-threads ramps too slowly for 50 blocked
            // threads). Restored in the finally block.
            ThreadPool.GetMinThreads(out var originalMinWorker, out var originalMinIocp);
            ThreadPool.SetMinThreads(Math.Max(originalMinWorker, Attempts + 8), originalMinIocp);

            try
            {
                // Act ---------------------------------------------------------------------------------------
                // Each attempt resolves its OWN DI scope (IInventoryService/StoreContext are Scoped and a
                // DbContext is not thread-safe), then rendezvouses at the Barrier so all 50 explicit
                // transactions contend on the same Products row lock at the same instant.
                using var startingGun = new Barrier(Attempts);
                var tasks = basketIds.Select(bid => Task.Run(async () =>
                {
                    using var scope = _fixture.Factory.Services.CreateScope();
                    var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
                    startingGun.SignalAndWait();                 // release all 50 at the same instant
                    return await inventory.CreateReservationAsync(bid, productId, 1);
                })).ToList();

                bool[] results = await Task.WhenAll(tasks);

                // Assert ------------------------------------------------------------------------------------
                results.Count(r => r).Should().Be(InitialStock,
                    "stock is 10 and each attempt reserves 1, so exactly ten succeed");
                results.Count(r => !r).Should().Be(Attempts - InitialStock,
                    "the other forty attempts must be denied once available stock reaches zero");

                // Available stock must have converged to zero and never gone negative.
                using (var readScope = _fixture.Factory.Services.CreateScope())
                {
                    var available = await readScope.ServiceProvider
                        .GetRequiredService<IInventoryService>()
                        .GetAvailableStockAsync(productId);
                    available.Should().Be(0, "all ten units are held by Active reservations");
                }

                // Authoritative DB check: the sum of Active reservation quantities is EXACTLY 10 — never more
                // (that would be oversell). Read through a fresh no-tracking scope so it reflects committed state.
                using (var dbScope = _fixture.Factory.Services.CreateScope())
                {
                    var ctx = dbScope.ServiceProvider.GetRequiredService<StoreContext>();
                    var reserved = await ctx.Reservations.AsNoTracking()
                        .Where(r => r.ProductId == productId && r.Status == ReservationStatus.Active)
                        .SumAsync(r => r.Quantity);

                    reserved.Should().BeLessOrEqualTo(InitialStock,
                        "ZERO OVERSELL invariant: committed Active reservations can never exceed available stock");
                    reserved.Should().Be(InitialStock,
                        "the row lock serializes the 50 transactions so exactly ten single-unit holds commit");
                }
            }
            finally
            {
                // Deterministic cleanup: restore the thread pool, purge this test's Reservations rows, reset
                // StockQuantity to its captured original, and delete every Redis key this test could have written.
                ThreadPool.SetMinThreads(originalMinWorker, originalMinIocp);
                await DeleteReservationsForProductAsync(productId);
                await RestoreProductStockAsync(productId, originalStock);
                await CleanupRedisKeysAsync(productId, basketIds);
            }
        }

        // -----------------------------------------------------------------------------------------------
        // Private helpers — keep this test independent and repeatable on the SHARED database/cache.
        // -----------------------------------------------------------------------------------------------

        private async Task<int> GetSeededProductIdAsync()
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();
            var product = await ctx.Products.AsNoTracking().OrderBy(p => p.Id).FirstAsync();
            return product.Id;
        }

        private async Task<int> SetProductStockAsync(int productId, int stock)
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();
            var product = await ctx.Products.FirstAsync(p => p.Id == productId);
            var original = product.StockQuantity;
            product.StockQuantity = stock;
            await ctx.SaveChangesAsync();
            return original;
        }

        private async Task RestoreProductStockAsync(int productId, int stock)
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();
            var product = await ctx.Products.FirstAsync(p => p.Id == productId);
            product.StockQuantity = stock;
            await ctx.SaveChangesAsync();
        }

        private async Task DeleteReservationsForProductAsync(int productId)
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<StoreContext>();
            var rows = await ctx.Reservations.Where(r => r.ProductId == productId).ToListAsync();
            if (rows.Count == 0)
            {
                return;
            }

            ctx.Reservations.RemoveRange(rows);
            await ctx.SaveChangesAsync();
        }

        private async Task CleanupRedisKeysAsync(int productId, IEnumerable<string> basketIds)
        {
            using var scope = _fixture.Factory.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

            await database.KeyDeleteAsync($"stock:product:{productId}");
            foreach (var basketId in basketIds)
            {
                await database.KeyDeleteAsync($"reservation:{basketId}:{productId}");
            }
        }
    }
}
