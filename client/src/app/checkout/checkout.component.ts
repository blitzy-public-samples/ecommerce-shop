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
    const basketSub = this.basketService.basket$.subscribe(basket => {
      if (basket && basket.items) {
        basket.items.forEach(item => this.trackProductStock(item.id));
      }
    });
    this.subscriptions.push(basketSub);
  }

  private trackProductStock(productId: number): void {
    if (this.trackedProductIds.has(productId)) {
      return;
    }
    this.trackedProductIds.add(productId);
    this.stockService.subscribeToProduct(productId);
    const stockSub = this.stockService.getStock$(productId).subscribe(stock => {
      this.stockMap.set(productId, stock);
      this.hasOutOfStockItem = Array.from(this.stockMap.values()).some(value => value === 0);
    });
    this.subscriptions.push(stockSub);
  }

  ngOnDestroy(): void {
    this.subscriptions.forEach(subscription => subscription.unsubscribe());
  }
}
