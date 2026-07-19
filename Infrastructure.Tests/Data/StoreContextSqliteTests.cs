using System.Linq;
using System.Threading.Tasks;
using Core.Entities.OrderAggregate;
using FluentAssertions;
using Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Infrastructure.Tests.Data
{
    /// <summary>
    /// Unit tests that exercise the SQLite-backed <c>StoreContext</c> path via
    /// <see cref="TestStoreContextFactory.CreateSqliteInMemoryContext"/> and
    /// <see cref="TestStoreContextFactory.SeedDeliveryMethodsAsync"/>.
    ///
    /// The bulk of the data-access suite deliberately uses the EF Core InMemory provider
    /// (AAP 0.4.4/0.5.1 permit "EF Core InMemory <b>or</b> SQLite"), which never touches the
    /// provider-specific value conversions declared in <c>StoreContext.OnModelCreating</c>.
    /// These tests close that gap by running against a genuine SQLite in-memory database so the
    /// production SQLite branch — every <c>decimal</c> mapped through <c>HasConversion&lt;double&gt;()</c>
    /// and every <c>DateTimeOffset</c> through <c>DateTimeOffsetToBinaryConverter</c> — is actually
    /// executed at runtime, and so the previously-unexercised factory helpers are covered.
    ///
    /// <see cref="DeliveryMethod"/> is chosen because it is relationally self-contained (no foreign
    /// keys) yet carries a <c>decimal</c> Price, making it valid under SQLite while still driving the
    /// decimal-to-double conversion. Each test creates its own SQLite ":memory:" connection, so the
    /// tests remain isolated and safe to run in parallel.
    ///
    /// Conventions (AAP 0.10.2): MethodName_StateUnderTest_ExpectedBehavior naming,
    /// Arrange-Act-Assert structure, FluentAssertions, and one logical behavior per test.
    /// </summary>
    public class StoreContextSqliteTests
    {
        [Fact]
        public void CreateSqliteInMemoryContext_WhenProviderInspected_UsesSqliteProvider()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateSqliteInMemoryContext();

            // Act
            var providerName = context.Database.ProviderName;

            // Assert — confirms the factory really wires up the SQLite provider (and therefore the
            // provider-specific conversions in StoreContext.OnModelCreating are in effect).
            providerName.Should().Be("Microsoft.EntityFrameworkCore.Sqlite");
        }

        [Fact]
        public async Task SeedDeliveryMethodsAsync_OnSqliteContext_PersistsRowsWithUniqueNonZeroIds()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateSqliteInMemoryContext();

            // Act — the seeder issues real SQL INSERTs against SQLite; generated keys flow back.
            var seeded = await TestStoreContextFactory.SeedDeliveryMethodsAsync(context, 3);

            // Re-read with no tracking so the rows are materialized fresh from the database
            // rather than served from the change tracker.
            var persisted = await context.DeliveryMethods.AsNoTracking().ToListAsync();

            // Assert
            seeded.Should().HaveCount(3);
            persisted.Should().HaveCount(3);
            persisted.Select(d => d.Id).Should().OnlyHaveUniqueItems();
            persisted.Should().OnlyContain(d => d.Id > 0);
        }

        [Fact]
        public async Task SeedDeliveryMethodsAsync_OnSqliteContext_RoundTripsDecimalPriceViaDoubleConversion()
        {
            // Arrange
            using var context = TestStoreContextFactory.CreateSqliteInMemoryContext();

            // Act — SeedDeliveryMethodsAsync assigns Price = 5m * i (5, 10, 15). Under SQLite these
            // decimals are stored as doubles (StoreContext.OnModelCreating HasConversion<double>) and
            // converted back to decimal on read. AsNoTracking forces that read-side conversion.
            await TestStoreContextFactory.SeedDeliveryMethodsAsync(context, 3);
            var prices = await context.DeliveryMethods
                .AsNoTracking()
                .OrderBy(d => d.Price)
                .Select(d => d.Price)
                .ToListAsync();

            // Assert — assert with a tolerance rather than exact equality, exactly as the factory's
            // decimal->double caveat prescribes for SQLite-backed tests.
            prices.Should().HaveCount(3);
            prices[0].Should().BeApproximately(5m, 0.001m);
            prices[1].Should().BeApproximately(10m, 0.001m);
            prices[2].Should().BeApproximately(15m, 0.001m);
        }
    }
}
