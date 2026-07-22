import { Component, OnDestroy, OnInit } from '@angular/core';
import {IProduct} from '../../shared/models/product';
import {ShopService} from "../shop.service";
import {ActivatedRoute} from "@angular/router";
import {BreadcrumbService} from "xng-breadcrumb";
import {BasketService} from "../../basket/basket.service";
// Real-Time Inventory & Flash Sale - hub + flash-sale wiring for the product-details widgets
import { Subscription } from 'rxjs';
import { InventoryHubService } from '../../core/services/inventory-hub.service';
import { FlashSaleService } from '../flash-sale.service';
import { IFlashSale } from '../../shared/models/flash-sale';
import { IInventoryUpdate } from '../../shared/models/inventory';

@Component({
  selector: 'app-product-details',
  templateUrl: './product-details.component.html',
  styleUrls: ['./product-details.component.scss']
})
export class ProductDetailsComponent implements OnInit, OnDestroy {
  product: IProduct;
  quantity = 1;
  // Real-Time Inventory & Flash Sale - live sale/stock state consumed by the child widgets
  activeFlashSale: IFlashSale;
  quantityAvailable: number;
  private productId: number;
  private hubSubscriptions = new Subscription();

  constructor(private shopService: ShopService, private activateRoute: ActivatedRoute,
              private bcService: BreadcrumbService, private basketService: BasketService,
              // Real-Time Inventory & Flash Sale - new services (existing DI preserved)
              private inventoryHubService: InventoryHubService, private flashSaleService: FlashSaleService) {
    this.bcService.set('@productDetails', ' ');
  }

  ngOnInit(): void {
    this.loadProduct();
    // Real-Time Inventory & Flash Sale - resolve product id, load active sale, wire hub streams
    this.productId = +this.activateRoute.snapshot.paramMap.get('id');
    this.loadActiveFlashSale();
    this.initInventoryHub();
  }
  addItemToBasket() {
    this.basketService.addItemToBasket(this.product, this.quantity);
  }
  incrementQuantity() {
    this.quantity++;
  }
  decrementQuantity() {
    if (this.quantity > 1){
      this.quantity--;
    }
  }
  loadProduct() {
    this.shopService.getProduct(+this.activateRoute.snapshot.paramMap.get('id')).subscribe(product => {
      this.product = product;
      this.bcService.set('@productDetails', product.name);
    }, error => {
      console.log(error);
    });
  }

  // Real-Time Inventory & Flash Sale - fetch the current active sale (if any) for this product
  loadActiveFlashSale(): void {
    this.flashSaleService.getActiveFlashSale(this.productId).subscribe(sale => {
      this.activeFlashSale = sale;
      if (sale) {
        this.quantityAvailable = sale.quantityAvailable;
      }
    }, error => {
      console.log(error);
    });
  }

  // Real-Time Inventory & Flash Sale - start hub, join product group, subscribe to live streams
  initInventoryHub(): void {
    this.inventoryHubService.start().then(() => this.inventoryHubService.joinProductGroup(this.productId));
    this.hubSubscriptions.add(
      this.inventoryHubService.inventoryUpdated$.subscribe((update: IInventoryUpdate) => {
        if (update && update.productId === this.productId) {
          this.quantityAvailable = update.quantityAvailable;
        }
      })
    );
    this.hubSubscriptions.add(
      this.inventoryHubService.flashSaleStarted$.subscribe((sale: IFlashSale) => {
        if (sale && sale.productId === this.productId) {
          this.activeFlashSale = sale;
          this.quantityAvailable = sale.quantityAvailable;
        }
      })
    );
    this.hubSubscriptions.add(
      this.inventoryHubService.flashSaleEnded$.subscribe((sale: IFlashSale) => {
        if (sale && sale.productId === this.productId) {
          this.activeFlashSale = undefined;
        }
      })
    );
  }

  // Real-Time Inventory & Flash Sale - clean up hub subscriptions and connection on destroy
  ngOnDestroy(): void {
    this.hubSubscriptions.unsubscribe();
    this.inventoryHubService.leaveProductGroup(this.productId);
    this.inventoryHubService.stop();
  }
}
