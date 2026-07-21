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

        // Bounded backoff bounds for the initial-subscribe retry loop (P4-10): start at 1s, cap at 30s.
        // A single startup Redis outage must not permanently disable real-time StockChanged broadcasts.
        private static readonly TimeSpan InitialSubscribeBackoff = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MaxSubscribeBackoff = TimeSpan.FromSeconds(30);

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

            try
            {
                // Establish the subscription with a bounded, cancellable retry loop (P4-10). A single
                // startup Redis outage must NOT permanently disable real-time broadcasts: if the initial
                // SubscribeAsync fails (Redis unavailable at startup), retry with capped backoff until it
                // succeeds or shutdown is requested. Once subscribed, StackExchange.Redis automatically
                // restores the subscription across later transient connection drops, so a single successful
                // subscribe is sufficient for the lifetime of the host. Subscribing is still fail-closed:
                // a Redis outage never crashes the host, and broadcasting remains best-effort — authoritative
                // stock still converges via the reconciliation republish.
                queue = await SubscribeWithRetryAsync(stoppingToken);

                if (queue != null)
                {
                    // Keep the hosted service alive until shutdown; the OnMessage callback runs on
                    // Redis-managed threads.
                    await Task.Delay(Timeout.Infinite, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown: the stopping token was signalled while waiting.
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

        /// <summary>
        /// Subscribes to the Redis <c>stock-updates</c> channel, retrying with capped exponential backoff
        /// until the subscription is established or <paramref name="stoppingToken"/> is signalled. This
        /// guarantees that a Redis outage present at startup does not permanently pause real-time
        /// <c>StockChanged</c> broadcasts (P4-10): once Redis becomes reachable, the loop subscribes and
        /// broadcasts resume without a host restart. Returns the live <see cref="ChannelMessageQueue"/>,
        /// or <c>null</c> if shutdown was requested before a subscription could be established.
        /// </summary>
        private async Task<ChannelMessageQueue> SubscribeWithRetryAsync(CancellationToken stoppingToken)
        {
            var backoff = InitialSubscribeBackoff;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var subscriber = _redis.GetSubscriber();
                    var queue = await subscriber.SubscribeAsync(StockUpdatesChannel);
                    queue.OnMessage(channelMessage => HandleMessageAsync(channelMessage.Message));
                    return queue;
                }
                catch (RedisConnectionException ex)
                {
                    _logger?.LogWarning(ex,
                        "StockBroadcastBackgroundService could not subscribe to the Redis '{Channel}' channel " +
                        "(Redis unavailable); retrying in {DelaySeconds}s. Real-time StockChanged broadcasts are " +
                        "paused until the subscription is established; authoritative stock still converges via " +
                        "the reconciliation republish.", StockUpdatesChannel, backoff.TotalSeconds);
                }
                catch (RedisTimeoutException ex)
                {
                    _logger?.LogWarning(ex,
                        "StockBroadcastBackgroundService timed out subscribing to the Redis '{Channel}' channel; " +
                        "retrying in {DelaySeconds}s until Redis recovers.", StockUpdatesChannel, backoff.TotalSeconds);
                }

                // Wait before the next attempt; a shutdown request cancels the delay and, via the
                // OperationCanceledException it raises, unwinds ExecuteAsync cleanly.
                await Task.Delay(backoff, stoppingToken);

                // capped exponential backoff
                var doubled = TimeSpan.FromTicks(backoff.Ticks * 2);
                backoff = doubled > MaxSubscribeBackoff ? MaxSubscribeBackoff : doubled;
            }

            return null;
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
