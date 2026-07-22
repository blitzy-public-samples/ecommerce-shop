import { fakeAsync, flush, TestBed } from '@angular/core/testing';
import { HubConnectionBuilder, HubConnectionState } from '@microsoft/signalr';
import { environment } from '../../../environments/environment';
import { StockService } from './stock.service';

/**
 * Unit tests for StockService - the Angular client of the Real-Time Inventory &
 * Flash-Sale feature's SignalR bridge.
 *
 * The suite is fully hermetic: no real WebSocket is ever opened. StockService
 * builds its hub connection inside its CONSTRUCTOR, so every test stubs
 * HubConnectionBuilder.prototype.build to return a fake HubConnection BEFORE the
 * service is instantiated by TestBed.inject. The fake exposes exactly the members
 * the service touches (start, stop, on, off, invoke, onreconnected, onreconnecting,
 * onclose and a mutable `state`) and captures the 'StockChanged' and reconnected
 * callbacks so tests can drive a server push or a reconnect deterministically, free
 * of any network or timing dependency.
 *
 * The builder's withUrl / withAutomaticReconnect calls run through (callThrough) so
 * their arguments can be asserted, while build() is stubbed so the real connection
 * is never created. Async behaviour is exercised deterministically with either
 * awaited microtask draining or fakeAsync + flush for the timed retry/backoff paths.
 */
describe('StockService', () => {
  let service: StockService;
  let hubSpy: jasmine.SpyObj<any>;
  let withUrlSpy: jasmine.Spy;
  let withReconnectSpy: jasmine.Spy;
  let stockChangedCb: (productId: number, currentStock: number) => void;
  let lowStockThresholdCb: (threshold: number) => void;
  let reconnectedCb: () => Promise<void>;
  let closeCb: () => void;

  beforeEach(() => {
    // A fake HubConnection exposing every member StockService uses. `state` starts
    // Disconnected; a successful start() flips it to Connected so the service's
    // Connected-gated invoke path (fail-closed) behaves like the real client.
    hubSpy = jasmine.createSpyObj('HubConnection', [
      'start', 'stop', 'on', 'off', 'invoke', 'onreconnected', 'onreconnecting', 'onclose'
    ]);
    hubSpy.state = HubConnectionState.Disconnected;
    hubSpy.start.and.callFake(() => {
      hubSpy.state = HubConnectionState.Connected;
      return Promise.resolve();
    });
    hubSpy.invoke.and.returnValue(Promise.resolve(7));
    hubSpy.on.and.callFake((method: string, cb: (...args: any[]) => void) => {
      if (method === 'StockChanged') {
        stockChangedCb = cb as (productId: number, currentStock: number) => void;
      }
      if (method === 'LowStockThreshold') {
        lowStockThresholdCb = cb as (threshold: number) => void;
      }
    });
    hubSpy.onreconnected.and.callFake((cb: () => Promise<void>) => {
      reconnectedCb = cb;
    });
    hubSpy.onclose.and.callFake((cb: () => void) => {
      closeCb = cb;
    });

    // Let withUrl / withAutomaticReconnect run through so their args are recorded for
    // assertion (they simply configure the builder and return it), but stub build()
    // so the constructor receives the fake connection and never opens a real socket.
    withUrlSpy = spyOn(HubConnectionBuilder.prototype, 'withUrl').and.callThrough();
    withReconnectSpy = spyOn(HubConnectionBuilder.prototype, 'withAutomaticReconnect').and.callThrough();
    spyOn(HubConnectionBuilder.prototype, 'build').and.returnValue(hubSpy);

    TestBed.configureTestingModule({});
    service = TestBed.inject(StockService);
  });

  afterEach(() => {
    // Remove the window 'online' listener this test's service registered (P5-4/P6-7), so
    // listeners do not accumulate on the global window across the suite and a later test's
    // dispatched 'online' event cannot fan out to a prior test's service instance.
    service.ngOnDestroy();
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });

  it('builds the hub connection with the exact url and reconnect policy', () => {
    // Verbatim AAP contract: url is environment.hubUrl + 'stock' and the reconnect
    // policy is the exact exponential-backoff delay array.
    expect(withUrlSpy).toHaveBeenCalledWith(environment.hubUrl + 'stock');
    expect(withReconnectSpy).toHaveBeenCalledWith([0, 2000, 5000, 10000, 30000]);
  });

  it('emits a StockChanged broadcast on getStock$ and replays it to late subscribers', () => {
    // getStock$ creates the per-product subject, so the subsequent broadcast is
    // applied (untracked ids would otherwise be ignored).
    let emitted: number;
    service.getStock$(1).subscribe(stock => (emitted = stock));

    stockChangedCb(1, 42);

    expect(emitted).toBe(42);

    // A subscriber that arrives AFTER the push still receives the latest value
    // (ReplaySubject buffer size 1).
    let late: number;
    service.getStock$(1).subscribe(stock => (late = stock));
    expect(late).toBe(42);
  });

  it('invokes SubscribeToProduct with the product id once the socket is Connected', async () => {
    service.subscribeToProduct(1);
    await service.startConnection();
    await Promise.resolve();
    await Promise.resolve();

    expect(hubSpy.invoke).toHaveBeenCalledWith('SubscribeToProduct', 1);
  });

  it('re-subscribes to every tracked product on reconnect (full refetch)', async () => {
    service.subscribeToProduct(1);
    service.subscribeToProduct(2);
    await service.startConnection();
    await Promise.resolve();
    await Promise.resolve();

    // Discard the initial invokes, then trigger and AWAIT the captured async
    // reconnect handler so its refetch promises settle before asserting.
    hubSpy.invoke.calls.reset();
    await reconnectedCb();

    expect(hubSpy.invoke).toHaveBeenCalledWith('SubscribeToProduct', 1);
    expect(hubSpy.invoke).toHaveBeenCalledWith('SubscribeToProduct', 2);
  });

  it('applies the stock snapshots returned by the reconnect refetch to the subjects', async () => {
    hubSpy.invoke.and.callFake((methodName: string, id: number) => Promise.resolve(id === 1 ? 11 : 22));

    service.subscribeToProduct(1);
    service.subscribeToProduct(2);
    await service.startConnection();
    await Promise.resolve();
    await Promise.resolve();

    let s1: number;
    let s2: number;
    service.getStock$(1).subscribe(v => (s1 = v));
    service.getStock$(2).subscribe(v => (s2 = v));

    // Reconnect refetch returns fresh snapshots; the handler must apply them.
    hubSpy.invoke.and.callFake((methodName: string, id: number) => Promise.resolve(id === 1 ? 111 : 222));
    await reconnectedCb();

    expect(s1).toBe(111);
    expect(s2).toBe(222);
  });

  it('handles a rejected reconnect refetch without an unhandled rejection', async () => {
    service.subscribeToProduct(1);
    await service.startConnection();
    await Promise.resolve();
    await Promise.resolve();

    hubSpy.invoke.and.callFake(() => Promise.reject(new Error('refetch failed')));

    // Per-id error handling means the aggregate reconnect promise still resolves.
    let resolved = false;
    await reconnectedCb().then(() => (resolved = true));
    expect(resolved).toBe(true);
  });

  it('retries a rejected initial start with bounded backoff and connects on a later attempt', fakeAsync(() => {
    (service as any).reconnectDelaysMs = [0, 0, 0, 0, 0];
    let attempts = 0;
    hubSpy.start.and.callFake(() => {
      attempts++;
      if (attempts < 3) {
        return Promise.reject(new Error('hub down'));
      }
      hubSpy.state = HubConnectionState.Connected;
      return Promise.resolve();
    });

    service.startConnection();
    flush();

    expect(attempts).toBe(3);
    expect(hubSpy.state).toBe(HubConnectionState.Connected);
  }));

  it('resolves fail-soft and resets the guard after exhausting initial-start retries, allowing a later retry', fakeAsync(() => {
    (service as any).reconnectDelaysMs = [0, 0, 0];
    hubSpy.start.and.callFake(() => Promise.reject(new Error('down')));

    let settled = false;
    service.startConnection().then(() => (settled = true));
    flush();

    // Attempted the bounded number of times, then gave up WITHOUT rejecting.
    expect(hubSpy.start).toHaveBeenCalledTimes(3);
    expect(settled).toBe(true);
    expect((service as any).startPromise).toBeNull();

    // Guard reset -> a later call starts a fresh cycle that now succeeds (retry).
    hubSpy.start.calls.reset();
    hubSpy.start.and.callFake(() => {
      hubSpy.state = HubConnectionState.Connected;
      return Promise.resolve();
    });
    service.startConnection();
    flush();

    expect(hubSpy.start).toHaveBeenCalled();
    expect(hubSpy.state).toBe(HubConnectionState.Connected);
  }));

  it('does not invoke SubscribeToProduct when the connection never becomes Connected (fail-closed)', fakeAsync(() => {
    (service as any).reconnectDelaysMs = [0, 0, 0];
    hubSpy.start.and.callFake(() => Promise.reject(new Error('down')));

    service.subscribeToProduct(1);
    flush();

    expect(hubSpy.invoke).not.toHaveBeenCalled();
  }));

  it('invokes SubscribeToProduct only once for duplicate subscriptions to the same product (idempotent)', async () => {
    service.subscribeToProduct(1);
    service.subscribeToProduct(1);
    await service.startConnection();
    await Promise.resolve();
    await Promise.resolve();

    const subscribeInvokes = hubSpy.invoke.calls.allArgs()
      .filter((args: any[]) => args[0] === 'SubscribeToProduct' && args[1] === 1);
    expect(subscribeInvokes.length).toBe(1);
  });

  it('evicts a product only after the last consumer unsubscribes, completing its subject', async () => {
    service.subscribeToProduct(1);
    service.subscribeToProduct(1);
    await service.startConnection();
    await Promise.resolve();

    let completed = false;
    service.getStock$(1).subscribe({ complete: () => (completed = true) });

    // First release: still one consumer, so nothing is evicted.
    service.unsubscribeFromProduct(1);
    expect((service as any).trackedProductIds.has(1)).toBe(true);
    expect(completed).toBe(false);

    // Last release: tracking + subject are evicted and the subject is completed.
    service.unsubscribeFromProduct(1);
    expect((service as any).trackedProductIds.has(1)).toBe(false);
    expect((service as any).stockSubjects.has(1)).toBe(false);
    expect(completed).toBe(true);
  });

  it('ignores new products once the tracked-product upper bound is reached', () => {
    (service as any).maxTrackedProducts = 2;

    service.subscribeToProduct(1);
    service.subscribeToProduct(2);
    service.subscribeToProduct(3);

    expect((service as any).trackedProductIds.has(3)).toBe(false);
    expect((service as any).trackedProductIds.size).toBe(2);
  });

  it('ignores subscribeToProduct for invalid product ids', () => {
    service.subscribeToProduct(0);
    service.subscribeToProduct(-5);
    service.subscribeToProduct(1.5);

    expect((service as any).trackedProductIds.size).toBe(0);
  });

  it('ignores StockChanged broadcasts for untracked products and invalid stock values', () => {
    // Untracked product id: no subject is created (guards unbounded growth).
    stockChangedCb(999, 5);
    expect((service as any).stockSubjects.has(999)).toBe(false);

    // Existing subject but invalid (negative) stock: the value is not applied.
    let emitted: number;
    service.getStock$(1).subscribe(stock => (emitted = stock));
    stockChangedCb(1, -3);
    expect(emitted).toBeUndefined();

    // A valid value for the same tracked subject is applied.
    stockChangedCb(1, 8);
    expect(emitted).toBe(8);
  });

  it('resets the start guard on a terminal close so a later startConnection reconnects (P4-11)', async () => {
    // First connection succeeds and arms the idempotency guard.
    await service.startConnection();
    expect(hubSpy.start).toHaveBeenCalledTimes(1);
    expect((service as any).startPromise).not.toBeNull();

    // Terminal close (SignalR's own automatic-reconnect schedule is exhausted): the guard
    // and attempt counter must be cleared so a fresh start cycle can begin.
    hubSpy.state = HubConnectionState.Disconnected;
    closeCb();
    expect((service as any).startPromise).toBeNull();
    expect((service as any).startAttempt).toBe(0);

    // A later start therefore opens a NEW connection instead of returning the stale,
    // already-resolved promise (which previously prevented any reconnection).
    await service.startConnection();
    expect(hubSpy.start).toHaveBeenCalledTimes(2);
    expect(hubSpy.state).toBe(HubConnectionState.Connected);
  });

  it('proactively restarts a fresh connection on terminal close when products are tracked (P5-4/P6-7)', async () => {
    // A product is tracked and the socket connects (start #1); the connect-success full
    // refetch invokes SubscribeToProduct for it.
    service.subscribeToProduct(1);
    await service.startConnection();
    await Promise.resolve();
    await Promise.resolve();
    expect(hubSpy.start).toHaveBeenCalledTimes(1);
    hubSpy.invoke.calls.reset();

    // Terminal close (SignalR's own auto-reconnect schedule exhausted) while product 1 is
    // STILL tracked. Previously the badge froze because nothing re-initiated the connection;
    // now onclose must PROACTIVELY begin a fresh start cycle (start #2) with no external
    // subscribeToProduct and no navigation.
    hubSpy.state = HubConnectionState.Disconnected;
    closeCb();
    await Promise.resolve();
    await Promise.resolve();

    expect(hubSpy.start).toHaveBeenCalledTimes(2);
    expect(hubSpy.state).toBe(HubConnectionState.Connected);
    // The fresh connection performs a FULL refetch, re-invoking SubscribeToProduct for the
    // tracked id so its badge re-converges to current stock.
    expect(hubSpy.invoke).toHaveBeenCalledWith('SubscribeToProduct', 1);
  });

  it('does NOT auto-restart on terminal close when no products are tracked (P5-4/P6-7)', async () => {
    // Connect with nothing tracked (start #1).
    await service.startConnection();
    expect(hubSpy.start).toHaveBeenCalledTimes(1);

    // Terminal close with an empty tracking set: there is no badge to keep live, so onclose
    // resets the guard but must NOT open a needless connection (a later subscribeToProduct
    // starts it on demand). This keeps the guard-reset-only contract for the untracked case.
    hubSpy.state = HubConnectionState.Disconnected;
    closeCb();
    await Promise.resolve();
    await Promise.resolve();

    expect(hubSpy.start).toHaveBeenCalledTimes(1);
    expect((service as any).startPromise).toBeNull();
    expect((service as any).startAttempt).toBe(0);
  });

  it('reconnects on the browser "online" event when the socket is not connected (P5-4/P6-7)', async () => {
    // A product is tracked and the socket connects (start #1).
    service.subscribeToProduct(1);
    await service.startConnection();
    await Promise.resolve();
    await Promise.resolve();
    expect(hubSpy.start).toHaveBeenCalledTimes(1);

    // Simulate an outage that exhausted every reconnect attempt: the socket is Disconnected
    // and the start guard is cleared (isolating the 'online' recovery path from onclose).
    hubSpy.state = HubConnectionState.Disconnected;
    (service as any).startPromise = null;
    (service as any).startAttempt = 0;
    hubSpy.invoke.calls.reset();

    // The browser regains connectivity: the 'online' listener must re-initiate the connection
    // (start #2) and full-refetch every tracked product — with no navigation or user action.
    window.dispatchEvent(new Event('online'));
    await Promise.resolve();
    await Promise.resolve();

    expect(hubSpy.start).toHaveBeenCalledTimes(2);
    expect(hubSpy.state).toBe(HubConnectionState.Connected);
    expect(hubSpy.invoke).toHaveBeenCalledWith('SubscribeToProduct', 1);
  });

  it('ignores the browser "online" event while already connected (no duplicate start) (P5-4/P6-7)', async () => {
    await service.startConnection();
    expect(hubSpy.start).toHaveBeenCalledTimes(1);
    expect(hubSpy.state).toBe(HubConnectionState.Connected);

    // Already Connected: the online handler is a no-op (startConnection short-circuits on a
    // Connected socket), so no second start() is issued.
    window.dispatchEvent(new Event('online'));
    await Promise.resolve();

    expect(hubSpy.start).toHaveBeenCalledTimes(1);
  });

  it('removes the "online" listener on ngOnDestroy so it cannot reconnect after teardown (P5-4/P6-7)', async () => {
    await service.startConnection();
    expect(hubSpy.start).toHaveBeenCalledTimes(1);

    // Tear the singleton down: the window 'online' listener must be removed.
    service.ngOnDestroy();

    // A subsequent connectivity restore must NOT reconnect a destroyed service.
    hubSpy.state = HubConnectionState.Disconnected;
    (service as any).startPromise = null;
    window.dispatchEvent(new Event('online'));
    await Promise.resolve();

    expect(hubSpy.start).toHaveBeenCalledTimes(1);
  });

  it('refetches a product tracked during a failed start cycle once a later start succeeds (P4-11)', fakeAsync(() => {
    (service as any).reconnectDelaysMs = [0, 0, 0];

    // Product 1 is subscribed while the hub is DOWN: every start attempt fails, so its
    // one-shot invoke is skipped (fail-closed) and the guard resets fail-soft.
    hubSpy.start.and.callFake(() => Promise.reject(new Error('down')));
    service.subscribeToProduct(1);
    flush();
    expect(hubSpy.invoke).not.toHaveBeenCalled();
    expect((service as any).trackedProductIds.has(1)).toBe(true);

    // The hub returns and a fresh start (e.g. another subscribe or app re-init) succeeds.
    // The connect-success FULL refetch must re-invoke the previously-tracked product 1
    // rather than omit it.
    hubSpy.start.and.callFake(() => {
      hubSpy.state = HubConnectionState.Connected;
      return Promise.resolve();
    });
    service.startConnection();
    flush();

    expect(hubSpy.invoke).toHaveBeenCalledWith('SubscribeToProduct', 1);
  }));

  it('exposes connection state transitions via connectionState$', async () => {
    const states: HubConnectionState[] = [];
    service.connectionState$.subscribe(state => states.push(state));

    await service.startConnection();
    await Promise.resolve();

    expect(states[0]).toBe(HubConnectionState.Disconnected);
    expect(states).toContain(HubConnectionState.Connected);
  });

  // ---------------------------------------------------------------------------
  // F6: backend-managed low-stock threshold. The hub pushes a `LowStockThreshold`
  // message on subscribe; the service must capture it so badges use a single source
  // of truth instead of a hardcoded client 5.
  // ---------------------------------------------------------------------------

  it('defaults lowStockThreshold to 5 (AAP default) before the hub delivers one', () => {
    expect(service.lowStockThreshold).toBe(5);
    let emitted: number;
    service.lowStockThreshold$.subscribe(v => (emitted = v));
    expect(emitted).toBe(5);
  });

  it('captures the LowStockThreshold hub message and exposes it on lowStockThreshold$', () => {
    const emitted: number[] = [];
    service.lowStockThreshold$.subscribe(v => emitted.push(v));

    lowStockThresholdCb(3);

    expect(service.lowStockThreshold).toBe(3);
    expect(emitted[emitted.length - 1]).toBe(3);
  });

  it('ignores a malformed LowStockThreshold value (<=0, non-integer, non-finite)', () => {
    lowStockThresholdCb(0);
    lowStockThresholdCb(-2);
    lowStockThresholdCb(2.5);
    lowStockThresholdCb(NaN);
    lowStockThresholdCb(Infinity);
    lowStockThresholdCb('7' as any);

    // Unchanged from the default; no malformed value corrupted the threshold.
    expect(service.lowStockThreshold).toBe(5);

    // A subsequent VALID value is still accepted.
    lowStockThresholdCb(8);
    expect(service.lowStockThreshold).toBe(8);
  });

  // ---------------------------------------------------------------------------
  // INF-1: a fractional stock must never render as "Only 2.7 left!"; the service
  // floors it (fail-safe: never rounds up into advertising absent stock).
  // ---------------------------------------------------------------------------

  it('floors a fractional stock value to an integer before emitting (INF-1)', () => {
    let emitted: number;
    service.getStock$(1).subscribe(stock => (emitted = stock));

    stockChangedCb(1, 2.7);
    expect(emitted).toBe(2);

    stockChangedCb(1, 0.4);
    expect(emitted).toBe(0);
  });

  // ---------------------------------------------------------------------------
  // getCurrentStock: synchronous snapshot used by the checkout StockGuard.
  // ---------------------------------------------------------------------------

  it('getCurrentStock returns undefined for an unknown product, the value after a broadcast, and undefined after eviction', () => {
    expect(service.getCurrentStock(1)).toBeUndefined();

    service.subscribeToProduct(1);
    stockChangedCb(1, 4);
    expect(service.getCurrentStock(1)).toBe(4);

    // A floored value is mirrored too.
    stockChangedCb(1, 3.9);
    expect(service.getCurrentStock(1)).toBe(3);

    // Last consumer releases -> snapshot is dropped (unknown again, fail-closed).
    service.unsubscribeFromProduct(1);
    expect(service.getCurrentStock(1)).toBeUndefined();
  });
});
