using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data
{
    public class ProductRepository:IProductRepository
    {
        private readonly StoreContext _context;

        public ProductRepository(StoreContext context)
        {
            _context = context;
        }

        public async Task<Product> GetProductByIdAsync(int id)
        {
            return await _context.Products
                .Include(p => p.ProductType)
                .Include(p => p.ProductBrand)
                .FirstOrDefaultAsync(p => p.Id == id);
        }

        public async Task<IReadOnlyList<Product>> GetProductsAsync()
        {
            return await _context.Products
                .Include(p=>p.ProductType)
                .Include(p=>p.ProductBrand)
                .ToListAsync();
        }

        public async Task<IReadOnlyList<ProductBrand>> GetProductsBrandsAsync()
        {
            return await _context.ProductBrands.ToListAsync();
        }

        public async Task<IReadOnlyList<ProductType>> GetProductsTypesAsync()
        {
            return await _context.ProductTypes.ToListAsync();
        }

        // Flash-Sale feature (review finding M08): this repository intentionally performs NO read-time
        // flash-sale price overlay. Per the AAP the flash-sale price reaches the client via
        // GET /api/flash-sales/active and the SignalR hub — never the /api/products product DTO (§0.3.2) — and
        // it is display-only (checkout totals derive from the base products.price, §0.5.2). A previous internal
        // ResolveEffectivePriceAsync helper here had no authorized consumer (dead code), so the effective-price
        // overlay lives in its only real consumer, FlashSaleService.GetActiveSalesAsync. Keeping it out of the
        // catalog read path preserves the /api/products payload (shape AND base-price value) byte-for-byte and
        // leaves the products.price write path untouched.
    }
}