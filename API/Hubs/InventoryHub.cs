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
        // Canonical per-product group name. MUST match InventoryBroadcaster byte-for-byte so
        // group-scoped events reach the correct clients. Also mirrored by the Angular client's
        // joinProductGroup(productId) invocation.
        public static string ProductGroup(int productId) => $"product-{productId}";

        // Client invokes 'JoinProductGroup' on product-details load so it receives only that product's updates.
        public Task JoinProductGroup(int productId) =>
            Groups.AddToGroupAsync(Context.ConnectionId, ProductGroup(productId));

        // Client invokes 'LeaveProductGroup' on component destroy / navigation away.
        public Task LeaveProductGroup(int productId) =>
            Groups.RemoveFromGroupAsync(Context.ConnectionId, ProductGroup(productId));
    }
}
