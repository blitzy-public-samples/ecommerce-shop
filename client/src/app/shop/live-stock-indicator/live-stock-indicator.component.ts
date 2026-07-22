import { Component, Input } from '@angular/core';

/**
 * Live per-product stock indicator.
 *
 * Real-Time Inventory & Flash Sale feature widget. Displays the currently available quantity and
 * updates live from the SignalR `InventoryUpdated` stream. All display state derives from a single
 * normalised value so a missing/invalid quantity can never masquerade as a healthy "in stock" state
 * (review finding m04).
 *
 * N5: display state is fully derived from getters over the @Input bindings, so the empty constructor
 * and empty ngOnInit/OnInit boilerplate were removed (no initialisation logic exists).
 */
@Component({
  selector: 'app-live-stock-indicator',
  templateUrl: './live-stock-indicator.component.html',
  styleUrls: ['./live-stock-indicator.component.scss']
})
export class LiveStockIndicatorComponent {
  /** Raw available quantity supplied by the host / hub stream. May be undefined until first update. */
  @Input() quantityAvailable: number;

  /** At or below this count the indicator switches to the low-stock (danger) treatment. */
  @Input() lowStockThreshold = 5;

  /**
   * m04: normalise the raw input to a finite, non-negative integer, or `null` when the value is
   * unavailable (undefined/null/NaN/Infinity/negative). Every other getter and the template derive
   * from this single source of truth, so non-finite or negative inputs resolve to an explicit
   * "unavailable" state rather than leaking through as an empty/green "in stock" message.
   */
  get normalizedQuantity(): number | null {
    const q = this.quantityAvailable;
    if (q === null || q === undefined || !Number.isFinite(q) || q < 0) {
      return null;
    }
    return Math.floor(q);
  }

  /** m04: explicit unavailable state when the quantity cannot be interpreted as a valid count. */
  get isUnavailable(): boolean {
    return this.normalizedQuantity === null;
  }

  get isOutOfStock(): boolean {
    return this.normalizedQuantity === 0;
  }

  get isLowStock(): boolean {
    const q = this.normalizedQuantity;
    return q !== null && q > 0 && q <= this.lowStockThreshold;
  }

  /** Healthy stock: a valid quantity strictly above the low-stock threshold. */
  get isInStock(): boolean {
    const q = this.normalizedQuantity;
    return q !== null && q > this.lowStockThreshold;
  }
}
