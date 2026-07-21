import { Injectable } from '@angular/core';
import { HubConnection, HubConnectionBuilder } from '@microsoft/signalr';
import { environment } from '../../../environments/environment';
import { Observable, ReplaySubject } from 'rxjs';

/**
 * StockService is the client endpoint of the Real-Time Inventory & Flash-Sale
 * feature's Redis -> SignalR bridge.
 *
 * The server broadcasts `StockChanged(productId, currentStock)` to a per-product
 * SignalR group; this service fans each broadcast into a per-product
 * `ReplaySubject<number>` consumed by the product, basket and checkout surfaces
 * for low-stock badges ('Only N left!' / 'Out of stock') and zero-stock checkout
 * gating.
 *
 * Design guarantees:
 * - Initial AND live stock arrive EXCLUSIVELY over the SignalR hub, never from the
 *   cached catalog response. This service deliberately issues no HTTP requests.
 * - `getStock$` replays the latest known value (ReplaySubject buffer size 1), so a
 *   component that subscribes AFTER a broadcast still receives the current value.
 * - `withAutomaticReconnect` plus an `onreconnected` FULL refetch re-converges
 *   client state after a socket drop by re-subscribing to every tracked product
 *   (a refetch of the current state, not a replay of frames missed while down).
 * - `startConnection` is idempotent and fail-soft: a hub outage is logged but never
 *   rejected, so it cannot crash Angular bootstrap.
 *
 * Root-provided singleton (mirrors the sibling `busy.service.ts`), so no module
 * `providers`/`declarations` entry is required.
 */
@Injectable({
  providedIn: 'root'
})
export class StockService {
  /** The single SignalR hub connection shared for the whole application session. */
  private hubConnection: HubConnection;

  /** Per-product streams of the latest known stock value, keyed by product id. */
  private stockSubjects = new Map<number, ReplaySubject<number>>();

  /** Every product id the client has subscribed to, replayed on reconnect. */
  private trackedProductIds = new Set<number>();

  /** Idempotency guard so the socket is started at most once by `startConnection`. */
  private startPromise: Promise<void>;

  constructor() {
    // Build the hub connection against the environment-configured hub host.
    // Dev  -> https://localhost:5001/hubs/stock ; Prod -> hubs/stock.
    // The exponential-backoff delay array is the opt-in automatic-reconnect policy.
    this.hubConnection = new HubConnectionBuilder()
      .withUrl(environment.hubUrl + 'stock')
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .build();

    // Server -> client push: fan every StockChanged broadcast into the matching
    // per-product subject so all subscribed UI updates within the 2s target.
    this.hubConnection.on('StockChanged', (productId: number, currentStock: number) => {
      this.getOrCreateSubject(productId).next(currentStock);
    });

    // On reconnect, re-invoke SubscribeToProduct for EVERY tracked product: a full
    // refetch of the current state (SignalR does not replay frames missed while down).
    this.hubConnection.onreconnected(() => {
      this.trackedProductIds.forEach(id => this.hubConnection.invoke('SubscribeToProduct', id));
    });
  }

  /**
   * Starts the hub connection exactly once. Called from `AppComponent.ngOnInit`.
   *
   * Idempotent: repeat calls return the same in-flight or settled promise. Fail-soft:
   * a connection error is caught and logged so a hub outage neither crashes Angular
   * bootstrap nor rejects callers awaiting the socket.
   */
  startConnection(): Promise<void> {
    if (!this.startPromise) {
      this.startPromise = this.hubConnection
        .start()
        .catch(err => console.error('Error starting SignalR connection', err));
    }

    return this.startPromise;
  }

  /**
   * Returns an observable of the latest known stock for a product. The underlying
   * ReplaySubject replays the most recent value to late subscribers. The observable
   * projection is returned (never the raw subject) so consumers cannot push values.
   */
  getStock$(productId: number): Observable<number> {
    return this.getOrCreateSubject(productId).asObservable();
  }

  /**
   * Tracks a product and asks the hub for its current stock.
   *
   * Records the id (so the reconnect handler can refetch it) and ensures its subject
   * exists, then, once the connection is live, invokes the server `SubscribeToProduct`
   * method (joining the per-product group). The invoke returns the current stock,
   * which is fed into the per-product subject; the same value also arrives via the
   * `StockChanged` push, so the two paths are idempotent. Awaiting `startConnection`
   * first guards against early component calls throwing before the socket is live;
   * a failed invoke is logged rather than surfaced as an unhandled rejection.
   */
  subscribeToProduct(productId: number): void {
    this.trackedProductIds.add(productId);
    this.getOrCreateSubject(productId);

    this.startConnection().then(() =>
      this.hubConnection
        .invoke('SubscribeToProduct', productId)
        .then((currentStock: number) => this.getOrCreateSubject(productId).next(currentStock))
        .catch(err => console.error('Error subscribing to product stock', err))
    );
  }

  /**
   * Lazily creates (or returns the existing) per-product `ReplaySubject<number>`.
   * A buffer size of 1 guarantees late subscribers immediately receive the latest
   * value pushed for the product.
   */
  private getOrCreateSubject(productId: number): ReplaySubject<number> {
    let subject = this.stockSubjects.get(productId);

    if (!subject) {
      subject = new ReplaySubject<number>(1);
      this.stockSubjects.set(productId, subject);
    }

    return subject;
  }
}
