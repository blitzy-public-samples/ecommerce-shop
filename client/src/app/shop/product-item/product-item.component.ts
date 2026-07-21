import {Component, Input, OnInit, OnDestroy} from '@angular/core';
import {IProduct} from '../../shared/models/product';
import {BasketService} from "../../basket/basket.service";
import {Subscription} from 'rxjs';
import {StockService} from '../../core/services/stock.service';

@Component({
  selector: 'app-product-item',
  templateUrl: './product-item.component.html',
  styleUrls: ['./product-item.component.scss']
})
export class ProductItemComponent implements OnInit, OnDestroy {
  @Input() product: IProduct;
  stock: number;
  private stockSub: Subscription;

  constructor(private basketService: BasketService, private stockService: StockService) { }

  ngOnInit(): void {
    this.stockService.subscribeToProduct(this.product.id);
    this.stockSub = this.stockService.getStock$(this.product.id).subscribe(s => this.stock = s);
  }

  ngOnDestroy(): void {
    if (this.stockSub) {
      this.stockSub.unsubscribe();
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
