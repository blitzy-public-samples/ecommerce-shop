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
  // C2-fe / QA R8-A - ids of reservations this page took. They are RETAINED to stay associated with
  // the basket lines they back and are NOT released on route destruction (see ngOnDestroy); their
  // terminal lifecycle is the TTL sweep (abandonment) or the checkout consume hook.
  private reservationIds: number[] = [];
  // QA P7-G - epoch ms of the most recent LIVE hub InventoryUpdated/FlashSaleStarted stock value. Used
  // to stop a slower/older REST poll response from moving displayed stock backward over a newer push.
  private lastInventoryUpdateAt = 0;
  // QA P6-J - true when the authenticated live-update connection is not currently 'connected'
  // (offline / reconnecting), so the shopper can be told on-screen stock may be stale.
  liveUpdatesStale = false;

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
  // QA P6-J: show the "live updates paused" notice only when it is actually meaningful - i.e. there is
  // an active flash sale whose stock the shopper is watching, the shopper is signed in (so a live hub
  // connection is expected at all; anonymous shoppers only ever get the initial REST value and must not
  // see a "paused" notice), and that connection is not currently 'connected'. This mirrors the hub
  // acquisition gate in initInventoryHub (token present) so the indicator can never misfire.
  get showStaleIndicator(): boolean {
    return this.isSaleActive && this.liveUpdatesStale && !!localStorage.getItem('token');
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
        // Hold secured: track its id (retained across route destruction per QA R8-A - the hold backs
        // this basket line; TTL sweep or checkout consume owns its terminal lifecycle) and only NOW
        // mutate the basket.
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
    // QA P7-G: remember WHEN this read was issued. The subscribe callback below only applies the
    // REST stock reading if NO live hub InventoryUpdated/FlashSaleStarted has arrived since (a newer
    // live value must never be moved backward by an older, slower poll response).
    const issuedAt = Date.now();
    this.flashSaleService.getActiveFlashSale(this.productId).subscribe(sale => {
      this.activeFlashSale = sale;
      if (sale) {
        // QA P7-G: guard against a stale poll overwriting a fresher live push. If a hub stock value
        // landed after this request was issued, keep the live value; otherwise adopt the REST value.
        if (this.lastInventoryUpdateAt <= issuedAt) {
          this.quantityAvailable = sale.quantityAvailable;
        }
      }
    }, error => {
      console.log(error);
    });
  }

  // Real-Time Inventory & Flash Sale - start hub, join product group, subscribe to live streams
  initInventoryHub(): void {
    // M13: acquire reference-counted shared ownership instead of calling start() directly, so this
    // component can never stop the shared root-singleton connection out from under another consumer.
    // acquire() surfaces a start failure (M13), so we catch it here - a dead hub must never break
    // product-details; the widgets still render from the initial REST fetch.
    // QA Issue #2: the InventoryHub is [Authorize]; a WebSocket cannot answer an auth challenge, so an
    // anonymous negotiate returns 401 and emits a retry-storm of console errors + a final rejection. Only
    // acquire the hub when a JWT is present (the SAME 'token' localStorage key used by jwt.interceptor.ts
    // and the hub accessTokenFactory). Anonymous shoppers still see the sale, countdown, and initial stock
    // via the public GET /api/flash-sales/active call; only LIVE hub updates require an authenticated
    // connection. release() in ngOnDestroy is ref-count-guarded, so an anonymous session that never
    // acquired is a safe no-op there.
    if (localStorage.getItem('token')) {
      this.inventoryHubService.acquire()
        .then(() => this.inventoryHubService.joinProductGroup(this.productId))
        .catch(err => console.error('InventoryHub acquire failed', err));
    }
    this.hubSubscriptions.add(
      this.inventoryHubService.inventoryUpdated$.subscribe((update: IInventoryUpdate) => {
        if (update && update.productId === this.productId) {
          this.quantityAvailable = update.quantityAvailable;
          // QA P7-G: this is the freshest LIVE value; stamp it so a slower REST poll that was issued
          // earlier cannot overwrite it with an older number (previously stock flickered backward).
          this.lastInventoryUpdateAt = Date.now();
        }
      })
    );
    this.hubSubscriptions.add(
      this.inventoryHubService.flashSaleStarted$.subscribe((sale: IFlashSale) => {
        if (sale && sale.productId === this.productId) {
          this.activeFlashSale = sale;
          this.quantityAvailable = sale.quantityAvailable;
          // QA P7-G: a freshly-started sale's stock is a live value; stamp it so an older in-flight
          // REST poll response cannot clobber it with a stale number.
          this.lastInventoryUpdateAt = Date.now();
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
      // QA P6-J: track the live-connection state so the shopper can be told when live stock updates
      // are paused (offline / reconnecting). liveUpdatesStale is true whenever the authenticated hub
      // connection is not 'connected'; the on-screen indicator is additionally gated (see
      // showStaleIndicator) on there being an active sale and a signed-in shopper who could ever have
      // a live connection, so anonymous or no-sale views never show a spurious "paused" notice.
      this.inventoryHubService.connectionState$.subscribe(state => {
        this.liveUpdatesStale = state !== 'connected';
      })
    );
    this.hubSubscriptions.add(
      // M14: poll fallback using environment.pollInterval (previously a dead, unused setting). The hub
      // is the primary live channel, but if it is disconnected/unreachable this periodic REST refresh
      // of the active sale (+ its authoritative quantityAvailable) reconciles stale state - e.g. a
      // missed FlashSaleEnded that would otherwise leave an expired sale on screen.
      timer(environment.pollInterval, environment.pollInterval).subscribe(() => this.loadActiveFlashSale())
    );
  }

  // Real-Time Inventory & Flash Sale - clean up hub subscriptions and connection on destroy.
  ngOnDestroy(): void {
    this.hubSubscriptions.unsubscribe();
    this.inventoryHubService.leaveProductGroup(this.productId);
    // M13: release shared ownership instead of stop(); the connection is torn down only when the
    // LAST consumer releases, so a sibling product-details instance keeps its live connection.
    this.inventoryHubService.release();
    // QA R8-A FIX (CRITICAL, was: releaseTrackedReservations() here): do NOT release this session's
    // holds on route destruction. A reservation backs a line the shopper has ADDED TO THE BASKET, and
    // navigating product-details -> basket (or anywhere) is not an intent to cancel that line. The
    // previous best-effort release returned the held stock to the pool while the Redis basket line
    // stayed checkout-ready, leaving the basket line unbacked and re-exposing sold-through stock
    // (oversell risk). Per AAP 0.4.3 destroy must only "leave the hub group" (done above); the hold's
    // terminal lifecycle is owned by the RESERVATION_TTL_SECONDS sweep (AAP R4 auto-release on
    // abandonment) and the checkout consume hook (AAP R5), never by page navigation. The reservation
    // ids remain tracked (see reservationIds) so they stay associated with the basket lines they back;
    // an explicit cart removal, checkout, or TTL expiry — not this destroy — releases them.
  }
}
