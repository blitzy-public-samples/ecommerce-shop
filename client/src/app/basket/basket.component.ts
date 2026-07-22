import {Component, OnDestroy, OnInit} from '@angular/core';
import {Router} from '@angular/router';
import {Observable, Subscription} from 'rxjs';
import {HubConnectionState} from '@microsoft/signalr';
import {IBasket, IBasketItem, IBasketTotals} from "../shared/models/basket";
import {BasketService} from "./basket.service";
import {StockService} from '../core/services/stock.service';

@Component({
  selector: 'app-basket',
  templateUrl: './basket.component.html',
  styleUrls: ['./basket.component.scss']
})
export class BasketComponent implements OnInit, OnDestroy {
  basket$: Observable<IBasket>;
  basketTotals$: Observable<IBasketTotals>;
  // Any basket line whose latest known live stock is exactly zero.
  hasOutOfStockItem = false;
  // QA F9: any basket line whose known live stock is positive but below the
  // requested quantity (e.g. quantity 2 but only 1 left).
  hasInsufficientStockItem = false;
  // QA F12: whether the live-stock hub is currently connected. Defaults to false
  // so the proceed action is fail-closed until the socket reports Connected.
  stockConnected = false;
  private stockMap = new Map<number, number>();
  // Latest requested quantity per product id, used by the F9 insufficient-stock
  // check. Kept in sync with the current basket on every basket$ emission.
  private quantityMap = new Map<number, number>();
  private trackedProductIds = new Set<number>();
  private subscriptions: Subscription[] = [];

  constructor(
    private basketService: BasketService,
    private stockService: StockService,
    private router: Router) {
  }

  ngOnInit(): void {
    this.basket$ = this.basketService.basket$;
    this.basketTotals$ = this.basketService.basketTotal$;

    // QA F12: reflect the live hub connection state so the UI can announce
    // unavailability and fail closed while the socket is down/reconnecting. The
    // service's connectionState$ is a BehaviorSubject, so this emits the current
    // state synchronously on subscribe.
    this.subscriptions.push(
      this.stockService.connectionState$.subscribe(state => {
        this.stockConnected = state === HubConnectionState.Connected;
        this.recomputeGate();
      })
    );

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
    const items = (basket && basket.items) ? basket.items : [];
    const currentIds = new Set<number>(items.map(item => item.id));

    // Prune products no longer in the basket so a departed item's stale zero can
    // no longer keep the checkout gate latched, and release their hub tracking.
    Array.from(this.trackedProductIds).forEach(id => {
      if (!currentIds.has(id)) {
        this.trackedProductIds.delete(id);
        this.stockMap.delete(id);
        this.quantityMap.delete(id);
        this.stockService.unsubscribeFromProduct(id);
      }
    });

    // Keep the requested quantity current for EVERY item on EVERY emit (quantities
    // change on increment/decrement without re-subscribing), then ensure a live
    // stock subscription exists for the product.
    items.forEach(item => {
      this.quantityMap.set(item.id, item.quantity);
      this.trackItemStock(item);
    });

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
   * Recomputes both gate conditions from the products STILL in the basket:
   * - hasOutOfStockItem: any tracked product's latest known stock is exactly zero.
   * - hasInsufficientStockItem (QA F9): any tracked product has a KNOWN positive
   *   stock that is below the requested basket quantity.
   */
  private recomputeGate(): void {
    const ids = Array.from(this.trackedProductIds);
    this.hasOutOfStockItem = ids.some(id => this.stockMap.get(id) === 0);
    this.hasInsufficientStockItem = ids.some(id => {
      const stock = this.stockMap.get(id);
      const qty = this.quantityMap.get(id);
      return stock !== undefined && qty !== undefined && stock > 0 && stock < qty;
    });
  }

  /**
   * QA H-C + F9 + F12: proceeding to checkout is blocked whenever any basket line
   * is out of stock, is below the requested quantity, or the authoritative live
   * stock status is unavailable (hub not Connected). The template renders this as
   * a real disabled <button>, so mouse, touch AND keyboard are all inert — unlike
   * the previous aria-disabled anchor, which stayed keyboard-activatable (the H-C
   * bypass). Direct URL / back-forward navigation is additionally blocked by the
   * /checkout StockGuard.
   */
  get proceedDisabled(): boolean {
    return this.hasOutOfStockItem || this.hasInsufficientStockItem || !this.stockConnected;
  }

  proceedToCheckout(): void {
    if (this.proceedDisabled) { return; }
    this.router.navigate(['/checkout']);
  }

  ngOnDestroy(): void {
    // QA F5: release EVERY still-tracked product's hub subscription so the
    // service's reference count is decremented and the eviction invariant holds —
    // not just the local RxJS subscriptions. Without this the ref-count grew
    // monotonically on every basket visit.
    this.trackedProductIds.forEach(id => this.stockService.unsubscribeFromProduct(id));
    this.trackedProductIds.clear();
    this.subscriptions.forEach(s => s.unsubscribe());
    // Release the hub tracking for every product still tracked at teardown so the
    // component leaves the per-product server groups and cannot leak subscriptions
    // when the shopper navigates away with items still in the basket (P4-12).
    this.trackedProductIds.forEach(id => this.stockService.unsubscribeFromProduct(id));
    this.trackedProductIds.clear();
    this.stockMap.clear();
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
