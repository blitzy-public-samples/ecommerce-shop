using System.ComponentModel.DataAnnotations;

namespace API.Dtos
{
    public class ReserveInventoryDto
    {
        // Flash-Sale feature (review finding F05): a non-nullable int with only [Required] would accept 0,
        // so enforce a positive range instead. ProductId must identify a real product (>= 1).
        [Range(1, int.MaxValue, ErrorMessage = "ProductId must be a positive integer")]
        public int ProductId { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Quantity must be at least 1")]
        public int Quantity { get; set; }

        // The client basket UUID (localStorage['basket_id']) reused as the reservation session key — no new identity concept (§0.1.2, R8).
        // Flash-Sale feature (review finding F05): [Required] on a non-nullable string only rejects null/empty,
        // not malformed or unbounded input. Constrain to the canonical 36-char UUID v4 form (as produced by the
        // client uuidv4()); the regex rejects whitespace and non-UUID text, and StringLength caps length to match
        // the EF SessionId HasMaxLength(36) mapping. Whitespace-only input fails the anchored regex.
        [Required(ErrorMessage = "SessionId is required")]
        [StringLength(36, MinimumLength = 36, ErrorMessage = "SessionId must be a 36-character UUID")]
        [RegularExpression(
            "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            ErrorMessage = "SessionId must be a canonical UUID (e.g. 3f2504e0-4f89-41d3-9a0c-0305e82c3301)")]
        public string SessionId { get; set; }
    }
}
