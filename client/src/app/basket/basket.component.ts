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
      this.basketService.basket$.subscribe(basket => {
        if (!basket || !basket.items) { return; }
        basket.items.forEach(item => this.trackItemStock(item));
      })
    );
  }

  private trackItemStock(item: IBasketItem): void {
    if (this.trackedProductIds.has(item.id)) { return; }
    this.trackedProductIds.add(item.id);
    this.stockService.subscribeToProduct(item.id);
    this.subscriptions.push(
      this.stockService.getStock$(item.id).subscribe(stock => {
        this.stockMap.set(item.id, stock);
        this.hasOutOfStockItem = Array.from(this.stockMap.values()).some(v => v === 0);
      })
    );
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
