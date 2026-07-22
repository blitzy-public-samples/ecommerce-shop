import {Component, Input, OnChanges, OnInit, OnDestroy, SimpleChanges} from '@angular/core';
import {IProduct} from '../../shared/models/product';
import {BasketService} from "../../basket/basket.service";
import {Subscription} from 'rxjs';
import {HubConnectionState} from '@microsoft/signalr';
import {StockService} from '../../core/services/stock.service';

@Component({
  selector: 'app-product-item',
  templateUrl: './product-item.component.html',
  styleUrls: ['./product-item.component.scss']
})
export class ProductItemComponent implements OnInit, OnChanges, OnDestroy {
  @Input() product: IProduct;
  stock: number;
  // QA F6: server-authoritative low-stock threshold (seeded with the AAP default 5,
  // then kept in sync with the value the hub publishes).
  lowStockThreshold = 5;
  // QA F4/F12: live-stock hub link health; drives fail-closed gating + the indicator.
  stockConnected = false;
  private stockSub: Subscription;
  private thresholdSub: Subscription;
  private connectionSub: Subscription;
  private subscribedProductId: number;

  constructor(private basketService: BasketService, private stockService: StockService) { }

  ngOnInit(): void {
    // Seed the threshold synchronously, then track hub updates (QA F6) and the live-link
    // state (QA F4/F12) for the lifetime of the card. These app-wide subscriptions are
    // established once and torn down in ngOnDestroy.
    this.lowStockThreshold = this.stockService.lowStockThreshold;
    this.thresholdSub = this.stockService.lowStockThreshold$.subscribe(t => this.lowStockThreshold = t);
    this.connectionSub = this.stockService.connectionState$.subscribe(
      state => this.stockConnected = state === HubConnectionState.Connected);
    // Initial per-product bind. When the card is driven by a template @Input binding,
    // ngOnChanges fires before ngOnInit and will already have bound; the id guard in
    // bindStock() then makes this call a no-op, so there is never a double subscription.
    this.bindStock();
  }

  ngOnChanges(changes: SimpleChanges): void {
    // The catalog reuses ProductItem instances across list re-renders / paging, so the
    // bound product can change identity on an existing instance. Rebind live-stock
    // tracking to the new product — releasing the previous product's hub subscription
    // first — whenever the @Input product changes (P4-24). The id guard in bindStock()
    // ignores unrelated change notifications.
    if (changes.product) {
      this.bindStock();
    }
  }

  // Binds live-stock tracking to the currently-bound product. No-op when already bound to
  // that product id; otherwise releases any previous binding and subscribes to the new id.
  private bindStock(): void {
    const newId = this.product ? this.product.id : undefined;
    if (newId === this.subscribedProductId) {
      return;
    }
    this.releaseStockSubscription();
    this.stock = undefined;
    if (newId !== undefined) {
      this.subscribedProductId = newId;
      this.stockService.subscribeToProduct(newId);
      this.stockSub = this.stockService.getStock$(newId).subscribe(s => this.stock = s);
    }
  }

  // Tears down the current product's per-product live-stock subscription and releases the
  // per-product hub tracking for the product being left (P4-12 / P4-24).
  private releaseStockSubscription(): void {
    if (this.stockSub) {
      this.stockSub.unsubscribe();
      this.stockSub = undefined;
    }
    if (this.subscribedProductId !== undefined) {
      this.stockService.unsubscribeFromProduct(this.subscribedProductId);
      this.subscribedProductId = undefined;
    }
  }

  // QA F4/F12 (fail-closed): the add-to-cart control is only enabled when the live link is
  // up AND stock is known AND positive. Unknown stock or a dropped connection must never
  // present an enabled "add" affordance.
  get addToCartDisabled(): boolean {
    return !this.stockConnected || this.stock === undefined || this.stock === 0;
  }

  addItemToBasket() {
    // Defense-in-depth guard (QA H2 + F4): refuse a programmatic add when stock is
    // zero/unknown or the live link is down. Authoritative oversell prevention still lives
    // on the server (reservation + row lock); this only prevents an obviously-bad add.
    if (this.addToCartDisabled) { return; }
    this.basketService.addItemToBasket(this.product);
  }

  ngOnDestroy(): void {
    // Release the app-wide subscriptions and the per-product hub tracking so a destroyed
    // card leaves the server group and cannot leak subscriptions (QA F5 / P4-12).
    if (this.thresholdSub) {
      this.thresholdSub.unsubscribe();
    }
    if (this.connectionSub) {
      this.connectionSub.unsubscribe();
    }
    this.releaseStockSubscription();
  }
}
