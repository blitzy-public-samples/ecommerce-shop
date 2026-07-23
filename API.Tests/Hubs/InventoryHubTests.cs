using API.Hubs;
using FluentAssertions;
// QA finding F1: Microsoft.AspNetCore.Http is referenced to construct a PathString from the resolver's output,
// reproducing the EXACT failure point (implicit string->PathString conversion) the fix must eliminate.
using Microsoft.AspNetCore.Http;
using Xunit;

namespace API.Tests.Hubs
{
    /// <summary>
    /// Unit tests for <see cref="InventoryHub.ResolveHubPath(string)"/> — the SINGLE canonical resolver that
    /// both <c>Startup.MapHub&lt;InventoryHub&gt;</c> (route registration) and
    /// <c>IdentityServiceExtensions.OnMessageReceived</c> (JWT query-string token lift, via
    /// <see cref="PathString.StartsWithSegments(PathString)"/>) use to derive the effective SignalR hub path
    /// from the <c>SIGNALR_HUB_PATH</c> configuration key.
    /// <para>
    /// These tests lock in the behavior required to close <b>QA finding F1 (MINOR)</b>: a
    /// <c>SIGNALR_HUB_PATH</c> supplied WITHOUT a leading slash (e.g. <c>"hubs/custom"</c>) previously flowed
    /// through unchanged and, when converted to a <see cref="PathString"/> in the JWT event, threw
    /// <see cref="System.ArgumentException"/> ("The path in 'value' must start with '/'"), which surfaced as an
    /// HTTP 500 on EVERY hub negotiate and on every request carrying a <c>?access_token=</c> query. The resolver
    /// must therefore always return a rooted path (leading <c>/</c>), so both call sites agree and the value is
    /// always a valid <see cref="PathString"/>. Tests follow the repository convention
    /// <c>MethodName_StateUnderTest_ExpectedBehavior</c> with FluentAssertions. No production code is modified by
    /// these tests.
    /// </para>
    /// </summary>
    public class InventoryHubTests
    {
        // Null / empty / whitespace all fall back to the canonical default HubPath (pre-existing M04 behavior).
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t")]
        public void ResolveHubPath_NullEmptyOrWhitespace_ReturnsCanonicalDefault(string configured)
        {
            InventoryHub.ResolveHubPath(configured).Should().Be(InventoryHub.HubPath);
            InventoryHub.HubPath.Should().Be("/hubs/inventory");
        }

        // An already-rooted value is returned unchanged (trimmed of incidental surrounding whitespace).
        [Theory]
        [InlineData("/hubs/inventory", "/hubs/inventory")]
        [InlineData("/ws/live", "/ws/live")]
        [InlineData("  /ws/live  ", "/ws/live")]
        public void ResolveHubPath_RootedValue_ReturnsTrimmedValueUnchanged(string configured, string expected)
        {
            InventoryHub.ResolveHubPath(configured).Should().Be(expected);
        }

        // QA finding F1: a value WITHOUT a leading slash must be normalized to a rooted path (slash prefixed),
        // AFTER trimming, so both the MapHub route and the JWT PathString guard receive a valid rooted path.
        [Theory]
        [InlineData("hubs/custom", "/hubs/custom")]
        [InlineData("ws/live", "/ws/live")]
        [InlineData("  hubs/custom  ", "/hubs/custom")]
        public void ResolveHubPath_MissingLeadingSlash_PrefixesSlash(string configured, string expected)
        {
            InventoryHub.ResolveHubPath(configured).Should().Be(expected);
        }

        // QA finding F1 (regression guard at the EXACT failure point): the resolver's output must ALWAYS be a
        // valid PathString. Converting the result to a PathString must never throw the ArgumentException that
        // previously 500'd the hub. This is the precise operation IdentityServiceExtensions performs.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("/hubs/inventory")]
        [InlineData("/ws/live")]
        [InlineData("hubs/custom")]
        [InlineData("  hubs/custom  ")]
        public void ResolveHubPath_AnyInput_ProducesValidPathStringAndAlwaysStartsWithSlash(string configured)
        {
            var resolved = InventoryHub.ResolveHubPath(configured);

            resolved.Should().StartWith("/", "both MapHub and the JWT PathString guard require a rooted path");

            // The exact conversion performed in IdentityServiceExtensions.OnMessageReceived; must not throw.
            System.Action convertToPathString = () => _ = new PathString(resolved);
            convertToPathString.Should().NotThrow<System.ArgumentException>(
                "a no-leading-slash SIGNALR_HUB_PATH previously threw here and 500'd every hub connection (QA finding F1)");

            // And the resulting PathString actually matches a request under that hub path (proves usability).
            var requestPath = new PathString(resolved + "/negotiate");
            requestPath.StartsWithSegments(new PathString(resolved)).Should().BeTrue();
        }
    }
}
