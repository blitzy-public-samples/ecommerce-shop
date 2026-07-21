using System.Text;
// Flash-Sale feature: System.Threading.Tasks for Task.CompletedTask; Microsoft.AspNetCore.Http for PathString.StartsWithSegments (query-string hub token).
using System.Threading.Tasks;
// Flash-Sale feature (review finding F07): API.Hubs for the single canonical InventoryHub.HubPath fallback.
using API.Hubs;
using Core.Entities.Identity;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace API.Extension
{
    public static class IdentityServiceExtensions
    {
        public static IServiceCollection AddIdentityServices(this IServiceCollection services, IConfiguration config)
        {
            var builder = services.AddIdentityCore<AppUser>();
            builder = new IdentityBuilder(builder.UserType, builder.Services);
            builder.AddEntityFrameworkStores<AppIdentityDbContext>();
            builder.AddSignInManager<SignInManager<AppUser>>();

            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Token:Key"])),
                        ValidIssuer = config["Token:Issuer"],
                        ValidateIssuer = true,
                        ValidateAudience = false,
                    };

                    // Flash-Sale feature: browsers cannot set an Authorization header on a WebSocket, so lift the JWT from the
                    // query-string 'access_token' for the SignalR hub path only (path from config SIGNALR_HUB_PATH). AAP R7 / §0.2.2.
                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = context =>
                        {
                            var accessToken = context.Request.Query["access_token"];
                            var path = context.HttpContext.Request.Path;
                            // Flash-Sale feature (review finding F07): resolve the hub path from configuration, but fall back to
                            // the single canonical InventoryHub.HubPath when SIGNALR_HUB_PATH is absent/blank. This guarantees the
                            // query-string token is always lifted for the hub — even if a deployment omits the config key — so the
                            // WebSocket auth can never silently no-op, and the extraction path always agrees with MapHub<InventoryHub>.
                            var hubPath = config["SIGNALR_HUB_PATH"];
                            if (string.IsNullOrWhiteSpace(hubPath))
                            {
                                hubPath = InventoryHub.HubPath;
                            }
                            if (!string.IsNullOrEmpty(accessToken) &&
                                path.StartsWithSegments(hubPath))
                            {
                                context.Token = accessToken;
                            }
                            return Task.CompletedTask;
                        }
                    };
                });
            
            return services;
        }
    }
}