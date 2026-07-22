import { Component, OnDestroy, OnInit } from '@angular/core';
import {FormBuilder, FormGroup, Validators} from "@angular/forms";
import {AccountService} from "../account/account.service";
import {Observable, Subscription} from 'rxjs';
import {IBasket, IBasketTotals} from "../shared/models/basket";
import {BasketService} from "../basket/basket.service";
import { StockService } from '../core/services/stock.service';
import { HubConnectionState } from '@microsoft/signalr';

@Component({
  selector: 'app-checkout',
  templateUrl: './checkout.component.html',
  styleUrls: ['./checkout.component.scss']
})
export class CheckoutComponent implements OnInit, OnDestroy {
  basketTotals$: Observable<IBasketTotals>;
  checkoutForm: FormGroup;
  hasOutOfStockItem = false;
  hasInsufficientStockItem = false;
  stockConnected = false;
  private stockMap = new Map<number, number>();
  private quantityMap = new Map<number, number>();
  private trackedProductIds = new Set<number>();
  private subscriptions: Subscription[] = [];

  constructor(private fb: FormBuilder, private accountService: AccountService,
              private basketService: BasketService, private stockService: StockService) { }

  /**
   * Single fail-closed signal the payment step binds to. Order submission is
   * blocked when any basket line is out of stock, when any line requests more
   * units than are currently available (QA F9), or while the live-stock hub is
   * not connected (QA F12) — because with no live feed the app cannot trust that
   * stock is still available (fail-closed, mirroring the basket "proceed" gate).
   */
  get submitDisabledForStock(): boolean {
    return this.hasOutOfStockItem || this.hasInsufficientStockItem || !this.stockConnected;
  }

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
    // Observe the hub connection state first so the fail-closed gate (QA F12) is
    // accurate before the first basket emission is processed. Without a live feed
    // the checkout cannot trust that stock is still available, so submission is
    // paused until the connection is (re)established.
    const connectionSub = this.stockService.connectionState$.subscribe(state => {
      this.stockConnected = state === HubConnectionState.Connected;
      this.recomputeGate();
    });
    this.subscriptions.push(connectionSub);

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
        this.quantityMap.delete(id);
        this.stockService.unsubscribeFromProduct(id);
      }
    });

    if (basket && basket.items) {
      basket.items.forEach(item => {
        // Record the requested quantity per line so the gate can detect the
        // "insufficient stock" case (available < requested), not only zero (QA F9).
        this.quantityMap.set(item.id, item.quantity);
        this.trackProductStock(item.id);
      });
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
    const ids = Array.from(this.trackedProductIds);
    this.hasOutOfStockItem = ids.some(id => this.stockMap.get(id) === 0);
    // Insufficient = some units are available, but fewer than the line requested.
    // A zero available count is reported as out-of-stock (above), never here.
    this.hasInsufficientStockItem = ids.some(id => {
      const stock = this.stockMap.get(id);
      const quantity = this.quantityMap.get(id);
      return stock !== undefined && quantity !== undefined && stock > 0 && stock < quantity;
    });
  }

  ngOnDestroy(): void {
    // Release every still-tracked product's hub subscription so the reference
    // count does not grow across checkout visits (QA F5 / eviction invariant).
    Array.from(this.trackedProductIds).forEach(id => this.stockService.unsubscribeFromProduct(id));
    this.trackedProductIds.clear();
    this.stockMap.clear();
    this.quantityMap.clear();
    this.subscriptions.forEach(subscription => subscription.unsubscribe());
    this.subscriptions = [];
  }
}
