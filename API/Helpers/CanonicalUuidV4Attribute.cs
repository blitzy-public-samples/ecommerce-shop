using System;
using System.ComponentModel.DataAnnotations;

namespace API.Helpers
{
    // Flash-Sale feature (review finding m08): validation attribute that accepts ONLY a canonical
    // RFC 4122 version-4 UUID in the hyphenated 8-4-4-4-12 "D" form.
    //
    // Rationale: the reserve endpoint keys its per-session rate limit (SessionRateLimitFilter) and its
    // reservations on the client basket UUID. A permissive [RegularExpression] that accepts any hex shape
    // and either case lets ONE logical basket present many distinct identities (upper- vs lower-case, or a
    // non-v4 GUID), each of which would receive its own 10 req/min budget — defeating the limit. As the
    // finding directs, this attribute validates with Guid.TryParseExact(value, "D", ...) — which rejects the
    // brace/parenthesis/no-hyphen forms ("B"/"P"/"N") and any non-parseable text — and then enforces the
    // RFC 4122 version (4) and variant (10xx) bits. Normalization to the single lowercase canonical
    // representation is performed in ReserveInventoryDto.SessionId's setter so every downstream consumer
    // (rate limiter, reservation service) keys off exactly ONE identity per basket.
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class CanonicalUuidV4Attribute : ValidationAttribute
    {
        public override bool IsValid(object value)
        {
            // A null/absent value is the concern of [Required]; treat it as valid here so a single, clear
            // "required" message is produced rather than two overlapping validation errors.
            if (value is null)
            {
                return true;
            }

            if (!(value is string candidate) || !Guid.TryParseExact(candidate, "D", out var parsed))
            {
                // Not a string, or not the 36-char hyphenated ("D") form -> reject.
                return false;
            }

            // Re-render from the parsed Guid so the version/variant read is independent of input casing.
            // Canonical "D": xxxxxxxx-xxxx-Vxxx-Nxxx-xxxxxxxxxxxx  (index 14 = version, index 19 = variant).
            var canonical = parsed.ToString("D");
            if (canonical[14] != '4')
            {
                return false; // RFC 4122 version must be 4 (matches the client-side uuidv4()).
            }

            // RFC 4122 variant (10xx) => the variant nibble is one of 8, 9, a, b (lowercase from ToString("D")).
            var variant = canonical[19];
            return variant == '8' || variant == '9' || variant == 'a' || variant == 'b';
        }
    }
}
