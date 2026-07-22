using Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Config
{
    public class InventoryReservationConfiguration:IEntityTypeConfiguration<InventoryReservation>
    {
        public void Configure(EntityTypeBuilder<InventoryReservation> builder)
        {
            // Flash-Sale feature (review finding F05): SessionId is the client basket UUID
            // (localStorage['basket_id'], a canonical 36-char UUID v4) reused as the session key — always
            // present, and bounded to 36 chars to match the API-layer UUID validation (ReserveInventoryDto)
            // and to prevent unbounded text storage / session-cardinality abuse.
            builder.Property(x => x.SessionId).IsRequired().HasMaxLength(36);

            // Flash-Sale feature (review finding C09): the reservation lifecycle
            // (Active -> Consumed | Released | Expired) is guarded by an optimistic-concurrency token on Status
            // itself. Marking Status as a concurrency token makes EF Core append "AND Status = @original" to
            // every UPDATE, so a conditional transition (e.g. Active -> Released in ReleaseAsync, Active ->
            // Consumed in ConsumeReservationsAsync, Active -> Expired in the sweep) affects zero rows when a
            // competing writer already moved the row off its expected state; EF then raises
            // DbUpdateConcurrencyException, which the services translate into a deterministic outcome. This is a
            // PROVIDER-AGNOSTIC guard expressed purely in the generated WHERE clause (no PostgreSQL xmin / system
            // column), so it behaves identically on PostgreSQL and on the relational test providers. It adds NO
            // column to the table — Status already exists — so the migration carries only the model annotation.
            builder.Property(x => x.Status).IsConcurrencyToken();

            // Flash-Sale feature (review finding C05 — deepest backstop): a reservation must never hold a
            // non-positive quantity. Service-boundary validation rejects quantity <= 0 before any write, but a
            // database CHECK constraint guarantees the invariant even against a future/direct writer, so a
            // stock-INFLATING negative-quantity hold can never be persisted. The column is quoted for
            // PostgreSQL; the constraint is emitted into the additive migration.
            builder.HasCheckConstraint("CK_InventoryReservations_Quantity_Positive", "\"Quantity\" > 0");

            // Flash-Sale feature (review finding M2 — database-integrity backstop): the persisted Status must
            // stay within the ReservationStatus enum domain (Active=0, Consumed=1, Released=2, Expired=3). A
            // direct/buggy writer could otherwise store an out-of-range Status that would silently vanish from
            // the availability aggregation (which filters on Active/Consumed) — a row "holding" stock that is
            // counted by nobody. This single-row CHECK guarantees the invariant at the deepest layer and is
            // emitted into the additive migration; keep its upper bound in step with the enum if values are added.
            builder.HasCheckConstraint("CK_InventoryReservations_Status_Valid", "\"Status\" >= 0 AND \"Status\" <= 3");

            // Flash-Sale feature (review finding M19): a reservation is a hold against a specific flash sale, so
            // configure the required FK InventoryReservation.FlashSaleId -> FlashSales.Id (scalar FK, no navigation
            // per repository convention). DeleteBehavior.Restrict is intentional: a sale that still has ANY
            // reservation rows (including durable Consumed/sold rows) cannot be deleted, preserving the sold-stock
            // history that the zero-oversell invariant depends on (AAP R3). The (FlashSaleId, Status) index below
            // leads with FlashSaleId, so it is a prefix match that backs this FK — EF reuses it and does NOT create
            // a duplicate standalone FlashSaleId FK index.
            builder.HasOne<FlashSale>()
                .WithMany()
                .HasForeignKey(x => x.FlashSaleId)
                .OnDelete(DeleteBehavior.Restrict);

            // Flash-Sale feature (review finding M19): the reservation also carries a denormalized ProductId, so
            // configure the required FK InventoryReservation.ProductId -> Products.Id explicitly (scalar FK, no
            // navigation). This rejects reservations that reference a non-existent product at the database level,
            // in addition to the transitive reservation -> sale -> product guarantee. DeleteBehavior.Restrict keeps
            // a product with live reservation history undeletable. The (ProductId, SessionId) index below leads with
            // ProductId, so it is a prefix match that backs THIS FK; EF therefore does NOT emit a separate standalone
            // ProductId FK-support index, keeping the reservation index set at exactly five (review finding m06).
            builder.HasOne<Product>()
                .WithMany()
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Restrict);

            // Flash-Sale feature (review finding C04/C11, m06): per-sale availability aggregation. Availability is
            // computed per FlashSaleId, filtered by Status (Active/Consumed rows hold stock), so lead the index
            // with (FlashSaleId, Status). Also backs the FlashSaleId FK above (FlashSaleId is the prefix column).
            builder.HasIndex(x => new { x.FlashSaleId, x.Status });
            // Flash-Sale feature (review findings M21/m06): the expiry sweep's hot predicate is
            // (Status == Active && ExpiresAt <= now). A composite (Status, ExpiresAt) index serves that predicate
            // directly; it REPLACES the former standalone ExpiresAt index (which did not cover the Status filter).
            builder.HasIndex(x => new { x.Status, x.ExpiresAt });
            // Flash-Sale feature (review finding m06, aligned with AAP 0.2.3): checkout-consume and session-scoped
            // lookups match reservations by product within a session. A ProductId-LEADING composite
            // (ProductId, SessionId) serves that predicate AND doubles as the backing index for the ProductId FK
            // above, so no separate standalone ProductId index is needed (keeps the set at five justified indexes).
            builder.HasIndex(x => new { x.ProductId, x.SessionId });
        }
    }
}
