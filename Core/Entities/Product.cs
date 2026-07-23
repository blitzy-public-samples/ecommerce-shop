using System.Text;

namespace Core.Entities
{
    public class Product:BaseEntity
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public decimal Price { get; set; }
        public string PictureUrl { get; set; }
        public ProductType ProductType { get; set; }
        public int ProductTypeId { get; set; }
        public ProductBrand ProductBrand { get; set; }
        public int ProductBrandId { get; set; }

        // Flash-Sale feature (review finding F04): GENERAL product-row optimistic-concurrency token,
        // mapped via ProductConfiguration.IsConcurrencyToken(). It guards concurrent mutations of the
        // Product row itself and is NOT the flash-sale allocation/oversell authority — that single
        // authority is FlashSale.Version (which owns StockAllocation). The reservation/sweep flows
        // never read or increment Product.Version, so the two tokens have clearly separated roles and
        // there is exactly one allocation authority. Not projected into any DTO.
        public uint Version { get; set; }
    }
}