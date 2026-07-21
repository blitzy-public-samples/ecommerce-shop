using System;
using System.Threading.Tasks;
using Core.Entities.Identity;
using Infrastructure.Data;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace API
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var host = CreateHostBuilder(args).Build();
            using (var scope = host.Services.CreateScope())
            {
                var services = scope.ServiceProvider;
                var loggerFactory = services.GetRequiredService<ILoggerFactory>();
                try
                {
                    var context = services.GetRequiredService<StoreContext>();
                    await context.Database.MigrateAsync();
                    await StoreContextSeed.SeedAsync(context,loggerFactory);

                    var userManager = services.GetRequiredService<UserManager<AppUser>>();
                    var identityContext = services.GetRequiredService<AppIdentityDbContext>();
                    await identityContext.Database.MigrateAsync();
                    await AppIdentityDbContextSeed.SeedUserAsync(userManager);
                }
                catch (Exception ex)
                {
                    var logger = loggerFactory.CreateLogger<Program>();
                    logger.LogError(ex,"An error occured during migration");
                }
            }
            host.Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args)
        {
            return Host.CreateDefaultBuilder(args)
                // Flash-Sale feature (review finding F09): enforce the Microsoft.AspNetCore.Hosting diagnostics
                // threshold at Warning PROGRAMMATICALLY, so it applies in EVERY environment (Production included),
                // not just Development. Rationale: a browser WebSocket cannot send an Authorization header, so the
                // SignalR hub JWT arrives as a query-string 'access_token'; ASP.NET Core logs request URLs at
                // Information level by default, which would otherwise leak that token into request logs. This filter
                // is added AFTER Host.CreateDefaultBuilder's configuration-based logging, so for this exact category
                // it is the last matching rule and wins selection — pinning the level to Warning regardless of any
                // per-environment appsettings value. A programmatic fix is used deliberately because the repository
                // .gitignores the base appsettings.json (git check-ignore), so an environment-neutral config file
                // would not be committed; per-environment overrides may only tighten logging, never relax it below
                // this Warning floor (AAP §0.6).
                .ConfigureLogging(logging =>
                {
                    logging.AddFilter("Microsoft.AspNetCore.Hosting", LogLevel.Warning);
                })
                .ConfigureWebHostDefaults(webBuilder => { webBuilder.UseStartup<Startup>(); });
        }
    }
}