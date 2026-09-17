using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using API.Hubs;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace API.Tests.Hubs
{
    /// <summary>
    /// Unit tests for <see cref="StockBroadcastBackgroundService"/> — specifically its startup
    /// subscription resilience (finding <b>P4-10</b>).
    /// <para>
    /// The service is exercised in complete isolation with Moq test doubles for
    /// <see cref="IConnectionMultiplexer"/>/<see cref="ISubscriber"/> and <see cref="IHubContext{StockHub}"/>,
    /// so no real Redis, SignalR host, or Docker container is required — in keeping with the AAP's
    /// "extend the existing test projects, no new test infrastructure" convention (§0.6.2). The
    /// happy-path subscribe-and-broadcast flow is already covered end-to-end by the existing
    /// <c>API.IntegrationTests/Inventory/StockPropagationTests</c> against real Testcontainers Redis;
    /// these unit tests cover the failure/recovery behaviour that integration cannot easily induce.
    /// </para>
    /// <para>
    /// <b>Regression guarded (P4-10).</b> Previously a single startup <c>SubscribeAsync</c> failure
    /// (Redis unavailable at startup) was logged and the service then parked on
    /// <c>Task.Delay(Timeout.Infinite)</c> forever, so a recovered Redis never resumed real-time
    /// <c>StockChanged</c> broadcasts until a host restart. The service now retries the initial
    /// subscription with capped backoff until it succeeds or shutdown is requested.
    /// </para>
    /// </summary>
    public class StockBroadcastBackgroundServiceTests
    {
        private static Mock<IConnectionMultiplexer> BuildMultiplexerThatAlwaysFailsToSubscribe(
            out Func<int> attemptCount)
        {
            var attempts = 0;
            attemptCount = () => Volatile.Read(ref attempts);

            var subscriber = new Mock<ISubscriber>(MockBehavior.Loose);
            // Every subscribe attempt increments the counter and returns a faulted task, simulating a
            // persistent Redis outage. Returning a faulted Task (rather than throwing synchronously)
            // mirrors how StackExchange.Redis surfaces an unreachable server from an async API.
            subscriber
                .Setup(s => s.SubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<CommandFlags>()))
                .Returns(() =>
                {
                    Interlocked.Increment(ref attempts);
                    return Task.FromException<ChannelMessageQueue>(
                        new RedisConnectionException(ConnectionFailureType.UnableToConnect,
                            "test-induced Redis outage"));
                });

            var mux = new Mock<IConnectionMultiplexer>(MockBehavior.Loose);
            mux.Setup(m => m.GetSubscriber(It.IsAny<object>())).Returns(subscriber.Object);
            return mux;
        }

        [Fact]
        public async Task ExecuteAsync_InitialSubscribeFailsRepeatedly_RetriesInsteadOfParkingForever()
        {
            // Arrange: a multiplexer whose SubscribeAsync always fails (Redis down at and after startup).
            var mux = BuildMultiplexerThatAlwaysFailsToSubscribe(out var attemptCount);
            var hub = Mock.Of<IHubContext<StockHub>>();
            var service = new StockBroadcastBackgroundService(mux.Object, hub);

            // Act: start the hosted service; ExecuteAsync runs in the background.
            await service.StartAsync(CancellationToken.None);

            try
            {
                // The first attempt happens immediately; the retry loop waits one bounded backoff
                // interval (1s) before the second attempt. Poll until we have observed at least two
                // attempts, which is only possible if the service RETRIES rather than parking forever.
                var stopwatch = Stopwatch.StartNew();
                while (attemptCount() < 2 && stopwatch.Elapsed < TimeSpan.FromSeconds(8))
                {
                    await Task.Delay(50);
                }

                // Assert: the service made more than one subscribe attempt (P4-10 regression guard).
                attemptCount().Should().BeGreaterOrEqualTo(2,
                    "the broadcaster must retry the initial Redis subscription after a startup outage " +
                    "instead of parking forever, so real-time broadcasts resume once Redis recovers (P4-10)");
            }
            finally
            {
                // Clean shutdown regardless of assertion outcome.
                await service.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task ExecuteAsync_RedisUnavailable_DoesNotCrashHostAndStopsCleanly()
        {
            // Arrange: Redis is unavailable, so the service is stuck in its subscribe-retry loop.
            var mux = BuildMultiplexerThatAlwaysFailsToSubscribe(out _);
            var hub = Mock.Of<IHubContext<StockHub>>();
            var service = new StockBroadcastBackgroundService(mux.Object, hub);

            // Act: start while Redis is down, then request shutdown while still retrying.
            await service.StartAsync(CancellationToken.None);

            // Assert (fail-closed): a Redis outage never faults the hosted service, and shutdown
            // completes cleanly (the cancellable retry delay unwinds ExecuteAsync without throwing).
            Func<Task> stop = () => service.StopAsync(CancellationToken.None);
            await stop.Should().NotThrowAsync(
                "a Redis outage must not crash the host and shutdown must complete cleanly (fail-closed)");
        }
    }
}
