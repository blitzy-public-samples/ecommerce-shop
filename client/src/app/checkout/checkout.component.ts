import { Component, OnDestroy, OnInit } from '@angular/core';
import {FormBuilder, FormGroup, Validators} from "@angular/forms";
import {AccountService} from "../account/account.service";
import {Observable, Subscription} from "rxjs";
import {IBasket, IBasketTotals} from "../shared/models/basket";
import {BasketService} from "../basket/basket.service";
import { StockService } from '../core/services/stock.service';

@Component({
  selector: 'app-checkout',
  templateUrl: './checkout.component.html',
  styleUrls: ['./checkout.component.scss']
})
export class CheckoutComponent implements OnInit, OnDestroy {
  basketTotals$: Observable<IBasketTotals>;
  checkoutForm: FormGroup;
  hasOutOfStockItem = false;
  private stockMap = new Map<number, number>();
  private trackedProductIds = new Set<number>();
  private subscriptions: Subscription[] = [];

  constructor(private fb: FormBuilder, private accountService: AccountService,
              private basketService: BasketService, private stockService: StockService) { }

  ngOnInit(): void {
    this.createCheckoutForm();
    this.getAddressFormValues();
    this.getDeliveryMethodValue();
    this.basketTotals$ = this.basketService.basketTotal$;
    this.trackBasketStock();
  }
  createCheckoutForm() {
    this.checkoutForm = this.fb.group({
      addressForm: this.fb.group({
        firstName: [null, Validators.required],
        lastName: [null, Validators.required],
        street: [null, Validators.required],
        city: [null, Validators.required],
        state: [null, Validators.required],
        zipCode: [null, Validators.required]
      }),
      deliveryForm: this.fb.group({
        deliveryMethod: [null, Validators.required]
      }),
      paymentForm: this.fb.group({
        nameOnCard: [null, Validators.required]
      })
    });
  }
  getAddressFormValues() {
    this.accountService.getUserAddress().subscribe(address => {
      if (address) {
        this.checkoutForm.get('addressForm').patchValue(address);
      }
    }, error => {
      console.log(error);
    });
  }
  getDeliveryMethodValue(){
    const basket = this.basketService.getCurrentBasketValue();
    if (basket.deliveryMethodId != null){
      this.checkoutForm.get('deliveryForm').get('deliveryMethod').patchValue(basket.deliveryMethodId.toString());
    }
  }

  private trackBasketStock(): void {
    const basketSub = this.basketService.basket$.subscribe(basket => this.syncBasketStock(basket));
    this.subscriptions.push(basketSub);
  }

  /**
   * Reconciles live-stock tracking with the CURRENT basket on every basket$ emit.
   *
   * Fix for the mid-session submit-gate latch (QA H1): the gate was recomputed
   * only inside the per-product stock callback, so a removed/emptied product's
   * stale `0` kept it stuck true and blocked the AAP recovery flow ("return to
   * your basket and remove it"). Here we prune products that have left the basket
   * — releasing their hub subscription (R1) — and recompute the gate from the
   * current basket on every emit.
   */
  private syncBasketStock(basket: IBasket): void {
    const currentIds = new Set<number>((basket && basket.items ? basket.items : []).map(item => item.id));

    Array.from(this.trackedProductIds).forEach(id => {
      if (!currentIds.has(id)) {
        this.trackedProductIds.delete(id);
        this.stockMap.delete(id);
        this.stockService.unsubscribeFromProduct(id);
      }
    });

    if (basket && basket.items) {
      basket.items.forEach(item => this.trackProductStock(item.id));
    }

    this.recomputeGate();
  }

  private trackProductStock(productId: number): void {
    if (this.trackedProductIds.has(productId)) {
      return;
    }
    this.trackedProductIds.add(productId);
    this.stockService.subscribeToProduct(productId);
    const stockSub = this.stockService.getStock$(productId).subscribe(stock => {
      // Ignore a late emission for a product that has since left the basket.
      if (!this.trackedProductIds.has(productId)) { return; }
      this.stockMap.set(productId, stock);
      this.recomputeGate();
    });
    this.subscriptions.push(stockSub);
  }

  /**
   * The gate reflects only products STILL in the basket: true when any
   * currently-tracked product's latest known stock is exactly zero.
   */
  private recomputeGate(): void {
    this.hasOutOfStockItem = Array.from(this.trackedProductIds).some(id => this.stockMap.get(id) === 0);
  }

  ngOnDestroy(): void {
    this.subscriptions.forEach(subscription => subscription.unsubscribe());
  }
}
