using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace API.Dtos
{
    // Flash-Sale feature (review findings F05/F10): validates the schedule-a-sale payload. DataAnnotations
    // cover per-field constraints; IValidatableObject covers cross-field rules, because [Required] on a
    // non-nullable value type cannot reject its default value. NOTE: product EXISTENCE and NON-OVERLAP with
    // other sales for the same product are authority checks the FlashSaleService MUST (re)validate
    // server-side — they cannot be expressed on the DTO (review finding F05 service-side note).
    public class CreateFlashSaleDto : IValidatableObject
    {
        // F05: reject 0/negative — a non-nullable int with only [Required] would accept 0.
        [Range(1, int.MaxValue, ErrorMessage = "ProductId must be a positive integer")]
        public int ProductId { get; set; }

        // Window boundaries; non-default + ordering are enforced in Validate() below (F05).
        public DateTimeOffset StartAt { get; set; }

        public DateTimeOffset EndAt { get; set; }

        // F10: SalePrice must fit the mapped decimal(18,2) column: strictly positive and within the 18,2
        // magnitude. Two-decimal scale is enforced in Validate() (RangeAttribute cannot constrain scale).
        [Range(typeof(decimal), "0.01", "9999999999999999.99",
            ErrorMessage = "SalePrice must be between 0.01 and 9999999999999999.99")]
        public decimal SalePrice { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "StockAllocation must be at least 1")]
        public int StockAllocation { get; set; }

        // F05/F10: cross-field and decimal-scale rules that per-field attributes cannot express.
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            // Reject default(DateTimeOffset): [Required] on a non-nullable DateTimeOffset does not.
            if (StartAt == default)
            {
                yield return new ValidationResult(
                    "StartAt is required and must be a valid, non-default timestamp.",
                    new[] { nameof(StartAt) });
            }

            if (EndAt == default)
            {
                yield return new ValidationResult(
                    "EndAt is required and must be a valid, non-default timestamp.",
                    new[] { nameof(EndAt) });
            }

            // The sale window must be a real forward interval.
            if (EndAt <= StartAt)
            {
                yield return new ValidationResult(
                    "EndAt must be strictly after StartAt.",
                    new[] { nameof(EndAt) });
            }

            // F10: at most two decimal places so the value round-trips through decimal(18,2).
            if (SalePrice != decimal.Round(SalePrice, 2))
            {
                yield return new ValidationResult(
                    "SalePrice must have at most 2 decimal places.",
                    new[] { nameof(SalePrice) });
            }
        }
    }
}
