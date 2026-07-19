using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Core.Entities;
using Core.Entities.OrderAggregate;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Helpers
{
    /// <summary>
    /// Builds fresh, isolated <see cref="StoreContext"/> instances for data-access unit
    /// tests. Every factory method returns a brand-new context (and, where applicable,
    /// its own backing store) so tests never share mutable state. The class is stateless:
    /// it caches no contexts, options, or connections, which keeps each test independent
    /// and safe to run in parallel.
    /// </summary>
    public static class TestStoreContextFactory
    {
        /// <summary>
        /// Creates a fresh StoreContext backed by the EF Core InMemory provider.
        /// A unique GUID database name (the default) isolates each call; pass an explicit
        /// <paramref name="dbName"/> when a single test must share one store across two
        /// context instances (e.g. write with one context, read back with another).
        /// InMemory skips StoreContext's SQLite decimal->double conversion, so exact
        /// decimal assertions are safe.
        /// </summary>
        public static StoreContext CreateInMemoryContext(string dbName = null)
        {
            var options = new DbContextOptionsBuilder<StoreContext>()
                .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
                .Options;

            return new StoreContext(options);
        }

        /// <summary>
        /// Creates a fresh StoreContext backed by a SQLite in-memory database, for the rare
        /// test that needs genuine relational behavior (FK enforcement, real SQL translation).
        /// Prefer <see cref="CreateInMemoryContext"/> for ordinary tests.
        /// </summary>
        public static StoreContext CreateSqliteInMemoryContext()
        {
            // The connection is opened and intentionally kept OPEN for the lifetime of the
            // returned context: a SQLite ":memory:" database exists only while its connection
            // is open, so closing it would drop the schema and all data. EF Core treats an
            // externally-supplied open connection as not-owned and will not close it on
            // context disposal; it is reclaimed when the connection is finalized after the
            // test releases the context.
            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();

            var options = new DbContextOptionsBuilder<StoreContext>()
                .UseSqlite(connection)
                .Options;

            var context = new StoreContext(options);

            // Materialize the schema for the freshly-opened in-memory database.
            context.Database.EnsureCreated();

            // CAVEAT: under SQLite, StoreContext.OnModelCreating converts every decimal to a
            // double, so monetary values lose exact precision. Tests using this factory must
            // avoid exact-decimal equality and assert with a tolerance
            // (e.g. FluentAssertions: value.Should().BeApproximately(expected, 0.001m)).
            return context;
        }

        /// <summary>
        /// Seeds <paramref name="count"/> valid Products (sharing one ProductBrand and one
        /// ProductType so foreign keys resolve under any provider) and saves them.
        /// Returns the seeded products so tests can read their generated ids.
        /// </summary>
        public static async Task<List<Product>> SeedProductsAsync(StoreContext context, int count = 3)
        {
            var brand = new ProductBrand { Name = "Test Brand" };
            var type = new ProductType { Name = "Test Type" };

            var products = new List<Product>();
            for (var i = 1; i <= count; i++)
            {
                products.Add(new Product
                {
                    Name = $"Test Product {i}",
                    Description = $"Test description {i}",
                    PictureUrl = $"images/products/test-{i}.png",
                    Price = 10m * i,
                    ProductBrand = brand,
                    ProductType = type
                });
            }

            context.Products.AddRange(products);
            await context.SaveChangesAsync();
            return products;
        }

        /// <summary>
        /// Seeds <paramref name="count"/> valid DeliveryMethods and saves them.
        /// Returns the seeded delivery methods so tests can read their generated ids.
        /// </summary>
        public static async Task<List<DeliveryMethod>> SeedDeliveryMethodsAsync(StoreContext context, int count = 3)
        {
            var methods = new List<DeliveryMethod>();
            for (var i = 1; i <= count; i++)
            {
                methods.Add(new DeliveryMethod
                {
                    ShortName = $"Method {i}",
                    DeliveryTime = $"{i}-{i + 2} Days",
                    Description = $"Delivery description {i}",
                    Price = 5m * i
                });
            }

            context.DeliveryMethods.AddRange(methods);
            await context.SaveChangesAsync();
            return methods;
        }
    }
}
