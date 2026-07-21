using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using API.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Caching.Memory;

namespace API.Helpers
{
    // Flash-Sale feature: in-memory, single-instance sliding-window rate limiter for the reserve endpoint.
    // Applied as [SessionRateLimitFilter] on InventoryController.Reserve. Enforces the binding user example
    // "10 req/min/session, returning HTTP 429 when exceeded" (AAP §0.2.3, §0.6). net5.0 has no built-in rate
    // limiter (Microsoft.AspNetCore.RateLimiting is .NET 7+), and the design forbids a Redis/distributed
    // backplane (AAP §0.5.2), so all state is process-local and matches the single-instance hub. No DI /
    // service resolution is used.
    //
    // Review hardening (findings M05/M06/M07):
    //   M07 — Identity is ONLY the canonical, validated basket UUID from the bound ReserveInventoryDto.
    //         The previous X-Session-Id header and RemoteIp fallbacks are removed: header rotation, case
    //         variation, or malformed values created alternate identities that bypassed the per-basket limit,
    //         and a shared IP fallback let NAT'd users share one bucket. A missing/invalid session identity is
    //         now rejected (HTTP 400), never silently substituted with a header/IP identity.
    //   M06 — Only ACCEPTED requests are recorded. The old code appended the timestamp BEFORE deciding, so
    //         denied requests kept extending the window (a caller spamming past the limit stayed locked out).
    //         We now prune expired hits, reject WITHOUT recording when the window already holds MaxRequests
    //         accepted hits, and append a timestamp ONLY when the request is admitted — i.e. exactly
    //         MaxRequests accepted per rolling 60s, with clean recovery once older hits age out.
    //   M05 — State is a BOUNDED, self-expiring MemoryCache (SizeLimit + per-entry sliding expiration) instead
    //         of an unbounded process-static dictionary. Idle sessions expire and are reclaimed, and the total
    //         number of tracked sessions is capped, so an attacker rotating keys cannot drive unbounded memory
    //         growth. Per-session state is capped at MaxRequests timestamps, so pruning stays O(MaxRequests).
    //         Keys are canonical 36-char UUIDs, so key length is inherently bounded (no attacker-chosen keys).
    public class SessionRateLimitFilter : Attribute, IAsyncActionFilter
    {
        private const int MaxRequests = 10;
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

        // Upper bound on the number of distinct sessions tracked concurrently. Combined with per-entry sliding
        // expiration, this caps worst-case memory (~ MaxTrackedSessions * (36-char key + <= MaxRequests
        // timestamps)) regardless of how many distinct keys a caller presents; MemoryCache compacts under size
        // pressure so rotating keys can never grow memory without bound (M05).
        private const int MaxTrackedSessions = 100_000;

        // Bounded, self-expiring store keyed by the canonical basket UUID. Static so counters persist across
        // requests on the single running instance; never resolved from DI (AAP §0.5.2: no distributed backplane).
        private static readonly MemoryCache Buckets = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = MaxTrackedSessions
        });

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (!TryResolveSessionKey(context, out var key))
            {
                // M07: reject a missing/invalid session identity instead of substituting a header/IP identity.
                // With [ApiController], an invalid ReserveInventoryDto is already rejected with HTTP 400 before
                // this filter runs; this guard is the defensive last line and never silently admits an
                // unidentified caller into a shared/unbounded bucket.
                context.Result = new ContentResult
                {
                    StatusCode = 400, // StatusCodes.Status400BadRequest
                    ContentType = "application/json",
                    Content = "{\"error\":\"INVALID_SESSION\"}"
                };
                return;
            }

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

        // M07: the ONLY accepted identity is the canonical basket UUID carried by the bound ReserveInventoryDto.
        // The value is normalized to the lowercase canonical "D" form so one basket maps to exactly one key
        // (this also bounds key length to 36 chars for M05). Returns false when no valid session identity is
        // present so the caller is rejected rather than silently bucketed by header/IP.
        private static bool TryResolveSessionKey(ActionExecutingContext context, out string key)
        {
            key = null;

            var raw = context.ActionArguments.Values
                .OfType<ReserveInventoryDto>()
                .FirstOrDefault()?.SessionId;

            if (!string.IsNullOrWhiteSpace(raw) && Guid.TryParseExact(raw.Trim(), "D", out var parsed))
            {
                key = parsed.ToString("D"); // canonical lowercase 36-char representation
                return true;
            }

            return false;
        }

        // Sliding window that counts ACCEPTED requests only (M06): prune expired hits, deny (WITHOUT recording)
        // when MaxRequests accepted hits are already inside the window, otherwise record this accepted hit.
        private static bool IsAllowed(string key)
        {
            // Fetch-or-create a bounded, self-expiring per-session bucket. Sliding expiration reclaims idle
            // sessions and Size = 1 participates in the SizeLimit cap (M05).
            var bucket = Buckets.GetOrCreate(key, entry =>
            {
                entry.Size = 1;
                entry.SlidingExpiration = Window;
                return new SessionBucket();
            });

            var now = DateTimeOffset.UtcNow;
            var cutoff = now - Window;

            // Lock the per-key bucket so simultaneous reserve requests for the same session are counted
            // correctly (the concurrency/load test can fire many requests at once).
            lock (bucket.Gate)
            {
                bucket.PruneOlderThan(cutoff); // bounded: at most MaxRequests timestamps are ever retained
                if (bucket.Count >= MaxRequests)
                {
                    return false; // window already holds MaxRequests accepted hits -> deny WITHOUT recording (M06)
                }

                bucket.Add(now); // record ONLY the accepted request (M06)
                return true;
            }
        }

        // Per-session sliding-window state. The timestamp count is capped at MaxRequests by IsAllowed, so all
        // operations here are O(MaxRequests) and the memory retained per session is bounded (M05).
        private sealed class SessionBucket
        {
            public readonly object Gate = new object();
            private readonly List<DateTimeOffset> _hits = new List<DateTimeOffset>(MaxRequests);

            public int Count => _hits.Count;
            public void Add(DateTimeOffset timestamp) => _hits.Add(timestamp);
            public void PruneOlderThan(DateTimeOffset cutoff) => _hits.RemoveAll(t => t < cutoff);
        }
    }
}
