using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace API.Hubs
{
    public class StockBroadcastBackgroundService : BackgroundService
    {
        private const string StockUpdatesChannel = "stock-updates";

        private readonly IConnectionMultiplexer _redis;
        private readonly IHubContext<StockHub> _hub;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public StockBroadcastBackgroundService(IConnectionMultiplexer redis, IHubContext<StockHub> hub)
        {
            _redis = redis;
            _hub = hub;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            ChannelMessageQueue queue = null;

            // Subscribe (fail-closed): a Redis outage must not crash the host. Broadcasting is best-effort;
            // authoritative stock still converges via the reconciliation republish.
            try
            {
                var subscriber = _redis.GetSubscriber();
                queue = await subscriber.SubscribeAsync(StockUpdatesChannel);
                queue.OnMessage(channelMessage => HandleMessageAsync(channelMessage.Message));
            }
            catch (RedisConnectionException)
            {
            }
            catch (RedisTimeoutException)
            {
            }

            // Keep the hosted service alive until shutdown; the OnMessage callback runs on Redis-managed threads.
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (TaskCanceledException)
            {
            }
            finally
            {
                if (queue != null)
                {
                    try
                    {
                        await queue.UnsubscribeAsync();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private async Task HandleMessageAsync(RedisValue value)
        {
            if (value.IsNullOrEmpty)
            {
                return;
            }

            StockUpdateMessage message;
            try
            {
                string json = value;
                message = JsonSerializer.Deserialize<StockUpdateMessage>(json, _jsonOptions);
            }
            catch (JsonException)
            {
                // malformed message: ignore, do not crash the subscription
                return;
            }

            if (message == null)
            {
                return;
            }

            try
            {
                await _hub.Clients.Group(message.ProductId.ToString())
                    .SendAsync("StockChanged", message.ProductId, message.CurrentStock);
            }
            catch (Exception)
            {
                // best-effort broadcast; swallow so a single failure does not tear down the subscription
            }
        }

        private class StockUpdateMessage
        {
            public int ProductId { get; set; }
            public long CurrentStock { get; set; }
            public int? FlashSaleId { get; set; }
        }
    }
}
