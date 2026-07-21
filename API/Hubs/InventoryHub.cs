using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace API.Hubs
{
    // Flash-Sale feature: real-time inventory + flash-sale SignalR hub (server-to-client only).
    // It declares NO domain-mutation methods — all writes go through the REST controllers
    // (InventoryController / FlashSalesController). Secured by the EXISTING JWT bearer scheme via
    // [Authorize]; because a browser WebSocket cannot send an Authorization header, the token arrives
    // as a query-string access_token and is lifted server-side by JwtBearerEvents.OnMessageReceived in
    // API/Extension/IdentityServiceExtensions.cs (combined with .AllowCredentials() on the CORS policy
    // in API/Startup.cs). Single-instance, in-memory — no Redis backplane (AAP §0.5.2).
    [Authorize]
    public class InventoryHub : Hub
    {
        // Flash-Sale feature (review finding F07): the SINGLE canonical hub path. It is the fallback used by
        // JWT query-token extraction (IdentityServiceExtensions) AND by the future endpoints.MapHub<InventoryHub>
        // registration in Startup, so both sides agree on the path even if the SIGNALR_HUB_PATH config key is
        // absent. Keep this in sync with the SIGNALR_HUB_PATH value in appsettings.json.
        public const string HubPath = "/hubs/inventory";

        // Flash-Sale feature (review finding F08): per-connection key under which we remember the ONE product
        // group a connection is currently subscribed to, so a new join replaces the previous subscription.
        private const string CurrentProductGroupKey = "flashsale:current-product-group";

        // Canonical per-product group name. MUST match InventoryBroadcaster byte-for-byte so
        // group-scoped events reach the correct clients. Also mirrored by the Angular client's
        // joinProductGroup(productId) invocation.
        public static string ProductGroup(int productId) => $"product-{productId}";

        // Client invokes 'JoinProductGroup' on product-details load so it receives only that product's updates.
        // Flash-Sale feature (review finding F08): validate the id and bound each connection to AT MOST ONE
        // product group. Rejecting non-positive ids and replacing any prior subscription closes the in-memory
        // resource-exhaustion path (a connection can no longer accumulate unbounded groups for arbitrary ids).
        public async Task JoinProductGroup(int productId)
        {
            if (productId <= 0)
            {
                throw new HubException("productId must be a positive integer.");
            }

            var group = ProductGroup(productId);

            // Replace the connection's previous product group (if any and different) — cap of one group per connection.
            if (Context.Items.TryGetValue(CurrentProductGroupKey, out var prior)
                && prior is string priorGroup
                && priorGroup != group)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, priorGroup);
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, group);
            Context.Items[CurrentProductGroupKey] = group;
        }

        // Client invokes 'LeaveProductGroup' on component destroy / navigation away.
        // Flash-Sale feature (review finding F08): validate the id and clear the remembered current group.
        public async Task LeaveProductGroup(int productId)
        {
            if (productId <= 0)
            {
                throw new HubException("productId must be a positive integer.");
            }

            var group = ProductGroup(productId);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);

            if (Context.Items.TryGetValue(CurrentProductGroupKey, out var current)
                && current is string currentGroup
                && currentGroup == group)
            {
                Context.Items.Remove(CurrentProductGroupKey);
            }
        }
    }
}
