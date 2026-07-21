using System;
using System.ComponentModel.DataAnnotations;

namespace API.Dtos
{
    public class CreateFlashSaleDto
    {
        [Required]
        public int ProductId { get; set; }

        [Required]
        public DateTimeOffset StartAt { get; set; }

        [Required]
        public DateTimeOffset EndAt { get; set; }

        [Range(0.01, double.MaxValue, ErrorMessage = "SalePrice must be greater than 0")]
        public decimal SalePrice { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "StockAllocation must be at least 1")]
        public int StockAllocation { get; set; }
    }
}
