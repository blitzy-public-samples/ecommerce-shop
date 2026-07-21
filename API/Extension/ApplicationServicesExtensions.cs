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
            // reservation-expiry sweep alongside the existing scoped registrations (AAP §0.3.2). The broadcaster
            // is the ONLY SignalR-aware service; its IHubContext<InventoryHub> dependency is supplied by
            // services.AddSignalR() (Startup). It is registered AddScoped exactly as documented on
            // InventoryBroadcaster, so it resolves correctly inside the per-tick DI scope the sweep opens.
            // FlashSaleService and InventoryReservationService depend on the broadcaster; OrderService (already
            // registered above) consumes IInventoryReservationService to release the session's held stock after
            // a successful checkout — without these registrations the OrderService activation fails at startup.
            services.AddScoped<IInventoryBroadcaster, InventoryBroadcaster>();
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