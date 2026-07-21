using System.Text;
// Flash-Sale feature: System.Threading.Tasks for Task.CompletedTask; Microsoft.AspNetCore.Http for PathString.StartsWithSegments (query-string hub token).
using System.Threading.Tasks;
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
                            var hubPath = config["SIGNALR_HUB_PATH"];
                            if (!string.IsNullOrEmpty(accessToken) &&
                                !string.IsNullOrEmpty(hubPath) &&
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