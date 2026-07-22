import { Component, OnInit, OnDestroy } from '@angular/core';
import {IProduct} from '../../shared/models/product';
import {ShopService} from "../shop.service";
import {ActivatedRoute} from "@angular/router";
import {BreadcrumbService} from "xng-breadcrumb";
import {BasketService} from "../../basket/basket.service";
import {Subscription} from 'rxjs';
import {HubConnectionState} from '@microsoft/signalr';
import {StockService} from '../../core/services/stock.service';

@Component({
  selector: 'app-product-details',
  templateUrl: './product-details.component.html',
  styleUrls: ['./product-details.component.scss']
})
export class ProductDetailsComponent implements OnInit, OnDestroy {
  product: IProduct;
  quantity = 1;
  stock: number;
  // QA F6: server-authoritative low-stock threshold (seeded with the AAP default 5).
  lowStockThreshold = 5;
  // QA F4/F12: live-stock hub link health; drives fail-closed gating + indicator.
  stockConnected = false;
  private stockSub: Subscription;
  private routeSub: Subscription;
  private thresholdSub: Subscription;
  private connectionSub: Subscription;
  private subscribedProductId: number;

  constructor(private shopService: ShopService, private activateRoute: ActivatedRoute,
              private bcService: BreadcrumbService, private basketService: BasketService,
              private stockService: StockService) {
    this.bcService.set('@productDetails', ' ');
  }

  ngOnInit(): void {
    // Seed the threshold synchronously, then track hub updates (QA F6) and the
    // live-link state (QA F4/F12) for the lifetime of the view.
    this.lowStockThreshold = this.stockService.lowStockThreshold;
    this.thresholdSub = this.stockService.lowStockThreshold$.subscribe(t => this.lowStockThreshold = t);
    this.connectionSub = this.stockService.connectionState$.subscribe(
      state => this.stockConnected = state === HubConnectionState.Connected);
    // Resolve the id from the paramMap OBSERVABLE (not a one-shot snapshot) so a
    // same-component route reuse — navigating between products without leaving the
    // details view — rebinds live-stock tracking to the new product (QA R2).
    this.routeSub = this.activateRoute.paramMap.subscribe(params => {
      this.loadProduct(+params.get('id'));
    });
  }
  addItemToBasket() {
    // Defense-in-depth guard (QA H2 + F4): refuse a programmatic add when stock is
    // zero/unknown or the live link is down, and never enqueue more than the
    // currently-known available stock. The template also disables the button;
    // authoritative oversell prevention still lives on the server (row lock).
    if (this.addToCartDisabled) { return; }
    const quantityToAdd = this.stock !== undefined ? Math.min(this.quantity, this.stock) : this.quantity;
    this.basketService.addItemToBasket(this.product, quantityToAdd);
  }

  // QA F4 (fail-closed): enable "Add to Cart" only when the live link is up AND
  // stock is known-positive; a dropped connection or unknown stock disables it.
  get addToCartDisabled(): boolean {
    return !this.stockConnected || this.stock === undefined || this.stock === 0;
  }
  incrementQuantity() {
    // Do not let the requested quantity exceed the known available stock (QA H2).
    // While stock is unknown (undefined) the control is unbounded as before.
    if (this.stock === undefined || this.quantity < this.stock) {
      this.quantity++;
    }
  }
  decrementQuantity() {
    if (this.quantity > 1){
      this.quantity--;
    }
  }
  loadProduct(id: number) {
    // Release any previously-bound product's live-stock subscription before
    // rebinding, so route reuse cannot leak subscriptions or show a stale badge.
    this.releaseStockSubscription();
    this.stock = undefined;
    this.quantity = 1;
    this.shopService.getProduct(id).subscribe(product => {
      this.product = product;
      this.bcService.set('@productDetails', product.name);
      this.subscribedProductId = product.id;
      this.stockService.subscribeToProduct(product.id);
      this.stockSub = this.stockService.getStock$(product.id).subscribe(s => {
        this.stock = s;
        // QA INF-2: if live stock drops below the currently-selected quantity,
        // clamp the displayed quantity down so the shopper never sees a request
        // quantity that exceeds availability. Never clamp below 1, and leave the
        // quantity untouched while stock is unknown (undefined).
        if (this.stock !== undefined && this.stock > 0 && this.quantity > this.stock) {
          this.quantity = this.stock;
        }
      });
    }, error => {
      console.log(error);
    });
  }
  private releaseStockSubscription(): void {
    if (this.stockSub) {
      this.stockSub.unsubscribe();
      this.stockSub = undefined;
    }
    if (this.subscribedProductId !== undefined) {
      // Release the per-product hub tracking for the product we are leaving (R1/R2).
      this.stockService.unsubscribeFromProduct(this.subscribedProductId);
      this.subscribedProductId = undefined;
    }
  }
  ngOnDestroy(): void {
    if (this.routeSub) {
      this.routeSub.unsubscribe();
    }
    if (this.thresholdSub) {
      this.thresholdSub.unsubscribe();
    }
    if (this.connectionSub) {
      this.connectionSub.unsubscribe();
    }
    this.releaseStockSubscription();
  }
}
