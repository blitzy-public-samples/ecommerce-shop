using System;
using System.Text.Json;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using StackExchange.Redis;

namespace Infrastructure.Data
{
    public class BasketRepository : IBasketRepository
    {
        private readonly IDatabase _database;
        public BasketRepository(IConnectionMultiplexer redis)
        {
            _database = redis.GetDatabase();
        }

        public async Task<CustomerBasket> GetBasketAsync(string basketId)
        {
            var data = await _database.StringGetAsync(basketId);
            // Explicit (string) cast resolves the System.Text.Json Deserialize<T> overload ambiguity
            // (string vs ReadOnlySpan<byte>) for the implicitly-convertible RedisValue under .NET 10.
            // Preserves the pre-upgrade string-overload binding; behavior, key, and 30-day TTL unchanged.
            return data.IsNullOrEmpty ? null : JsonSerializer.Deserialize<CustomerBasket>((string)data);
        }

        //update or create a basket
        public async Task<CustomerBasket> UpdateBasketAsync(CustomerBasket basket)
        {
            var created = await _database.StringSetAsync(basket.Id, JsonSerializer.Serialize(basket), TimeSpan.FromDays(30));

            if (!created) return null;

            return await GetBasketAsync(basket.Id);
        }

        public async Task<bool> DeleteBasketAsync(string basketId)
        {
            return await _database.KeyDeleteAsync(basketId);
        }
    }
}