import { Injectable } from '@angular/core';
import * as signalR from '@microsoft/signalr';
// P6-J: BehaviorSubject backs the new connectionState$ stream so a late subscriber (e.g. a
// product-details instance created after the socket already connected/dropped) immediately receives
// the CURRENT live-connection status rather than waiting for the next transition.
import { BehaviorSubject, Subject } from 'rxjs';
import { environment } from '../../../environments/environment';
import { IFlashSale, IFlashSaleEnded } from '../../shared/models/flash-sale';
import { IInventoryUpdate } from '../../shared/models/inventory';

// M13: Initial-connect retry policy. We self-manage all (re)connection (QA finding F4 removed
// withAutomaticReconnect() - see the builder), so startWithRetry() below owns BOTH the very first
// connect and every reconnect attempt. The initial connect is retried a bounded number of times with a
// fixed backoff, then the final failure is surfaced to the caller (no longer swallowed into a
// fulfilled promise).
const INITIAL_START_MAX_ATTEMPTS = 3;
const INITIAL_START_RETRY_DELAY_MS = 2000;

// QA finding F4: bounded self-managed reconnect budget. When an already-established connection drops we
// retry the connect OURSELVES (see onclose -> reconnect) at INITIAL_START_RETRY_DELAY_MS spacing. This
// budget bounds ONLY connect attempts made while the browser reports ONLINE (an OFFLINE period parks on
// the 'online' event and consumes NO attempts - so a long outage never exhausts the budget and self-
// heals the instant connectivity returns). The budget therefore caps the rarer "online but server
// briefly unreachable" case; if it is exhausted we stop retrying (mirroring the previous
// withAutomaticReconnect give-up), the product-details M14 REST poll keeps the UI fresh, and a later
// manual acquire() can always reopen the socket.
const RECONNECT_MAX_ATTEMPTS = 10;

// Real-Time Inventory & Flash Sale - SignalR client wrapper.
// Surfaces the three server-to-client hub events as RxJS observables consumed by the
// shop widgets and product-details.component. Authentication reuses the existing JWT
// scheme: a WebSocket cannot carry an Authorization header, so the token is supplied via
// accessTokenFactory reading the SAME localStorage 'token' key used by
// core/interceptors/jwt.interceptor.ts. No second auth mechanism is introduced, and the
// hub is single-instance / in-memory (no Redis backplane).
//
// This service is HTTP-free by design (its spec provides no HttpClient): any state that must be
// reconciled after a reconnect is signalled to consumers via reconnected$, and the consuming
// component performs the REST refresh itself.
@Injectable({
  providedIn: 'root'
})
export class InventoryHubService {
  private hubConnection: signalR.HubConnection =
    new signalR.HubConnectionBuilder()
      // QA Issue #2 / Info: silence SignalR's chatty default Information-level lifecycle logging (which also
      // prints the access_token-bearing WebSocket URL to the console). Warning still surfaces genuine
      // warnings/errors but removes the per-connection console noise, supporting console cleanliness.
      .configureLogging(signalR.LogLevel.Warning)
      .withUrl(environment.hubUrl, { accessTokenFactory: () => localStorage.getItem('token') || '' })
      // QA finding F4 (LOW, resilience/console-hygiene): intentionally NOT calling
      // .withAutomaticReconnect(). Its internal reconnect loop (HubConnection._reconnect ->
      // _startInternal -> transport start) settles a FLOATING promise whose rejection our code cannot
      // attach a .catch to. Under Angular's zone.js that surfaces as an "Unhandled Promise rejection"
      // console.error during a sustained outage - and zone.js logs it BEFORE dispatching the native
      // 'unhandledrejection' event to listeners, so a window-level preventDefault() cannot suppress it.
      // Instead we drive reconnection ourselves from the onclose handler via startWithRetry() (see
      // below), so EVERY connect attempt is awaited inside our own try/catch and no promise is ever
      // left floating. The graceful-recovery contract is preserved: on a successful reconnect we rejoin
      // the retained product groups and emit reconnected$ so consumers reconcile missed updates.
      .build();

  private inventoryUpdatedSource = new Subject<IInventoryUpdate>();
  inventoryUpdated$ = this.inventoryUpdatedSource.asObservable();
  private flashSaleStartedSource = new Subject<IFlashSale>();
  flashSaleStarted$ = this.flashSaleStartedSource.asObservable();
  // M9-fe: FlashSaleEnded is now typed as IFlashSaleEnded ({ productId, saleId }) rather than a full
  // IFlashSale. The server sends only the identifiers of the sale that ended, so consumers can clear
  // exactly the sale whose id matches and never wipe a different (e.g. freshly-started) sale.
  private flashSaleEndedSource = new Subject<IFlashSaleEnded>();
  flashSaleEnded$ = this.flashSaleEndedSource.asObservable();

  // M13: Emits AFTER the connection is re-established (by the self-managed onclose-driven reconnect(),
  // QA finding F4) and retained groups have been rejoined. Consumers subscribe to refresh any state
  // that may have drifted while disconnected (e.g. re-fetch the active sale + live stock via REST). The
  // refresh lives in the consumer so this service stays HTTP-free.
  private reconnectedSource = new Subject<void>();
  reconnected$ = this.reconnectedSource.asObservable();

  // P6-J (offline UI staleness): expose the live-connection status so a consumer can tell the shopper
  // when on-screen inventory may be stale (offline / reconnecting) instead of silently presenting a
  // last-known value as if it were live. Purely additive/observational — it never alters the
  // (re)connection control flow; the values are emitted from the SAME lifecycle points that already
  // drive start()/onclose/reconnect(). 'connected' = live push is flowing; 'reconnecting' = an
  // unexpected drop we are actively healing (incl. parked-until-online); 'disconnected' = deliberately
  // stopped, fully released, or the bounded reconnect budget was exhausted. Seeded 'disconnected' so a
  // subscriber that connects before the first acquire() sees the correct pre-connection state.
  private connectionStateSource =
    new BehaviorSubject<'connected' | 'reconnecting' | 'disconnected'>('disconnected');
  connectionState$ = this.connectionStateSource.asObservable();

  // M13: Retained registry of product groups this (shared) connection has joined. Kept so reconnect()
  // (QA finding F4) can transparently rejoin them after a self-managed reconnect, and so groups
  // requested before the socket is up are joined once it connects.
  private joinedGroups = new Set<number>();

  // M13: Reference count of active consumers (product-details instances). The underlying connection
  // starts on the FIRST acquire() and stops only on the LAST release(), so one component can no
  // longer stop the shared root-singleton connection out from under another subscriber.
  private refCount = 0;

  // M13: Shared in-flight start promise so concurrent acquire() callers await the SAME start attempt
  // instead of racing two start() calls (which SignalR rejects unless state === Disconnected).
  private startPromise: Promise<void> | null = null;

  // QA finding F4 (LOW, resilience): distinguishes a DELIBERATE teardown from an unexpected network
  // drop for the onclose-driven reconnect (see constructor + reconnect()). stop()/release() set this
  // true so onclose does NOT try to resurrect a connection the app intentionally closed; acquire()
  // clears it because a fresh consumer wants the connection kept alive across drops.
  private userStopped = false;

  // QA finding F4 (LOW, console-hygiene): while the browser is offline reconnect() parks on the window
  // 'online' event instead of calling hubConnection.start() (a failed start() makes @microsoft/signalr
  // float internal negotiate-rejections that zone.js logs as "Unhandled Promise rejection"). This holds
  // the cleanup for the currently-parked wait so a deliberate stop() can release it immediately.
  private onlineWaitAbort: (() => void) | null = null;

  constructor() {
    // Register the server-to-client handlers once. The event names MUST match the backend
    // InventoryHub method names exactly (InventoryUpdated / FlashSaleStarted / FlashSaleEnded).
    this.hubConnection.on('InventoryUpdated', (payload: IInventoryUpdate) => this.inventoryUpdatedSource.next(payload));
    this.hubConnection.on('FlashSaleStarted', (payload: IFlashSale) => this.flashSaleStartedSource.next(payload));
    // M9-fe: forward the { productId, saleId } payload typed as IFlashSaleEnded.
    this.hubConnection.on('FlashSaleEnded', (payload: IFlashSaleEnded) => this.flashSaleEndedSource.next(payload));

    // QA finding F4 (LOW, resilience/console-hygiene): recover from an UNEXPECTED connection drop
    // ourselves. Because we do NOT use withAutomaticReconnect() (see builder above), the connection has
    // no internal reconnect policy and instead fires onclose the moment the socket drops. We respond by
    // self-managing reconnection so that no library-internal promise can float unhandled through
    // zone.js. onclose fires for BOTH a deliberate stop() and a network drop, so reconnect() itself
    // gates on userStopped/refCount to only resurrect a connection consumers still want. This single
    // handler replaces the previous onreconnected/onreconnecting hooks (which only fire when
    // withAutomaticReconnect owns the connection); the rejoin-groups + reconnected$ recovery they
    // provided now lives in reconnect().
    this.hubConnection.onclose(() => {
      // Drop the shared start promise so reconnect()/a future acquire() can reopen from Disconnected.
      this.startPromise = null;
      // P6-J: reflect the drop to consumers using the SAME gate reconnect() applies below. An
      // unexpected drop that we will actively heal (consumers still want the connection) is
      // 'reconnecting'; a deliberate stop() or a fully-released connection is 'disconnected'.
      this.connectionStateSource.next(
        (this.userStopped || this.refCount <= 0) ? 'disconnected' : 'reconnecting');
      // Fire-and-forget: reconnect() is fully self-contained (every path is caught), so it can never
      // surface as an unhandled rejection; `void` marks the intentional non-await.
      void this.reconnect();
    });
  }

  // M13: Reference-counted shared ownership. Each consuming component acquires on init; the
  // connection is started only on the first acquisition. Returns the shared start promise so callers
  // can await a live connection (and observe a start failure).
  acquire(): Promise<void> {
    this.refCount++;
    // QA finding F4: a consumer wants the connection, so clear any prior deliberate-stop flag - a drop
    // from here on should trigger the onclose-driven reconnect() rather than stand down.
    this.userStopped = false;
    return this.start();
  }

  // M13: Release a consumer's interest. The connection is stopped ONLY when the last consumer
  // releases, so a single product-details instance can no longer tear down the shared connection.
  release(): Promise<void> {
    if (this.refCount > 0) {
      this.refCount--;
    }
    if (this.refCount > 0) {
      // Other consumers still need the connection - keep it open.
      return Promise.resolve();
    }
    return this.stop();
  }

  // Start the connection with a bounded initial-connect retry. Unlike the previous implementation the
  // final failure is NOT swallowed: it is re-thrown so the caller (acquire()) can react. A start
  // attempt already in flight is shared via startPromise so concurrent acquires never race.
  start(): Promise<void> {
    // Already connected - nothing to do.
    if (this.hubConnection.state === signalR.HubConnectionState.Connected) {
      return Promise.resolve();
    }
    // A start attempt is already in flight (e.g. another acquire()) - share its outcome.
    if (this.startPromise) {
      return this.startPromise;
    }
    // Only a Disconnected connection may be started (SignalR throws otherwise). If we are mid-transition
    // (Connecting/Disconnecting, e.g. a self-managed reconnect via startWithRetry() is already in
    // flight, or a stop() is completing) there is nothing safe to do here - the in-flight attempt or the
    // onclose-driven reconnect() path will settle the state.
    if (this.hubConnection.state !== signalR.HubConnectionState.Disconnected) {
      return Promise.resolve();
    }

    this.startPromise = this.startWithRetry(INITIAL_START_MAX_ATTEMPTS)
      // (Re)join any groups requested before the socket finished connecting.
      .then(() => this.rejoinGroups())
      .then(() => {
        this.startPromise = null;
        // P6-J: the initial connect (and its group rejoin) succeeded — live push is now flowing.
        this.connectionStateSource.next('connected');
      })
      .catch(err => {
        this.startPromise = null;
        // M13: surface the failure instead of swallowing it into a fulfilled promise.
        throw err;
      });
    return this.startPromise;
  }

  // Stop the connection, guarded by state; errors swallowed/logged. Prefer release() from components
  // so reference counting is honoured - a direct stop() bypasses shared ownership.
  stop(): Promise<void> {
    // QA finding F4: mark this as a DELIBERATE teardown BEFORE stopping. onclose fires for both a manual
    // stop and a network drop; this flag lets the onclose-driven reconnect() stand down here instead of
    // trying to reopen a connection the app intentionally closed.
    this.userStopped = true;
    // QA finding F4: release any reconnect loop currently parked waiting for the browser to come back
    // online, so a deliberate teardown does not leave it hanging on the 'online' event. The loop wakes,
    // re-checks userStopped (now true) and stands down.
    if (this.onlineWaitAbort) {
      this.onlineWaitAbort();
    }
    if (this.hubConnection.state === signalR.HubConnectionState.Disconnected) {
      return Promise.resolve();
    }
    return this.hubConnection.stop().catch(err => console.error('InventoryHub stop failed', err));
  }

  // Best-effort per-product group subscription so product-details receives only relevant updates
  // (AAP 0.4.3 "joins the product's hub group ... leaves on destroy"). The membership is retained in
  // joinedGroups regardless of connection state (M13) so it can be (re)joined on connect/reconnect.
  // The hub is primarily server-to-client; if the backend does not expose these invokable methods the
  // rejected promise is swallowed, making these safe no-ops.
  joinProductGroup(productId: number): Promise<void> {
    this.joinedGroups.add(productId);
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected) {
      // Not connected yet; membership is retained and joined by start()/reconnect() once connected.
      return Promise.resolve();
    }
    return this.hubConnection.invoke('JoinProductGroup', productId)
      .catch(err => console.error('InventoryHub joinProductGroup failed', err));
  }

  leaveProductGroup(productId: number): Promise<void> {
    this.joinedGroups.delete(productId);
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected) {
      return Promise.resolve();
    }
    return this.hubConnection.invoke('LeaveProductGroup', productId)
      .catch(err => console.error('InventoryHub leaveProductGroup failed', err));
  }

  // M13: Rejoin every retained group. Called after the initial connect and after an automatic
  // reconnect. Each invoke is individually guarded so one failure never rejects the whole batch.
  private rejoinGroups(): Promise<void> {
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected || this.joinedGroups.size === 0) {
      return Promise.resolve();
    }
    const rejoins = Array.from(this.joinedGroups).map(productId =>
      this.hubConnection.invoke('JoinProductGroup', productId)
        .catch(err => console.error('InventoryHub rejoinProductGroup failed', productId, err)));
    return Promise.all(rejoins).then(() => undefined);
  }

  // M13: Recursive bounded retry of the INITIAL connect. On the final failure the error propagates.
  private async startWithRetry(attemptsRemaining: number): Promise<void> {
    try {
      await this.hubConnection.start();
    } catch (err) {
      if (attemptsRemaining > 1) {
        console.warn(`InventoryHub start failed; retrying (${attemptsRemaining - 1} attempt(s) left)`, err);
        await this.delay(INITIAL_START_RETRY_DELAY_MS);
        return this.startWithRetry(attemptsRemaining - 1);
      }
      console.error('InventoryHub initial start failed after all retries', err);
      throw err;
    }
  }

  // QA finding F4 (LOW, resilience/console-hygiene): self-managed reconnect, invoked from onclose after
  // an UNEXPECTED drop. It replaces withAutomaticReconnect()'s internal (floating) reconnect loop with a
  // loop we fully own. The crux of the fix: EVERY hubConnection.start() attempt is awaited inside this
  // method's own try/catch, so a failed attempt rejects into OUR handler instead of settling a floating
  // promise - which is exactly what keeps zone.js from ever observing an uncaught rejection and printing
  // "Unhandled Promise rejection" console.error noise during an outage.
  //
  // Logging is deliberately minimal (the finding's theme is console hygiene): individual failed attempts
  // are silent, and only a single informative warning is emitted if we ultimately give up. On a
  // successful reconnect it restores the exact pre-drop contract the previous onreconnected hook
  // provided: rejoin the retained product groups, then signal consumers via reconnected$ so they
  // reconcile any updates missed while offline (the REST refresh lives in the consumer; this service
  // stays HTTP-free). A deliberate stop() (userStopped) or a fully-released connection (refCount === 0)
  // short-circuits - checked at entry AND on every iteration - so we never resurrect a connection nobody
  // wants. If the outage outlasts the bounded budget we stop (matching the previous withAutomaticReconnect
  // give-up behaviour); the product-details M14 REST poll keeps the UI fresh and a later acquire()/start()
  // reopens the socket. Declared async and returning Promise<void> purely for testability; onclose calls
  // it fire-and-forget and every path is caught, so the returned promise never rejects (nothing floats).
  private async reconnect(): Promise<void> {
    if (this.userStopped || this.refCount <= 0) {
      return;
    }
    for (let attempt = 1; attempt <= RECONNECT_MAX_ATTEMPTS; attempt++) {
      // A deliberate stop() or a full release() may have happened between attempts - stand down.
      if (this.userStopped || this.refCount <= 0) {
        return;
      }
      // CORE OF THE F4 FIX: never call hubConnection.start() while the browser is offline. A start()
      // that fails at negotiation makes @microsoft/signalr settle internal promises that our await
      // cannot attach to, and zone.js reports them as "Unhandled Promise rejection" console.error noise.
      // By parking on the 'online' event until connectivity returns we simply never invoke the failing
      // start(), so no such promise is ever created. This also means an offline period consumes NO
      // attempts from the budget and the socket self-heals the instant the network comes back.
      await this.waitUntilOnline();
      if (this.userStopped || this.refCount <= 0) {
        return;
      }
      try {
        // Only a Disconnected connection may be (re)started (SignalR throws otherwise).
        if (this.hubConnection.state === signalR.HubConnectionState.Disconnected) {
          await this.hubConnection.start();
        }
      } catch {
        // Online but the connect failed (e.g. the server was briefly unreachable). The rejection is
        // fully handled HERE - it never floats. Back off and retry (kept silent for console hygiene).
        await this.delay(INITIAL_START_RETRY_DELAY_MS);
        continue;
      }
      if (this.hubConnection.state === signalR.HubConnectionState.Connected) {
        // Reconnected. If everyone released (or a deliberate stop happened) while we were reconnecting,
        // stand the connection back down; otherwise restore the pre-drop contract.
        if (this.userStopped || this.refCount <= 0) {
          return this.stop();
        }
        await this.rejoinGroups();
        // P6-J: live push has resumed after the drop — clear the 'reconnecting' status.
        this.connectionStateSource.next('connected');
        this.reconnectedSource.next();
        return;
      }
      // Still transitioning (not yet Connected) - wait out the backoff and re-check.
      await this.delay(INITIAL_START_RETRY_DELAY_MS);
    }
    // P6-J: the bounded online-retry budget was exhausted (sustained server-unreachable-while-online).
    // We stop actively retrying here, so the status is 'disconnected' until a later acquire()/start()
    // reopens the socket; the product-details M14 REST poll keeps reconciling stock/price meanwhile.
    this.connectionStateSource.next('disconnected');
    // Online-retry budget exhausted (a sustained server-unreachable-while-online outage; an offline
    // outage never reaches here because it parks on 'online' above). Surface ONE handled warning (not an
    // unhandled rejection) so the give-up stays diagnosable; live push resumes on the next connection and
    // the product-details M14 REST poll keeps reconciling stock/price in the meantime.
    console.warn('InventoryHub reconnect gave up after a sustained outage; live updates will resume on '
      + 'the next connection (REST polling continues to refresh stock/price).');
  }

  // QA finding F4: is the browser currently online? navigator may be undefined in non-browser/test
  // contexts, in which case we treat the environment as online so the reconnect logic proceeds normally.
  private isOnline(): boolean {
    return typeof navigator === 'undefined' || navigator.onLine !== false;
  }

  // QA finding F4: resolve as soon as the browser has connectivity. If already online this resolves
  // synchronously; otherwise it parks on a one-shot window 'online' listener (never a timer/poll) so we
  // avoid calling start() - and thus creating a floating negotiate-rejection - during the outage. The
  // pending wait is exposed via onlineWaitAbort so stop() can release it immediately on teardown; the
  // reconnect loop re-checks userStopped/refCount right after this resolves and stands down if needed.
  private waitUntilOnline(): Promise<void> {
    if (this.isOnline()) {
      return Promise.resolve();
    }
    if (typeof window === 'undefined' || typeof window.addEventListener !== 'function') {
      return Promise.resolve();
    }
    return new Promise<void>(resolve => {
      const done = () => {
        window.removeEventListener('online', done);
        if (this.onlineWaitAbort === done) {
          this.onlineWaitAbort = null;
        }
        resolve();
      };
      // Allow a deliberate stop() to release the parked wait immediately (the loop then stands down).
      this.onlineWaitAbort = done;
      window.addEventListener('online', done);
    });
  }

  // Small awaitable delay used between initial-connect retries. Isolated as a method so tests can
  // stub the backoff (spy) and run the retry path instantly.
  private delay(ms: number): Promise<void> {
    return new Promise<void>(resolve => setTimeout(resolve, ms));
  }
}
