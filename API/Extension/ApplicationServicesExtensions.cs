using System.Linq;
using API.Errors;
using API.Hubs; // Flash-Sale feature: InventoryBroadcaster (IInventoryBroadcaster impl over IHubContext<InventoryHub>)
using Core.Interfaces;
using Infrastructure.Data;
using Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace API.Extension
{
    public static class ApplicationServicesExtensions
    {
        public static IServiceCollection AddApplicationServices(this IServiceCollection services)
        {
            services.AddSingleton<IResponseCacheService, ResponseCacheService>();
            services.AddScoped<ITokenService, TokenService>();
            services.AddScoped<IOrderService, OrderService>();
            services.AddScoped<IPaymentService, PaymentService>();
            services.AddScoped<IUnitOfWork, UnitOfWork>();
            services.AddScoped<IProductRepository, ProductRepository>();
            services.AddScoped<IBasketRepository, BasketRepository>();
            services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));

            // Flash-Sale feature: register the real-time inventory / flash-sale services and the background
            // reservation-expiry sweep alongside the existing scoped registrations (AAP §0.3.2).
            //
            // The broadcaster is the ONLY SignalR-aware service; its IHubContext<InventoryHub> dependency is
            // supplied by services.AddSignalR() (Startup). It is registered as a SINGLETON (review findings
            // M12/M13): it is stateless and depends only on the singleton IHubContext, so a singleton lifetime is
            // safe, and — critically — it lets the singleton IInventoryBroadcastCoordinator consume it WITHOUT a
            // captive-dependency violation (a singleton must not depend on a scoped service).
            services.AddSingleton<IInventoryBroadcaster, InventoryBroadcaster>();

            // Flash-Sale feature (review findings M12/M13/M14): the broadcast coordinator is the single, shared,
            // ordered, persistence-authoritative publication point reused by EVERY writer that changes
            // availability (reserve/release/consume here, schedule in FlashSaleService, and the expiry sweep). It
            // MUST be a SINGLETON so its per-product serialization gate (SemaphoreSlim) is shared process-wide;
            // a scoped coordinator would give each request its own locks and defeat cross-request ordering. It
            // depends only on the singleton broadcaster and ILogger, so the lifetime is captive-safe. The scoped
            // services below inject this singleton (scoped-depends-on-singleton is always valid).
            services.AddSingleton<IInventoryBroadcastCoordinator, InventoryBroadcastCoordinator>();

            // FlashSaleService and InventoryReservationService depend on the coordinator; OrderService (already
            // registered above) consumes IInventoryReservationService to consume the session's held stock after
            // a successful checkout — without these registrations the OrderService activation fails at startup.
            services.AddScoped<IFlashSaleService, FlashSaleService>();
            services.AddScoped<IInventoryReservationService, InventoryReservationService>();
            services.AddHostedService<ReservationExpirySweepService>();

            services.Configure<ApiBehaviorOptions>(options =>
            {
                options.InvalidModelStateResponseFactory = actionContext =>
                {
                    var errors = actionContext.ModelState
                        .Where(e => e.Value.Errors.Count > 0)
                        .SelectMany(x => x.Value.Errors)
                        .Select(x => x.ErrorMessage).ToArray();

                    var errorResponse = new ApiValidationErrorResponose
                    {
                        Errors = errors
                    };
                    return new BadRequestObjectResult(errorResponse);
                };
            });
            return services;
        }
    }
}