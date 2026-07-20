using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using API.Helpers;
using Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace API.Tests.Helpers
{
    /// <summary>
    /// Unit tests for the response-cache action filter
    /// <see cref="API.Helpers.CachedAttribute"/> (<c>: Attribute, IAsyncActionFilter</c>).
    /// <para>
    /// The system under test resolves an <see cref="IResponseCacheService"/> from the request
    /// service provider, derives a deterministic cache key from the request path and its
    /// (alphabetically ordered) query parameters, short-circuits the pipeline with a
    /// <see cref="ContentResult"/> on a cache hit, and — on a cache miss — caches the unwrapped
    /// value of an <see cref="OkObjectResult"/> with the TTL supplied to the attribute's
    /// constructor. These behaviours are exercised in complete isolation: the cache collaborator
    /// is a Moq double, and no real Redis, HTTP transport, or ASP.NET Core request pipeline is
    /// involved (AAP §0.4.1 unit-test layer).
    /// </para>
    /// <para>
    /// Conventions (AAP §0.10.2): test names follow
    /// <c>MethodName_StateUnderTest_ExpectedBehavior</c>, bodies follow Arrange-Act-Assert, all
    /// assertions use FluentAssertions, and the sole collaborator is mocked with Moq. No
    /// production code is modified by anything in this file (AAP §0.10.1).
    /// </para>
    /// </summary>
    public class CachedAttributeTests
    {
        /// <summary>
        /// Time-to-live (in seconds) passed to the <see cref="CachedAttribute"/> constructor across
        /// the tests. Reused in the caching assertion so the value that flows from the constructor
        /// through to <see cref="IResponseCacheService.CacheResponseAsync"/> is explicit.
        /// </summary>
        private const int DefaultTtlSeconds = 600;

        // ------------------------------------------------------------------
        // Test infrastructure helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds a minimal <see cref="ActionExecutingContext"/> for the filter under test, backed by
        /// a <see cref="DefaultHttpContext"/> whose <see cref="HttpRequest.Path"/> and (optionally)
        /// <see cref="HttpRequest.QueryString"/> are populated, and whose
        /// <see cref="HttpContext.RequestServices"/> exposes the supplied
        /// <see cref="IResponseCacheService"/> so the filter's
        /// <c>GetRequiredService&lt;IResponseCacheService&gt;()</c> call succeeds.
        /// </summary>
        /// <remarks>
        /// Assigning <see cref="HttpRequest.QueryString"/> updates the underlying request feature;
        /// the SUT enumerates <see cref="HttpRequest.Query"/>, which ASP.NET Core 5 lazily re-parses
        /// from the assigned query string, so there is no need to populate <c>Request.Query</c>
        /// separately. A <see cref="ServiceCollection"/> is used (rather than a hand-rolled
        /// <see cref="IServiceProvider"/> mock) so that <c>GetRequiredService</c> resolves exactly as
        /// it would at runtime.
        /// </remarks>
        /// <param name="path">The request path (e.g. <c>"/api/products"</c>). Must begin with '/'.</param>
        /// <param name="queryString">The raw query string beginning with '?', or <c>null</c>/empty
        /// for a request with no query parameters.</param>
        /// <param name="cacheService">The (mocked) cache service to register in request services.</param>
        /// <returns>A fully-wired <see cref="ActionExecutingContext"/> ready to pass to the filter.</returns>
        private static ActionExecutingContext BuildExecutingContext(
            string path, string queryString, IResponseCacheService cacheService)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = new PathString(path);

            if (!string.IsNullOrEmpty(queryString))
            {
                // Request.Query re-parses from the assigned QueryString in ASP.NET Core 5.
                httpContext.Request.QueryString = new QueryString(queryString);
            }

            var services = new ServiceCollection();
            services.AddSingleton<IResponseCacheService>(cacheService);
            httpContext.RequestServices = services.BuildServiceProvider();

            var actionContext = new ActionContext(
                httpContext,
                new RouteData(),
                new ActionDescriptor(),
                new ModelStateDictionary());

            return new ActionExecutingContext(
                actionContext,
                new List<IFilterMetadata>(),
                new Dictionary<string, object>(),
                controller: null);
        }

        /// <summary>
        /// Produces an <see cref="ActionExecutionDelegate"/> that, when invoked by the filter,
        /// simulates the downstream action executing and returning the supplied
        /// <paramref name="result"/>. The returned <see cref="ActionExecutedContext"/> reuses the
        /// filters and controller of the originating <paramref name="context"/>.
        /// </summary>
        /// <param name="context">The executing context the filter is processing.</param>
        /// <param name="result">The action result to surface as the executed result.</param>
        /// <returns>A delegate matching the <see cref="ActionExecutionDelegate"/> signature.</returns>
        private static ActionExecutionDelegate NextReturning(
            ActionExecutingContext context, IActionResult result)
            => () => Task.FromResult(
                new ActionExecutedContext(context, context.Filters, context.Controller)
                {
                    Result = result
                });

        // ------------------------------------------------------------------
        // Test 1 — deterministic cache-key generation (order-independent)
        // ------------------------------------------------------------------

        /// <summary>
        /// The cache key must be independent of the order in which query parameters arrive: the SUT
        /// sorts them with <c>OrderBy(x =&gt; x.Key)</c> before appending each <c>|key-value</c>
        /// segment. Two requests to the same path carrying the same parameters in different orders
        /// must therefore produce byte-for-byte identical keys, equal to the canonical
        /// alphabetically-ordered form.
        /// </summary>
        [Fact]
        public async Task GenerateCacheKeyFromRequest_WhenQueryParamsSuppliedInDifferentOrders_ProducesIdenticalKey()
        {
            // Arrange
            var mockCache = new Mock<IResponseCacheService>();
            var capturedKeys = new List<string>();
            mockCache
                .Setup(x => x.GetCachedResponseAsync(It.IsAny<string>()))
                .Callback<string>(key => capturedKeys.Add(key))
                .ReturnsAsync((string)null); // cache miss => filter proceeds, no short-circuit

            var sut = new CachedAttribute(DefaultTtlSeconds);

            var contextInOrder = BuildExecutingContext(
                "/api/products", "?brandId=1&sort=priceAsc&typeId=2", mockCache.Object);
            var contextReversed = BuildExecutingContext(
                "/api/products", "?typeId=2&sort=priceAsc&brandId=1", mockCache.Object);

            // Act
            await sut.OnActionExecutionAsync(
                contextInOrder, NextReturning(contextInOrder, new OkObjectResult("payload")));
            await sut.OnActionExecutionAsync(
                contextReversed, NextReturning(contextReversed, new OkObjectResult("payload")));

            // Assert
            capturedKeys.Should().HaveCount(2,
                "the filter resolves the cache key exactly once per request");
            capturedKeys[0].Should().Be(capturedKeys[1],
                "query-parameter ordering must not affect the generated key");
            capturedKeys[0].Should().Be("/api/products|brandId-1|sort-priceAsc|typeId-2",
                "the key is the request path followed by each query parameter appended in ascending key order");
        }

        // ------------------------------------------------------------------
        // Test 2 — cache hit short-circuits the pipeline
        // ------------------------------------------------------------------

        /// <summary>
        /// On a cache hit (a non-null, non-empty cached response), the filter must set
        /// <see cref="ActionExecutingContext.Result"/> to a <see cref="ContentResult"/> carrying the
        /// cached JSON with HTTP 200 and <c>application/json</c>, and must return immediately without
        /// invoking the <see cref="ActionExecutionDelegate"/> or re-caching anything.
        /// </summary>
        [Fact]
        public async Task OnActionExecutionAsync_WhenCachedResponseExists_ShortCircuitsWithContentResultAndDoesNotInvokeNext()
        {
            // Arrange
            const string cachedJson = "{\"id\":1,\"name\":\"Cached Product\"}";
            var mockCache = new Mock<IResponseCacheService>();
            mockCache
                .Setup(x => x.GetCachedResponseAsync(It.IsAny<string>()))
                .ReturnsAsync(cachedJson); // cache hit

            var sut = new CachedAttribute(DefaultTtlSeconds);
            var context = BuildExecutingContext("/api/products", queryString: null, mockCache.Object);

            var nextWasInvoked = false;
            ActionExecutionDelegate next = () =>
            {
                nextWasInvoked = true;
                return Task.FromResult(
                    new ActionExecutedContext(context, context.Filters, context.Controller)
                    {
                        Result = new OkObjectResult("should-not-be-reached")
                    });
            };

            // Act
            await sut.OnActionExecutionAsync(context, next);

            // Assert
            nextWasInvoked.Should().BeFalse(
                "a cache hit must short-circuit the pipeline before the downstream action executes");
            context.Result.Should().BeOfType<ContentResult>(
                "a cache hit returns the cached body as a ContentResult");

            var contentResult = (ContentResult)context.Result;
            contentResult.StatusCode.Should().Be(200);
            contentResult.ContentType.Should().Be("application/json");
            contentResult.Content.Should().Be(cachedJson);

            mockCache.Verify(
                x => x.CacheResponseAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<TimeSpan>()),
                Times.Never,
                "nothing is (re)cached when the response is served from cache");
        }

        // ------------------------------------------------------------------
        // Test 3 — cache miss with an OkObjectResult is cached with the TTL
        // ------------------------------------------------------------------

        /// <summary>
        /// On a cache miss where the downstream action yields an <see cref="OkObjectResult"/>, the
        /// filter must cache the <em>unwrapped</em> value (<see cref="ObjectResult.Value"/>, not the
        /// result wrapper) under the request-derived key, using
        /// <see cref="TimeSpan.FromSeconds(double)"/> of the TTL passed to the constructor. A path
        /// with no query string is used so the expected key is exactly the request path.
        /// </summary>
        [Fact]
        public async Task OnActionExecutionAsync_WhenCacheMissAndResultIsOk_CachesValueWithConfiguredTtl()
        {
            // Arrange
            var mockCache = new Mock<IResponseCacheService>();
            mockCache
                .Setup(x => x.GetCachedResponseAsync(It.IsAny<string>()))
                .ReturnsAsync((string)null); // cache miss

            var sut = new CachedAttribute(DefaultTtlSeconds);
            // No query string => the generated key is exactly the request path.
            var context = BuildExecutingContext("/api/products", queryString: null, mockCache.Object);

            // The exact instance placed in the OkObjectResult; the filter caches OkObjectResult.Value,
            // which is this same reference, so Moq's argument match succeeds by reference equality.
            var payload = new { Id = 1, Name = "Fresh Product" };
            var okResult = new OkObjectResult(payload);

            // Act
            await sut.OnActionExecutionAsync(context, NextReturning(context, okResult));

            // Assert
            mockCache.Verify(
                x => x.CacheResponseAsync(
                    "/api/products",
                    payload,
                    TimeSpan.FromSeconds(DefaultTtlSeconds)),
                Times.Once,
                "a cache miss with an OkObjectResult caches the unwrapped value under the request key with the configured TTL");
        }

        // ------------------------------------------------------------------
        // Test 4 — non-OkObjectResult responses are not cached
        // ------------------------------------------------------------------

        /// <summary>
        /// On a cache miss where the downstream action yields anything other than an
        /// <see cref="OkObjectResult"/> (here a <see cref="NotFoundObjectResult"/>), the filter must
        /// not cache the response — only successful <c>200 OK</c> object results are cacheable.
        /// </summary>
        [Fact]
        public async Task OnActionExecutionAsync_WhenResultIsNotOkObjectResult_DoesNotCache()
        {
            // Arrange
            var mockCache = new Mock<IResponseCacheService>();
            mockCache
                .Setup(x => x.GetCachedResponseAsync(It.IsAny<string>()))
                .ReturnsAsync((string)null); // cache miss

            var sut = new CachedAttribute(DefaultTtlSeconds);
            var context = BuildExecutingContext("/api/products", queryString: null, mockCache.Object);

            var notFoundResult = new NotFoundObjectResult("resource missing");

            // Act
            await sut.OnActionExecutionAsync(context, NextReturning(context, notFoundResult));

            // Assert
            mockCache.Verify(
                x => x.CacheResponseAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<TimeSpan>()),
                Times.Never,
                "only OkObjectResult responses are cached; any other result type must be left uncached");
        }
    }
}
