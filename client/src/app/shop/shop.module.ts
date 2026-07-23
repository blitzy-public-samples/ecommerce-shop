import { NgModule } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ShopComponent } from './shop.component';
import { ProductItemComponent } from './product-item/product-item.component';
import {SharedModule} from '../shared/shared.module';
import { ProductDetailsComponent } from './product-details/product-details.component';
import {ShopRoutingModule} from "./shop-routing.module";
// Real-Time Inventory & Flash Sale - register the three new product-details widget components
import { FlashSaleBannerComponent } from './flash-sale-banner/flash-sale-banner.component';
import { CountdownTimerComponent } from './countdown-timer/countdown-timer.component';
import { LiveStockIndicatorComponent } from './live-stock-indicator/live-stock-indicator.component';



@NgModule({
  declarations: [
    ShopComponent,
    ProductItemComponent,
    ProductDetailsComponent,
    // Real-Time Inventory & Flash Sale - new product-details child widgets
    FlashSaleBannerComponent,
    CountdownTimerComponent,
    LiveStockIndicatorComponent
  ],
  imports: [
    CommonModule,
    SharedModule,
    ShopRoutingModule
  ],
})
export class ShopModule { }
