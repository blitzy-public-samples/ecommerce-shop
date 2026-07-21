import {Component, OnDestroy, OnInit} from '@angular/core';
import {Observable, Subscription} from "rxjs";
import {IBasket, IBasketItem, IBasketTotals} from "../shared/models/basket";
import {BasketService} from "./basket.service";
import {StockService} from "../core/services/stock.service";

@Component({
  selector: 'app-basket',
  templateUrl: './basket.component.html',
  styleUrls: ['./basket.component.scss']
})
export class BasketComponent implements OnInit, OnDestroy {
  basket$: Observable<IBasket>;
  basketTotals$: Observable<IBasketTotals>;
  hasOutOfStockItem = false;
  private stockMap = new Map<number, number>();
  private trackedProductIds = new Set<number>();
  private subscriptions: Subscription[] = [];

  constructor(private basketService: BasketService, private stockService: StockService) {
  }

  ngOnInit(): void {
    this.basket$ = this.basketService.basket$;
    this.basketTotals$ = this.basketService.basketTotal$;

    this.subscriptions.push(
      this.basketService.basket$.subscribe(basket => this.syncBasketStock(basket))
    );
  }

  /**
   * Reconciles live-stock tracking with the CURRENT basket on every basket$ emit.
   *
   * This is the fix for the mid-session checkout-gate latch (QA H1): recomputing
   * the gate only inside the per-product stock callback meant that once a product
   * hit zero its stale `0` stayed in the map forever, so the gate never cleared
   * after the shopper removed the item or emptied the basket. Here we (1) prune
   * tracking for any product that has left the basket — releasing its hub
   * subscription (R1) — and (2) recompute the gate from the current basket, so the
   * AAP recovery flow ("remove the zero-stock item and continue") works.
   */
  private syncBasketStock(basket: IBasket): void {
    const currentIds = new Set<number>((basket && basket.items ? basket.items : []).map(item => item.id));

    // Prune products no longer in the basket so a departed item's stale zero can
    // no longer keep the checkout gate latched, and release their hub tracking.
    Array.from(this.trackedProductIds).forEach(id => {
      if (!currentIds.has(id)) {
        this.trackedProductIds.delete(id);
        this.stockMap.delete(id);
        this.stockService.unsubscribeFromProduct(id);
      }
    });

    if (basket && basket.items) {
      basket.items.forEach(item => this.trackItemStock(item));
    }

    // Recompute from the current basket on EVERY emit (not only when a per-product
    // stock value arrives), so removals and empties clear the gate.
    this.recomputeGate();
  }

  private trackItemStock(item: IBasketItem): void {
    if (this.trackedProductIds.has(item.id)) { return; }
    this.trackedProductIds.add(item.id);
    this.stockService.subscribeToProduct(item.id);
    this.subscriptions.push(
      this.stockService.getStock$(item.id).subscribe(stock => {
        // Ignore a late emission for a product that has since left the basket so a
        // stale value cannot re-latch the gate.
        if (!this.trackedProductIds.has(item.id)) { return; }
        this.stockMap.set(item.id, stock);
        this.recomputeGate();
      })
    );
  }

  /**
   * The gate reflects only products STILL in the basket: it is true when any
   * currently-tracked product's latest known stock is exactly zero.
   */
  private recomputeGate(): void {
    this.hasOutOfStockItem = Array.from(this.trackedProductIds).some(id => this.stockMap.get(id) === 0);
  }

  ngOnDestroy(): void {
    this.subscriptions.forEach(s => s.unsubscribe());
  }

  removeBasketItem(item: IBasketItem) {
    this.basketService.removeItemFromBasket(item);
  }

  incrementItemQuantity(item: IBasketItem) {
    this.basketService.incrementItemQuantity(item);
  }

  decrementItemQuantity(item: IBasketItem) {
    this.basketService.decrementItemQuantity(item);
  }
}
