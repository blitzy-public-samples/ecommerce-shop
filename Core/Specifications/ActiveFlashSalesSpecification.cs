using System;
using Core.Entities;

namespace API.Specifications
{
    public class ActiveFlashSalesSpecification : BaseSpecification<FlashSale>
    {
        public ActiveFlashSalesSpecification(DateTimeOffset now)
            : base(f => f.Status == FlashSaleStatus.Active && f.StartsAt <= now && now < f.EndsAt)
        {
        }

        public ActiveFlashSalesSpecification(int productId, DateTimeOffset now)
            : base(f => f.ProductId == productId && f.Status == FlashSaleStatus.Active && f.StartsAt <= now && now < f.EndsAt)
        {
        }
    }
}
