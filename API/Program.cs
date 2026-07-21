using System;
using System.Threading.Tasks;
using Core.Entities.Identity;
using Core.Interfaces;
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

                    // Seed Redis stock counters from committed PostgreSQL stock so counters are warm
                    // before the first request. This is isolated in its OWN try/catch: a Redis outage
                    // at startup must NOT abort the remaining critical initialization (the identity
                    // database migration and user seeding below), otherwise login would break whenever
                    // Redis is unavailable. Fail-closed: if the counters cannot be warmed, the
                    // reservation hot path falls back to the PostgreSQL SELECT ... FOR UPDATE check and
                    // the reconciliation service reseeds the counters once Redis becomes reachable.
                    try
                    {
                        var inventoryService = services.GetRequiredService<IInventoryService>();
                        await inventoryService.SeedStockCountersAsync();
                    }
                    catch (Exception redisEx)
                    {
                        var redisLogger = loggerFactory.CreateLogger<Program>();
                        redisLogger.LogWarning(redisEx,
                            "Redis stock-counter seeding was skipped at startup (Redis unavailable). " +
                            "Identity initialization will continue; counters will be reseeded by the " +
                            "reconciliation service once Redis is reachable.");
                    }

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
                .ConfigureWebHostDefaults(webBuilder => { webBuilder.UseStartup<Startup>(); });
        }
    }
}