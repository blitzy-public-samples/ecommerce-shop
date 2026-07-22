using System.IO;
using System.Threading.Tasks; // QA Issue #3: Task.CompletedTask for the security-headers Response.OnStarting callback
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
            // Flash-Sale feature — QA finding Issue #3 (security hardening, MINOR): baseline security response
            // headers were absent on EVERY response (controllers, SignalR hub negotiate, error/404 pages, static
            // files, and authenticated endpoints such as /api/orders). This middleware is registered FIRST so it
            // wraps the entire pipeline — including the re-executed error path from UseStatusCodePagesWithReExecute
            // below — and attaches the headers via Response.OnStarting, which runs just before the response is
            // flushed (AFTER UseAuthentication has populated HttpContext.User), so the authenticated-only
            // Cache-Control decision is correct despite this middleware's early position. Each header is written
            // only when ABSENT, so it NEVER overwrites a value a downstream component set: the anonymous, cacheable
            // catalog responses served through the existing [Cached] Redis path keep their behaviour (no
            // Cache-Control is added to them). Adding response HEADERS changes no request/response body SHAPE, so the
            // AAP §0.5.2 immutability guards for /api/products and /api/orders remain satisfied.
            app.Use(async (context, next) =>
            {
                context.Response.OnStarting(() =>
                {
                    var headers = context.Response.Headers;
                    // Stop browsers MIME-sniffing a response away from its declared Content-Type.
                    if (!headers.ContainsKey("X-Content-Type-Options"))
                        headers["X-Content-Type-Options"] = "nosniff";
                    // These are JSON APIs and a same-origin SPA; deny framing to prevent clickjacking.
                    if (!headers.ContainsKey("X-Frame-Options"))
                        headers["X-Frame-Options"] = "DENY";
                    // HSTS: the API redirects to HTTPS (UseHttpsRedirection below); instruct browsers to only ever
                    // use HTTPS, closing the downgrade gap QA flagged (UseHttpsRedirection present, UseHsts absent).
                    if (!headers.ContainsKey("Strict-Transport-Security"))
                        headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
                    // Do not allow shared/browser caches to store AUTHENTICATED responses (e.g. /api/orders,
                    // /api/account). Anonymous catalog responses are deliberately left untouched so the [Cached]
                    // response-cache path is unaffected.
                    if (context.User?.Identity != null && context.User.Identity.IsAuthenticated
                        && !headers.ContainsKey("Cache-Control"))
                        headers["Cache-Control"] = "no-store";
                    return Task.CompletedTask;
                });

                await next();
            });

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