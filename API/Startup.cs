using System.IO;
using API.Extension;
using API.Helpers;
using API.Hubs; // Flash-Sale feature: InventoryHub type for endpoints.MapHub<InventoryHub>() below
using API.Middleware;
using Infrastructure.Data;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using StackExchange.Redis;

namespace API
{
    public class Startup
    {
        private readonly IConfiguration _config;

        public Startup(IConfiguration config)
        {
            _config = config;
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddAutoMapper(typeof(MappingProfiles));
            services.AddControllers();
            services.AddSignalR(); // Flash-Sale feature: enable the in-memory SignalR hub (single-instance, no Redis backplane per AAP §0.5.2)
            services.AddDbContext<StoreContext>(x =>
                x.UseNpgsql(_config.GetConnectionString("DefaultConnection")));
            services.AddDbContext<AppIdentityDbContext>(x =>
            {
                x.UseNpgsql(_config.GetConnectionString("IdentityConnection"));
            });
            services.AddSingleton<IConnectionMultiplexer>(c =>
            {
                var configuration = ConfigurationOptions.Parse(_config.GetConnectionString("Redis"),
                    true);
                return ConnectionMultiplexer.Connect(configuration);
            });
            services.AddApplicationServices();
            services.AddIdentityServices(_config);
            services.AddSwaggerDocumentation();
            services.AddCors(opt =>
            {
                opt.AddPolicy("CorsPolicy",
                    policy =>
                    {
                        // Flash-Sale feature: AllowCredentials required for the browser SignalR (WebSocket) connection.
                        // Valid here because the policy uses a FIXED origin (WithOrigins), not AllowAnyOrigin. Origin unchanged.
                        policy.AllowAnyHeader().AllowAnyMethod().WithOrigins("https://localhost:4200").AllowCredentials();
                    });
            });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.UseMiddleware<ExceptionMiddleware>();

            app.UseStatusCodePagesWithReExecute("/errors/{0}");

            app.UseHttpsRedirection();

            app.UseRouting();

            app.UseStaticFiles();
            
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(
                    Path.Combine(Directory.GetCurrentDirectory(), "Content")
                ),
                RequestPath = "/content"
            });

            app.UseCors("CorsPolicy");

            app.UseAuthentication();

            app.UseAuthorization();

            app.UseSwaggerDocumentation();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                // Flash-Sale feature: map the real-time inventory SignalR hub at the configured path.
                // Flash-Sale feature (review finding M04): resolve the path via the SINGLE canonical
                // InventoryHub.ResolveHubPath so a null/empty/whitespace SIGNALR_HUB_PATH is normalized to the
                // SAME value used by the JwtBearerEvents.OnMessageReceived hub-path check in
                // IdentityServiceExtensions.cs — authentication and routing can no longer target different URLs.
                // Registered BEFORE the SPA catch-all so MapFallbackToController remains the LAST mapping.
                endpoints.MapHub<InventoryHub>(InventoryHub.ResolveHubPath(_config["SIGNALR_HUB_PATH"]));
                endpoints.MapFallbackToController("Index", "Fallback");
            });
        }
    }
}