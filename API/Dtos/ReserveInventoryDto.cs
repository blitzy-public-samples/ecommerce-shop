using System; // Flash-Sale feature (review finding m08): Guid for canonical UUID v4 parsing/normalization.
using System.ComponentModel.DataAnnotations;
using API.Helpers; // Flash-Sale feature (review finding m08): CanonicalUuidV4Attribute (v4/variant validation).

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
        //
        // Flash-Sale feature (review finding m08): the previous [RegularExpression] accepted ANY hex UUID
        // shape in EITHER case and performed NO canonicalization, so one logical basket could present many
        // distinct rate-limit identities (upper- vs lower-case, or a non-v4 GUID), each receiving its own
        // 10 req/min budget and defeating the per-session limit. We now:
        //   (1) validate the value is a canonical RFC 4122 v4 UUID via [CanonicalUuidV4]
        //       (Guid.TryParseExact + explicit version/variant check — see CanonicalUuidV4Attribute), and
        //   (2) normalize accepted input to the single lowercase "D" representation inside the setter, so the
        //       rate limiter (M07) and the reservation service key off exactly ONE identity per basket.
        // [StringLength] still caps length to the EF SessionId HasMaxLength(36) mapping; [Required] reports a
        // single clear message for null/empty (CanonicalUuidV4 intentionally passes null through to [Required]).
        [Required(ErrorMessage = "SessionId is required")]
        [StringLength(36, MinimumLength = 36, ErrorMessage = "SessionId must be a 36-character UUID")]
        [CanonicalUuidV4(ErrorMessage = "SessionId must be a canonical UUID v4 (e.g. 3f2504e0-4f89-41d3-9a0c-0305e82c3301)")]
        public string SessionId
        {
            get => _sessionId;
            // Normalize a valid v4 UUID (regardless of input case, and tolerating surrounding whitespace) to
            // the canonical lowercase "D" form so a single basket maps to a single rate-limit/reservation key.
            // Invalid input is stored unchanged so the validation attributes above report it; a property setter
            // must never throw during model binding.
            set => _sessionId = value != null && Guid.TryParseExact(value.Trim(), "D", out var canonical)
                ? canonical.ToString("D")
                : value;
        }

        // Backing field for the normalized SessionId (see the setter above).
        private string _sessionId;
    }
}
