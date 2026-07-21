using System.Collections.Generic;
using API.Dtos;
using AutoMapper;
using Core.Entities;
using Core.Entities.OrderAggregate;
using Core.Interfaces;

namespace API.Helpers
{
    public class MappingProfiles : Profile
    {
        public MappingProfiles()
        {
            CreateMap<Product, ProductToReturnDto>()
                .ForMember(d => d.ProductBrand, o => o.MapFrom(s => s.ProductBrand.Name))
                .ForMember(d => d.ProductType, o => o.MapFrom(s => s.ProductType.Name))
                .ForMember(d => d.PictureUrl, o => o.MapFrom<ProductUrlResolver>());
            CreateMap<Core.Entities.Identity.Address, AddressDto>().ReverseMap();
            CreateMap<CustomerBasketDto, CustomerBasket>();
            CreateMap<BasketItemDto, BasketItem>();
            CreateMap<AddressDto, Core.Entities.OrderAggregate.Address>();
            CreateMap<Order, OrderToReturnDto>()
                .ForMember(d => d.DeliveryMethod, o => o.MapFrom(s => s.DeliveryMethod.ShortName))
                .ForMember(d => d.ShippingPrice, o => o.MapFrom(s => s.DeliveryMethod.Price));
            CreateMap<OrderItem, OrderItemDto>()
                .ForMember(d => d.ProductId, o => o.MapFrom(s => s.ItemOrdered.ProductItemId))
                .ForMember(d => d.ProductName, o => o.MapFrom(s => s.ItemOrdered.ProductName))
                .ForMember(d => d.PictureUrl, o => o.MapFrom(s => s.ItemOrdered.PictureUrl))
                .ForMember(d => d.PictureUrl, o => o.MapFrom<OrderItemUrlResolver>());

            // Flash-Sale feature: active sale (entity + computed availability) -> client DTO
            CreateMap<ActiveFlashSale, FlashSaleDto>()
                .ForMember(d => d.Id,                o => o.MapFrom(s => s.Sale.Id))
                .ForMember(d => d.ProductId,         o => o.MapFrom(s => s.Sale.ProductId))
                .ForMember(d => d.SalePrice,         o => o.MapFrom(s => s.Sale.SalePrice))
                .ForMember(d => d.StartAt,           o => o.MapFrom(s => s.Sale.StartAt))
                .ForMember(d => d.EndAt,             o => o.MapFrom(s => s.Sale.EndAt))
                .ForMember(d => d.StockAllocation,   o => o.MapFrom(s => s.Sale.StockAllocation))
                .ForMember(d => d.QuantityAvailable, o => o.MapFrom(s => s.QuantityAvailable));

            // Flash-Sale feature: reservation entity -> return DTO (names align 1:1)
            CreateMap<InventoryReservation, ReservationToReturnDto>();

            // Flash-Sale feature: create-request DTO -> entity; ignore server-owned Id and the
            // optimistic-concurrency Version so the map is complete (never map Version into/out of a DTO).
            CreateMap<CreateFlashSaleDto, FlashSale>()
                .ForMember(d => d.Id, o => o.Ignore())
                .ForMember(d => d.Version, o => o.Ignore());

        }
    }
}
