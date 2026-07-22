import { Injectable, OnDestroy } from '@angular/core';
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
export class StockService implements OnDestroy {
  /** The single SignalR hub connection shared for the whole application session. */
  private hubConnection: HubConnection;

  /** Per-product streams of the latest known stock value, keyed by product id. */
  private stockSubjects = new Map<number, ReplaySubject<number>>();

  /**
   * Latest known stock value per product id, mirrored synchronously alongside the
   * per-product ReplaySubject. A ReplaySubject does not expose its buffered value
   * synchronously, so this map lets non-reactive callers (e.g. the checkout
   * `StockGuard`) read the current authoritative-as-known stock without subscribing.
   * A product id maps to a value ONLY after a valid broadcast has been applied for
   * it; an id that has never been observed returns `undefined` (unknown), which
   * consumers treat as fail-closed.
   */
  private lastKnownStock = new Map<number, number>();

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

  /**
   * Backing subject for the backend-managed low-stock threshold. Seeded with the
   * AAP-documented default of 5 so the "Only N left!" badge threshold is correct
   * even before the hub delivers the authoritative value (fail-safe default).
   */
  private lowStockThresholdSubject = new BehaviorSubject<number>(5);

  /**
   * Observable of the backend low-stock threshold (the AAP `Inventory:LowStockThreshold`
   * key, default 5). The hub pushes it via the `LowStockThreshold` message on subscribe
   * so the product/detail badges render "Only N left!" from a SINGLE source of truth
   * instead of a hardcoded client constant. Consuming templates bind to this (async)
   * or to the `lowStockThreshold` snapshot.
   */
  readonly lowStockThreshold$: Observable<number> = this.lowStockThresholdSubject.asObservable();

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

    // Server -> client push of the backend low-stock threshold (AAP
    // Inventory:LowStockThreshold). Captured into a subject so badges use a single
    // source of truth rather than a hardcoded client constant. Validated the same
    // way the server clamps it (a positive integer); a malformed value is ignored so
    // a rogue frame cannot corrupt the badge threshold.
    this.hubConnection.on('LowStockThreshold', (threshold: number) => {
      this.applyLowStockThreshold(threshold);
    });

    // Surface transient connection state so consuming UI can fail closed while the
    // socket is down or re-establishing.
    this.hubConnection.onreconnecting(() => this.connectionStateSubject.next(HubConnectionState.Reconnecting));
    this.hubConnection.onclose(() => {
      this.connectionStateSubject.next(HubConnectionState.Disconnected);
      // Terminal close (SignalR's own automatic-reconnect schedule is exhausted): clear the
      // start guard so a later startConnection() — e.g. a fresh subscribeToProduct or an app
      // re-init — begins a brand-new bounded retry cycle instead of returning the stale,
      // already-resolved start promise, which would otherwise prevent any reconnection (P4-11).
      this.startPromise = null;
      this.startAttempt = 0;

      // P5-4/P6-7 (terminal-reconnect recovery): SignalR's built-in withAutomaticReconnect
      // schedule is now exhausted and the socket is terminally closed. Previously we only reset
      // the guard and then WAITED for some external caller (a fresh subscribeToProduct or an app
      // re-init) to call startConnection() again — but a UI that already subscribed to its
      // products never re-subscribes, so its low-stock badges froze permanently and never
      // recovered even once connectivity returned. Proactively begin a FRESH bounded retry cycle
      // so the connection self-heals with no navigation or user action required. On success
      // connectWithRetry() performs a full refetch of every tracked product (re-converging state,
      // not replaying missed frames); if the server is still unreachable it resolves fail-soft and
      // resets the guard again. This cannot tight-loop: onclose only fires for a socket that WAS
      // connected, and connectWithRetry()'s own start() failures never raise onclose. Only bother
      // when something is actually tracked — with nothing subscribed there is no badge to keep live
      // (a later subscribeToProduct will start the connection on demand).
      if (this.trackedProductIds.size > 0) {
        this.startConnection();
      }
    });

    // On reconnect, re-invoke SubscribeToProduct for EVERY tracked product: a full
    // refetch of the current state (SignalR does not replay frames missed while down).
    // The handler is async and awaitable, applies the returned snapshots, and handles
    // errors per id so a single failed refetch cannot leave an unobserved rejection.
    this.hubConnection.onreconnected(() => {
      this.connectionStateSubject.next(HubConnectionState.Connected);
      return this.refetchAllTrackedStock();
    });

    // P5-4/P6-7 (terminal-reconnect recovery): also self-heal when the browser regains network
    // connectivity. If an outage lasted long enough to exhaust BOTH SignalR's automatic-reconnect
    // schedule and the onclose-triggered retry cycle above, the socket is left disconnected until
    // something restarts it. The browser 'online' event is exactly that signal, so re-initiate the
    // connection when it fires. Guarded with typeof checks so it is a safe no-op under server-side
    // rendering and the Node/Karma test host where `window` may be absent.
    if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') {
      window.addEventListener('online', this.handleOnline);
    }
  }

  /**
   * Browser `online` handler (P5-4/P6-7). Bound as an arrow property so the SAME reference is
   * used to add and later remove the listener. Re-initiates the hub connection when connectivity
   * returns, but only if the socket is not already live — `startConnection()` is idempotent and
   * its success path performs a full refetch of every tracked product, so a spurious `online`
   * event while already connected is a harmless no-op.
   */
  private handleOnline = (): void => {
    if (this.hubConnection.state !== HubConnectionState.Connected) {
      this.startConnection();
    }
  };

  /**
   * Removes the `online` listener when the root singleton is torn down (application shutdown /
   * test teardown), so the bound handler cannot outlive the service. Guarded with the same
   * typeof checks used at registration so it is a safe no-op where `window` is absent.
   */
  ngOnDestroy(): void {
    if (typeof window !== 'undefined' && typeof window.removeEventListener === 'function') {
      window.removeEventListener('online', this.handleOnline);
    }
  }

  /** Convenience flag mirroring the underlying hub connection's live state. */
  get isConnected(): boolean {
    return this.hubConnection.state === HubConnectionState.Connected;
  }

  /**
   * Snapshot of the current backend low-stock threshold (default 5 until the hub
   * delivers the authoritative value). Templates that cannot use the async pipe read
   * this synchronously to decide when to render the "Only N left!" badge.
   */
  get lowStockThreshold(): number {
    return this.lowStockThresholdSubject.value;
  }

  /**
   * Returns the latest known stock for a product, or `undefined` if no valid stock
   * has been observed yet. Synchronous, non-reactive read for callers that cannot
   * subscribe (e.g. the checkout `StockGuard`, which must decide in `canActivate`).
   * `undefined` denotes UNKNOWN and MUST be treated as fail-closed by callers.
   */
  getCurrentStock(productId: number): number | undefined {
    return this.lastKnownStock.get(productId);
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
   *   group join / initial fetch; the connect and reconnect handlers refetch every
   *   tracked id regardless, so a duplicate subscription never re-invokes the hub.
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

    // If the socket is already live, fetch just this product now. Otherwise start the
    // connection: its success handler performs a FULL refetch of every tracked id (see
    // connectWithRetry), so this product — and any previously-tracked-but-unfetched ids
    // left over from an earlier failed start cycle — are all (re)fetched exactly once on
    // connect. This avoids a duplicate invoke per product while ensuring none are omitted
    // after a fresh connection (P4-11).
    if (this.hubConnection.state === HubConnectionState.Connected) {
      this.invokeSubscribe(productId);
    } else {
      this.startConnection();
    }
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
    // Drop the synchronous snapshot too, so getCurrentStock() cannot return a stale
    // value for a product no longer tracked (the guard treats absence as unknown).
    this.lastKnownStock.delete(productId);

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
        // Refetch EVERY tracked product after this fresh connection (not only newly-added
        // ids): an earlier failed start cycle may have tracked products whose one-shot invoke
        // was skipped while disconnected, so a full refetch on connect guarantees none are
        // omitted from live-stock tracking (P4-11).
        return this.refetchAllTrackedStock();
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

    // Integer-guard the stock (QA INF-1): the authoritative Products.StockQuantity is
    // an integer, but coerce defensively so a fractional value can never render as
    // "Only 2.7 left!". Flooring is fail-safe — it never rounds UP into advertising
    // stock that is not there (2.7 -> 2, 0.4 -> 0 -> "Out of stock").
    const normalizedStock = Math.floor(currentStock);

    // Mirror synchronously so getCurrentStock() (used by the checkout StockGuard) can
    // read the latest value without subscribing.
    this.lastKnownStock.set(productId, normalizedStock);

    this.getOrCreateSubject(productId).next(normalizedStock);
  }

  /**
   * Validates and applies a backend low-stock threshold pushed over the hub. Accepts
   * only a positive integer (mirroring the server-side clamp of Max(1, value)); any
   * other payload (NaN, non-finite, <= 0, non-integer) is ignored so the badge
   * threshold cannot be corrupted by a malformed or rogue frame.
   */
  private applyLowStockThreshold(threshold: number): void {
    if (typeof threshold === 'number' && Number.isInteger(threshold) && threshold >= 1) {
      this.lowStockThresholdSubject.next(threshold);
    }
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
