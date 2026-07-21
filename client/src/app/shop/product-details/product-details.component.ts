import { Component, OnInit, OnDestroy } from '@angular/core';
import {IProduct} from '../../shared/models/product';
import {ShopService} from "../shop.service";
import {ActivatedRoute} from "@angular/router";
import {BreadcrumbService} from "xng-breadcrumb";
import {BasketService} from "../../basket/basket.service";
import {Subscription} from 'rxjs';
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
  private stockSub: Subscription;
  private routeSub: Subscription;
  private subscribedProductId: number;

  constructor(private shopService: ShopService, private activateRoute: ActivatedRoute,
              private bcService: BreadcrumbService, private basketService: BasketService,
              private stockService: StockService) {
    this.bcService.set('@productDetails', ' ');
  }

  ngOnInit(): void {
    // Resolve the id from the paramMap OBSERVABLE (not a one-shot snapshot) so a
    // same-component route reuse — navigating between products without leaving the
    // details view — rebinds live-stock tracking to the new product (QA R2).
    this.routeSub = this.activateRoute.paramMap.subscribe(params => {
      this.loadProduct(+params.get('id'));
    });
  }
  addItemToBasket() {
    // Defense-in-depth guard (QA H2): refuse a programmatic add at zero stock and
    // never enqueue more than the currently-known available stock. The template
    // already disables the button at zero; authoritative oversell prevention still
    // lives on the server (reservation + row lock).
    if (this.stock === 0) { return; }
    const quantityToAdd = this.stock !== undefined ? Math.min(this.quantity, this.stock) : this.quantity;
    this.basketService.addItemToBasket(this.product, quantityToAdd);
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
      this.stockSub = this.stockService.getStock$(product.id).subscribe(s => this.stock = s);
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
    this.releaseStockSubscription();
  }
}
