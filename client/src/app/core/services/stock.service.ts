import { Injectable } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState } from '@microsoft/signalr';
import { environment } from '../../../environments/environment';
import { BehaviorSubject, Observable, ReplaySubject } from 'rxjs';

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
 * - `startConnection` is idempotent and resilient: the initial `start()` is retried
 *   with a bounded exponential backoff. A total outage is logged and resolves
 *   fail-soft (never rejects) so it cannot crash Angular bootstrap, but the guard is
 *   reset afterwards so a later call can start a fresh retry cycle. Subscriptions
 *   are only sent once the socket is confirmed `Connected` (fail-closed).
 * - Client state is bounded: subscriptions are idempotent and reference-counted,
 *   `unsubscribeFromProduct` evicts (and completes) a product's subject once the
 *   last consumer releases it, an upper bound caps the number of tracked products,
 *   and unsolicited / malformed broadcasts are validated and ignored.
 * - `connectionState$` exposes the live connection state so consuming UI can fail
 *   closed and announce unavailability while the socket is down or reconnecting.
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

  /** Every product id the client is currently subscribed to, replayed on reconnect. */
  private trackedProductIds = new Set<number>();

  /**
   * Number of live consumers per product id. Enables reference-counted eviction so
   * a product's subject and tracking are only released when the LAST consumer of it
   * calls `unsubscribeFromProduct`.
   */
  private subscriptionCounts = new Map<number, number>();

  /**
   * Idempotency guard for `startConnection`. Holds the in-flight/settled start
   * promise, or `null` when no start is in progress (including after a bounded
   * retry cycle has been exhausted, so a later call can retry).
   */
  private startPromise: Promise<void> | null = null;

  /** Current index into `reconnectDelaysMs` for the bounded initial-start retry. */
  private startAttempt = 0;

  /**
   * Exponential-backoff delay array (milliseconds). Used verbatim as the
   * `withAutomaticReconnect` policy for post-connection drops AND as the schedule
   * for the bounded initial-start retry cycle.
   */
  private readonly reconnectDelaysMs = [0, 2000, 5000, 10000, 30000];

  /**
   * Upper bound on the number of distinct products tracked at once. Prevents the
   * tracking set / subject map from growing without bound over a long session.
   */
  private readonly maxTrackedProducts = 500;

  /** Backing subject for the exposed connection state. Seeded as Disconnected. */
  private connectionStateSubject = new BehaviorSubject<HubConnectionState>(HubConnectionState.Disconnected);

  /**
   * Observable of the live hub connection state. Consuming UI subscribes to fail
   * closed (hide/disable stock-gated actions) and announce unavailability while the
   * connection is Connecting, Reconnecting or Disconnected.
   */
  readonly connectionState$: Observable<HubConnectionState> = this.connectionStateSubject.asObservable();

  constructor() {
    // Build the hub connection against the environment-configured hub host.
    // Dev  -> https://localhost:5001/hubs/stock ; Prod -> hubs/stock.
    // The exponential-backoff delay array is the opt-in automatic-reconnect policy
    // (used for socket drops AFTER an initial connection has succeeded).
    this.hubConnection = new HubConnectionBuilder()
      .withUrl(environment.hubUrl + 'stock')
      .withAutomaticReconnect(this.reconnectDelaysMs)
      .build();

    // Server -> client push: fan every StockChanged broadcast into the matching
    // per-product subject so all subscribed UI updates within the 2s target. The
    // payload is validated and unsolicited/untracked ids are ignored (see applyStock).
    this.hubConnection.on('StockChanged', (productId: number, currentStock: number) => {
      this.applyStock(productId, currentStock);
    });

    // Surface transient connection state so consuming UI can fail closed while the
    // socket is down or re-establishing.
    this.hubConnection.onreconnecting(() => this.connectionStateSubject.next(HubConnectionState.Reconnecting));
    this.hubConnection.onclose(() => this.connectionStateSubject.next(HubConnectionState.Disconnected));

    // On reconnect, re-invoke SubscribeToProduct for EVERY tracked product: a full
    // refetch of the current state (SignalR does not replay frames missed while down).
    // The handler is async and awaitable, applies the returned snapshots, and handles
    // errors per id so a single failed refetch cannot leave an unobserved rejection.
    this.hubConnection.onreconnected(() => {
      this.connectionStateSubject.next(HubConnectionState.Connected);
      return this.refetchAllTrackedStock();
    });
  }

  /** Convenience flag mirroring the underlying hub connection's live state. */
  get isConnected(): boolean {
    return this.hubConnection.state === HubConnectionState.Connected;
  }

  /**
   * Starts the hub connection exactly once per cycle. Called from
   * `AppComponent.ngOnInit`.
   *
   * Idempotent: while a start is in-flight (or has succeeded) the same promise is
   * returned. Resilient: a failed `start()` is retried with the bounded
   * exponential backoff in `reconnectDelaysMs`. If every attempt fails the promise
   * resolves fail-soft (it never rejects, so a hub outage cannot crash Angular
   * bootstrap) and the guard is reset so a later call can begin a fresh retry cycle.
   */
  startConnection(): Promise<void> {
    if (this.hubConnection.state === HubConnectionState.Connected) {
      return Promise.resolve();
    }

    if (!this.startPromise) {
      this.startAttempt = 0;
      this.startPromise = this.connectWithRetry();
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
   * Behaviour:
   * - Invalid product ids (non-positive / non-integer) are ignored.
   * - Reference-counts consumers so `unsubscribeFromProduct` can evict precisely.
   * - Enforces `maxTrackedProducts` so a session cannot track unbounded products.
   * - Idempotent: only the FIRST consumer of a product triggers the server-side
   *   group join / initial fetch; the reconnect handler refetches every tracked id
   *   regardless, so a duplicate subscription never re-invokes the hub.
   * - Fail-closed: the `SubscribeToProduct` invoke is only sent once the socket is
   *   confirmed `Connected` (see invokeSubscribe), never against a dead connection.
   */
  subscribeToProduct(productId: number): void {
    if (!this.isValidProductId(productId)) {
      console.warn('StockService.subscribeToProduct ignored invalid product id', productId);
      return;
    }

    const alreadyTracked = this.trackedProductIds.has(productId);

    if (!alreadyTracked && this.trackedProductIds.size >= this.maxTrackedProducts) {
      console.warn('StockService.subscribeToProduct ignored: tracked-product limit reached', this.maxTrackedProducts);
      return;
    }

    this.subscriptionCounts.set(productId, (this.subscriptionCounts.get(productId) || 0) + 1);
    this.trackedProductIds.add(productId);
    this.getOrCreateSubject(productId);

    // Idempotent: a duplicate subscription for an already-tracked product must not
    // re-invoke the hub (it only increments the reference count above).
    if (alreadyTracked) {
      return;
    }

    this.startConnection().then(() => this.invokeSubscribe(productId));
  }

  /**
   * Releases a consumer's interest in a product. Reference-counted: the product's
   * subject and tracking are only evicted once the LAST consumer releases it, at
   * which point the subject is completed and removed so it cannot leak or keep
   * emitting to future subscribers.
   */
  unsubscribeFromProduct(productId: number): void {
    const count = this.subscriptionCounts.get(productId);

    if (!count) {
      return;
    }

    if (count > 1) {
      this.subscriptionCounts.set(productId, count - 1);
      return;
    }

    // Last consumer released: evict all client-side state for this product.
    this.subscriptionCounts.delete(productId);
    this.trackedProductIds.delete(productId);

    const subject = this.stockSubjects.get(productId);
    if (subject) {
      subject.complete();
      this.stockSubjects.delete(productId);
    }
  }

  /**
   * Sends the server `SubscribeToProduct` invoke (joining the per-product group) and
   * feeds the returned current stock into the per-product subject.
   *
   * Fail-closed: only invokes against a confirmed `Connected` socket. If the
   * connection is not live (e.g. the initial start exhausted its retries), the
   * invoke is skipped rather than issued against a dead connection; the value will
   * arrive via a later subscribe or the reconnect full refetch instead.
   */
  private invokeSubscribe(productId: number): Promise<void> {
    if (this.hubConnection.state !== HubConnectionState.Connected) {
      return Promise.resolve();
    }

    return this.hubConnection
      .invoke('SubscribeToProduct', productId)
      .then((currentStock: number) => this.applyStock(productId, currentStock))
      .catch(err => console.error('Error subscribing to product stock', err));
  }

  /**
   * Re-invokes `SubscribeToProduct` for EVERY tracked product after a reconnect: a
   * full refetch of current state. Each refetch is awaited with per-id error
   * handling (a rejected invoke is logged, never left as an unobserved rejection)
   * and its returned snapshot is applied to the per-product subject.
   */
  private refetchAllTrackedStock(): Promise<void> {
    const refetches = Array.from(this.trackedProductIds).map(id =>
      this.hubConnection
        .invoke('SubscribeToProduct', id)
        .then((currentStock: number) => this.applyStock(id, currentStock))
        .catch(err => console.error('Error refetching product stock on reconnect', err))
    );

    return Promise.all(refetches).then(() => undefined);
  }

  /**
   * Drives the bounded initial-start retry cycle. On success the attempt counter is
   * reset and the state advances to Connected. On failure it logs, marks the state
   * Disconnected, and either schedules the next attempt after the corresponding
   * backoff delay or — once the bounded schedule is exhausted — resets the guard and
   * resolves fail-soft so callers/bootstrap are never rejected.
   */
  private connectWithRetry(): Promise<void> {
    this.connectionStateSubject.next(HubConnectionState.Connecting);

    return this.hubConnection
      .start()
      .then(() => {
        this.startAttempt = 0;
        this.connectionStateSubject.next(HubConnectionState.Connected);
      })
      .catch(err => {
        console.error('Error starting SignalR connection', err);
        this.connectionStateSubject.next(HubConnectionState.Disconnected);

        const nextAttempt = this.startAttempt + 1;
        if (nextAttempt < this.reconnectDelaysMs.length) {
          this.startAttempt = nextAttempt;
          return this.delay(this.reconnectDelaysMs[nextAttempt]).then(() => this.connectWithRetry());
        }

        // Bounded retries exhausted: reset the guard so a later explicit call can
        // begin a fresh retry cycle, and resolve fail-soft (never reject) so a hub
        // outage cannot crash Angular bootstrap or surface an unhandled rejection.
        this.startPromise = null;
        this.startAttempt = 0;
        return Promise.resolve();
      });
  }

  /** Promise-based delay used to space out the bounded initial-start retries. */
  private delay(ms: number): Promise<void> {
    return new Promise<void>(resolve => setTimeout(resolve, ms));
  }

  /**
   * Applies a stock value to a product's subject after validating it. Malformed
   * payloads are dropped, and broadcasts for products the client neither tracks nor
   * already has a subject for are ignored so an unsolicited/rogue broadcast cannot
   * grow client memory without bound.
   */
  private applyStock(productId: number, currentStock: number): void {
    if (!this.isValidProductId(productId) || !this.isValidStock(currentStock)) {
      return;
    }

    if (!this.trackedProductIds.has(productId) && !this.stockSubjects.has(productId)) {
      return;
    }

    this.getOrCreateSubject(productId).next(currentStock);
  }

  /** A product id is valid when it is a positive integer. */
  private isValidProductId(productId: number): boolean {
    return typeof productId === 'number' && Number.isInteger(productId) && productId > 0;
  }

  /** A stock value is valid when it is a finite, non-negative number. */
  private isValidStock(currentStock: number): boolean {
    return typeof currentStock === 'number' && Number.isFinite(currentStock) && currentStock >= 0;
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
