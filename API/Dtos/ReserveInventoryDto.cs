using System.ComponentModel.DataAnnotations;

namespace API.Dtos
{
    public class ReserveInventoryDto
    {
        [Required]
        public int ProductId { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Quantity must be at least 1")]
        public int Quantity { get; set; }

        // The client basket UUID (localStorage['basket_id']) reused as the reservation session key — no new identity concept (§0.1.2, R8).
        [Required]
        public string SessionId { get; set; }
    }
}
