using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using API.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace API.Helpers
{
    // Flash-Sale feature: in-memory, single-instance sliding-window rate limiter for the reserve endpoint.
    // Applied as [SessionRateLimitFilter] on InventoryController.Reserve. Limits each session to
    // 10 requests per 60s; the 11th request within the window is short-circuited with HTTP 429 (the
    // controller action is never invoked). net5.0 has no built-in rate limiter, and the design forbids a
    // Redis/distributed backplane (AAP §0.5.2), so state is a process-static dictionary that matches the
    // single-instance hub design. No DI is used (unlike CachedAttribute).
    public class SessionRateLimitFilter : Attribute, IAsyncActionFilter
    {
        private const int MaxRequests = 10;
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

        // Keyed by session id (the basket UUID); value = timestamps of hits currently inside the window.
        // Static so counters persist across requests on the single running instance.
        private static readonly ConcurrentDictionary<string, List<DateTimeOffset>> Hits =
            new ConcurrentDictionary<string, List<DateTimeOffset>>();

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var key = ResolveSessionKey(context);

            if (!IsAllowed(key))
            {
                // Rate limit exceeded -> HTTP 429 Too Many Requests. Short-circuit: do NOT call next().
                context.Result = new ContentResult
                {
                    StatusCode = 429, // StatusCodes.Status429TooManyRequests
                    ContentType = "application/json",
                    Content = "{\"error\":\"RATE_LIMIT_EXCEEDED\"}"
                };
                return;
            }

            await next(); // within limit -> proceed to the controller action
        }

        // Resolve the caller key: basket UUID from ReserveInventoryDto.SessionId, else X-Session-Id header,
        // else the remote IP (last-resort so the filter never crashes on a missing session).
        private static string ResolveSessionKey(ActionExecutingContext context)
        {
            var dto = context.ActionArguments.Values.OfType<ReserveInventoryDto>().FirstOrDefault();
            if (dto != null && !string.IsNullOrWhiteSpace(dto.SessionId))
            {
                return dto.SessionId;
            }

            var header = context.HttpContext.Request.Headers["X-Session-Id"].ToString();
            if (!string.IsNullOrWhiteSpace(header))
            {
                return header;
            }

            return context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }

        // Sliding window: record this hit, drop hits older than the window, allow while count <= MaxRequests.
        private static bool IsAllowed(string key)
        {
            var now = DateTimeOffset.UtcNow;
            var cutoff = now - Window;

            var timestamps = Hits.GetOrAdd(key, _ => new List<DateTimeOffset>());

            // Lock the per-key list so simultaneous reserve requests for the same session are counted
            // correctly (the concurrency/load test fires up to 500 requests at once).
            lock (timestamps)
            {
                timestamps.Add(now);
                timestamps.RemoveAll(t => t < cutoff);
                return timestamps.Count <= MaxRequests;
            }
        }
    }
}
