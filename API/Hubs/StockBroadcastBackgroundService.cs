using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace API.Hubs
{
    public class StockBroadcastBackgroundService : BackgroundService
    {
        private const string StockUpdatesChannel = "stock-updates";

        private readonly IConnectionMultiplexer _redis;
        private readonly IHubContext<StockHub> _hub;
        private readonly ILogger<StockBroadcastBackgroundService> _logger;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // ILogger is an OPTIONAL trailing dependency (defaults to null) so this bridge can be
        // constructed without a logger (e.g., in a focused test) while the DI container injects the
        // real logger in production. All logging is via _logger?. so a null logger is a safe no-op.
        public StockBroadcastBackgroundService(IConnectionMultiplexer redis, IHubContext<StockHub> hub,
            ILogger<StockBroadcastBackgroundService> logger = null)
        {
            _redis = redis;
            _hub = hub;
            _logger = logger;
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
            catch (RedisConnectionException ex)
            {
                _logger?.LogWarning(ex,
                    "StockBroadcastBackgroundService could not subscribe to the Redis '{Channel}' channel " +
                    "(Redis unavailable); real-time StockChanged broadcasts are paused. Authoritative stock " +
                    "still converges via the reconciliation republish once Redis recovers.", StockUpdatesChannel);
            }
            catch (RedisTimeoutException ex)
            {
                _logger?.LogWarning(ex,
                    "StockBroadcastBackgroundService timed out subscribing to the Redis '{Channel}' channel; " +
                    "real-time StockChanged broadcasts are paused until Redis recovers.", StockUpdatesChannel);
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
            catch (JsonException ex)
            {
                // malformed message: ignore, do not crash the subscription. Log (without the raw
                // payload, which comes from the channel and is untrusted) so recurring corruption is visible.
                _logger?.LogWarning(ex,
                    "StockBroadcastBackgroundService received a malformed message on the Redis '{Channel}' " +
                    "channel and ignored it.", StockUpdatesChannel);
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
            catch (Exception ex)
            {
                // best-effort broadcast; swallow so a single failure does not tear down the subscription
                _logger?.LogWarning(ex,
                    "StockBroadcastBackgroundService failed to broadcast StockChanged for product " +
                    "{ProductId} (currentStock {CurrentStock}); the subscription remains active.",
                    message.ProductId, message.CurrentStock);
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
