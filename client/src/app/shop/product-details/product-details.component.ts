import { Component, OnDestroy, OnInit } from '@angular/core';
import {IProduct} from '../../shared/models/product';
import {ShopService} from "../shop.service";
import {ActivatedRoute} from "@angular/router";
import {BreadcrumbService} from "xng-breadcrumb";
import {BasketService} from "../../basket/basket.service";
// Real-Time Inventory & Flash Sale - hub + flash-sale wiring for the product-details widgets
import { Subscription, timer } from 'rxjs';
import { environment } from '../../../environments/environment';
import { InventoryHubService } from '../../core/services/inventory-hub.service';
import { FlashSaleService } from '../flash-sale.service';
import { IFlashSale, IFlashSaleEnded } from '../../shared/models/flash-sale';
import { IInventoryReservation, IInventoryUpdate } from '../../shared/models/inventory';

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
  // C2-fe - reservation UX state: `reserving` disables the control while a reserve is in flight, and
  // `reservationError` surfaces the backend 409/429 contracts to the shopper (bound in the template).
  reserving = false;
  reservationError: string;
  private productId: number;
  private hubSubscriptions = new Subscription();
  // C2-fe - ids of reservations taken on THIS page, released on destroy (see ngOnDestroy).
  private reservationIds: number[] = [];

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
  // C2-fe: while a product has an ACTIVE flash sale, the "Add to Cart" control is disabled when the
  // live available stock is zero, so a shopper cannot even attempt to add unavailable units.
  get isSaleActive(): boolean {
    return !!this.activeFlashSale;
  }
  get isSaleOutOfStock(): boolean {
    return this.isSaleActive && this.quantityAvailable === 0;
  }

  // C2-fe: for a product with an ACTIVE flash sale we RESERVE the requested quantity BEFORE mutating
  // the basket, so a shopper can never add flash-sale units the allocation cannot cover (zero-oversell
  // intent, R3). Non-sale products keep the original direct add-to-basket path unchanged - the
  // flash-sale price is display-only and checkout order-gating is out of scope (AAP §0.5.2).
  addItemToBasket() {
    if (!this.isSaleActive) {
      this.basketService.addItemToBasket(this.product, this.quantity);
      return;
    }
    if (this.reserving || this.isSaleOutOfStock) {
      return;
    }

    // M15-fe: ensure the basket UUID exists BEFORE reserving so the reservation's sessionId is a valid
    // canonical basket id (never null for a first-time shopper). Reuses the shared BasketService path.
    this.basketService.getOrCreateBasketId();

    this.reserving = true;
    this.reservationError = undefined;
    this.flashSaleService.reserve(this.productId, this.quantity).subscribe(
      (reservation: IInventoryReservation) => {
        // Hold secured: track its id (released on destroy) and only NOW mutate the basket.
        this.reservationIds.push(reservation.id);
        this.basketService.addItemToBasket(this.product, this.quantity);
        this.reserving = false;
      },
      error => {
        // C2-fe: surface the documented failure contracts; the basket is NOT mutated on failure.
        this.reservationError = this.describeReserveError(error);
        this.reserving = false;
      }
    );
  }

  // C2-fe: map the backend reservation error contracts to a concise shopper-facing message.
  //   429                                   -> rate limited (10 req/min/session)
  //   409 { error: INSUFFICIENT_STOCK, available } -> too few units remaining
  //   409 { error: RESERVATION_CONFLICT }   -> optimistic-concurrency conflict after one retry
  private describeReserveError(error: any): string {
    const status = error && error.status;
    const code = error && error.error && error.error.error;
    if (status === 429) {
      return 'You are reserving too quickly. Please wait a moment and try again.';
    }
    if (status === 409 && code === 'INSUFFICIENT_STOCK') {
      const available = error.error.available;
      return available > 0
        ? 'Only ' + available + ' left - reduce the quantity and try again.'
        : 'This flash-sale item is out of stock.';
    }
    if (status === 409 && code === 'RESERVATION_CONFLICT') {
      return 'High demand right now - please try again.';
    }
    return 'Could not reserve this item. Please try again.';
  }

  incrementQuantity() {
    // C2-fe: while a sale is active, never let the requested quantity exceed the live available stock
    // (the reserve call would 409 anyway). Non-sale products remain uncapped.
    if (this.isSaleActive && this.quantityAvailable != null && this.quantity >= this.quantityAvailable) {
      return;
    }
    this.quantity++;
  }
  decrementQuantity() {
    if (this.quantity > 1){
      this.quantity--;
    }
  }

  // M14: the visible countdown reached the sale's end. Clear the local sale immediately so the
  // banner/countdown/stock widgets disappear at the boundary, then re-fetch authoritative state in
  // case a new sale was scheduled or the window changed server-side.
  onFlashSaleExpired(): void {
    this.activeFlashSale = undefined;
    this.quantityAvailable = undefined;
    this.loadActiveFlashSale();
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
    // M13: acquire reference-counted shared ownership instead of calling start() directly, so this
    // component can never stop the shared root-singleton connection out from under another consumer.
    // acquire() now surfaces a start failure (M13), so we catch it here - a dead hub must never break
    // product-details; the widgets still render from the initial REST fetch.
    this.inventoryHubService.acquire()
      .then(() => this.inventoryHubService.joinProductGroup(this.productId))
      .catch(err => console.error('InventoryHub acquire failed', err));
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
      // M9-fe: FlashSaleEnded now carries { productId, saleId }. Clear ONLY the sale whose id matches
      // the one currently displayed, so an out-of-order tick (Started(new) then Ended(old)) cannot
      // wipe a different, freshly-started sale.
      this.inventoryHubService.flashSaleEnded$.subscribe((ended: IFlashSaleEnded) => {
        if (ended && ended.productId === this.productId &&
            this.activeFlashSale && this.activeFlashSale.id === ended.saleId) {
          this.activeFlashSale = undefined;
          this.quantityAvailable = undefined;
        }
      })
    );
    this.hubSubscriptions.add(
      // M13: after an automatic reconnect the hub transparently rejoins our product group; re-fetch
      // the active sale + live stock via REST to reconcile any events missed while disconnected. The
      // REST call lives here (not in the hub service, which is intentionally HTTP-free).
      this.inventoryHubService.reconnected$.subscribe(() => this.loadActiveFlashSale())
    );
    this.hubSubscriptions.add(
      // M14: poll fallback using environment.pollInterval (previously a dead, unused setting). The hub
      // is the primary live channel, but if it is disconnected/unreachable this periodic REST refresh
      // of the active sale (+ its authoritative quantityAvailable) reconciles stale state - e.g. a
      // missed FlashSaleEnded that would otherwise leave an expired sale on screen.
      timer(environment.pollInterval, environment.pollInterval).subscribe(() => this.loadActiveFlashSale())
    );
  }

  // Real-Time Inventory & Flash Sale - clean up hub subscriptions and connection on destroy
  ngOnDestroy(): void {
    this.hubSubscriptions.unsubscribe();
    this.inventoryHubService.leaveProductGroup(this.productId);
    // M13: release shared ownership instead of stop(); the connection is torn down only when the
    // LAST consumer releases, so a sibling product-details instance keeps its live connection.
    this.inventoryHubService.release();
    // C2-fe / M6-fe: best-effort release of any holds taken on this page, returning stock promptly
    // rather than waiting for the RESERVATION_TTL_SECONDS sweep. Release is idempotent/ownership-
    // checked server-side (a hold already consumed by a completed order simply 409s and is ignored
    // here); the TTL sweep remains the backstop if the tab closes without ngOnDestroy firing.
    this.releaseTrackedReservations();
  }

  // C2-fe: release every reservation this page took, using the owning basket UUID as sessionId.
  private releaseTrackedReservations(): void {
    const sessionId = localStorage.getItem('basket_id') || '';
    this.reservationIds.forEach(id =>
      this.flashSaleService.releaseReservation(id, sessionId).subscribe(
        () => { /* released */ },
        () => { /* best-effort: ignore already consumed/expired/released holds */ }
      ));
    this.reservationIds = [];
  }
}
