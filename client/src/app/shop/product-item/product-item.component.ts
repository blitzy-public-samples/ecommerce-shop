import {Component, Input, OnChanges, OnInit, OnDestroy, SimpleChanges} from '@angular/core';
import {IProduct} from '../../shared/models/product';
import {BasketService} from "../../basket/basket.service";
import {Subscription} from 'rxjs';
import {StockService} from '../../core/services/stock.service';

@Component({
  selector: 'app-product-item',
  templateUrl: './product-item.component.html',
  styleUrls: ['./product-item.component.scss']
})
export class ProductItemComponent implements OnInit, OnChanges, OnDestroy {
  @Input() product: IProduct;
  stock: number;
  private stockSub: Subscription;
  private subscribedProductId: number;

  constructor(private basketService: BasketService, private stockService: StockService) { }

  ngOnInit(): void {
    // Initial bind. When this component is driven by a template @Input binding, ngOnChanges
    // fires first with the initial product and will already have bound; the id guard in
    // bindStock() then makes this call a no-op, so there is never a double subscription.
    this.bindStock();
  }

  ngOnChanges(changes: SimpleChanges): void {
    // The catalog reuses ProductItem instances (e.g. list re-render / paging), so the bound
    // product can change identity on an existing instance. Rebind live-stock tracking to the
    // new product — releasing the previous product's hub subscription first — whenever the
    // @Input product changes (P4-24). The id guard in bindStock() ignores unrelated changes.
    if (changes.product) {
      this.bindStock();
    }
  }

  ngOnDestroy(): void {
    // Release BOTH the local Rx subscription and the per-product hub tracking so a destroyed
    // card leaves the server group and cannot leak subscriptions (P4-12).
    this.releaseStockSubscription();
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

  // Tears down the current product's live-stock subscription: unsubscribes the local Rx
  // stream and releases the per-product hub tracking for the product being left (P4-12/P4-24).
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

  addItemToBasket() {
    // Defense-in-depth guard (QA H2): the template already disables the button at
    // zero stock, but the method must also refuse a programmatic add so no code
    // path can enqueue an out-of-stock product. Authoritative oversell prevention
    // still lives on the server (reservation + row lock).
    if (this.stock === 0) { return; }
    this.basketService.addItemToBasket(this.product);
  }
}
