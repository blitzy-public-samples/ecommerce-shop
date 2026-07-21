using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Core.Entities;

namespace Core.Interfaces
{
    public interface IFlashSaleService
    {
        // Create/schedule a flash sale. The service is a TRUST BOUNDARY (review finding M09): it independently
        // validates its inputs so a DIRECT caller (not only the DTO-validated controller) cannot persist an
        // invalid authority row, and it enforces per-product NON-OVERLAP transactionally (review finding C03) so
        // two sales for one product can never be active at the same instant and independently allocate stock.
        // The result carries a deterministic Outcome the API maps to an exact HTTP status (Success -> 201,
        // validation failures -> 400, ProductNotFound -> 404, Overlap -> 409) WITHOUT coupling Core to ASP.NET.
        // On Success the persisted FlashSale (with generated Id and default Version) is returned.
        Task<FlashSaleScheduleResult> ScheduleAsync(int productId, DateTimeOffset startAt, DateTimeOffset endAt,
            decimal salePrice, int stockAllocation);

        // Sales active now (now ∈ [StartAt, EndAt]) each paired with computed live availability. Availability is
        // SALE-SCOPED and accounts for BOTH durable sold units AND active, non-expired holds for the EXACT sale
        // authority (review findings C04, m09):
        //   QuantityAvailable = FlashSale.StockAllocation
        //                       - SUM(Quantity WHERE FlashSaleId = <that sale>
        //                             AND (Status = Consumed        -- durable sold units, never dropped
        //                                  OR (Status = Active AND ExpiresAt > now)))   -- live holds
        // clamped at zero. Implementations MUST NOT aggregate by ProductId (that would let overlapping/sequential
        // sales contaminate one another) and MUST NOT ignore Consumed (sold) quantity. Feeds the deliberately
        // NON-cached GET /api/flash-sales/active endpoint.
        Task<IReadOnlyList<ActiveFlashSale>> GetActiveSalesAsync();
    }

    // Deterministic outcome of a schedule attempt (review finding M09). The API translates each value into an
    // exact HTTP response; Core stays free of any ASP.NET dependency.
    public enum FlashSaleScheduleOutcome
    {
        Success,
        // The referenced product does not exist -> 404. The service establishes product existence rather than
        // trusting a foreign-key failure to surface later.
        ProductNotFound,
        // StartAt/EndAt are default or not a strictly-forward interval (EndAt <= StartAt) -> 400.
        InvalidWindow,
        // StockAllocation <= 0 -> 400.
        InvalidAllocation,
        // SalePrice <= 0 -> 400.
        InvalidSalePrice,
        // SalePrice is not strictly below the product's base price -> 400 (a "discount" must actually discount).
        SalePriceNotBelowBasePrice,
        // Another sale for the same product overlaps the requested window -> 409 (review finding C03).
        Overlap
    }

    // Plain result the API translates into exact HTTP responses without coupling Core to ASP.NET.
    public class FlashSaleScheduleResult
    {
        public FlashSaleScheduleOutcome Outcome { get; set; }
        public FlashSale FlashSale { get; set; } // populated on Success only.
    }

    // Plain result: an active sale plus its computed live availability. Availability is SALE-SCOPED — allocation
    // minus all durable sold units (Consumed) AND active, non-expired holds for THAT FlashSale (review findings
    // C04, m09). Mapped to FlashSaleDto in the API layer.
    public class ActiveFlashSale
    {
        public FlashSale Sale { get; set; }
        public int QuantityAvailable { get; set; }
    }
}
