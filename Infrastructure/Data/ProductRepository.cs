using System; // Flash-Sale feature: required for DateTimeOffset in read-time effective-price resolution
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

        // Flash-Sale feature: internal, display-only effective-price resolution for the Real-Time
        // Inventory & Flash Sale feature. Resolves the price a product should DISPLAY at read time by
        // overlaying an active flash sale's SalePrice on top of the base Product.Price when now() falls
        // inside the sale's [StartAt, EndAt] window (inclusive). This is a read-only overlay: it NEVER
        // writes back to Product.Price, never calls SaveChanges, and is deliberately NOT wired into
        // GetProductByIdAsync/GetProductsAsync, so the /api/products contract (payload shape AND
        // base-price value) is preserved byte-for-byte. Intended for consumption by feature code
        // (e.g. flash-sale / order services) within the Infrastructure assembly.
        internal async Task<decimal> ResolveEffectivePriceAsync(Product product)
        {
            // Guard: a null product has no price to resolve.
            if (product == null) return 0m;

            // Use UTC to match the DateTimeOffset window stored on FlashSale (StartAt / EndAt).
            var now = DateTimeOffset.UtcNow;

            // Select the flash sale that is currently active for this product
            // (StartAt <= now <= EndAt, inclusive). If multiple sales overlap, the one that started
            // most recently takes precedence.
            var sale = await _context.FlashSales
                .Where(fs => fs.ProductId == product.Id && fs.StartAt <= now && fs.EndAt >= now)
                .OrderByDescending(fs => fs.StartAt)
                .FirstOrDefaultAsync();

            // Display-only overlay: return the sale price while a sale is active, otherwise the
            // untouched base price. Product.Price is never modified here.
            return sale != null ? sale.SalePrice : product.Price;
        }
    }
}