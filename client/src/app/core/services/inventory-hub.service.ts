import { Injectable } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { environment } from '../../../environments/environment';
import { IFlashSale, IFlashSaleEnded } from '../../shared/models/flash-sale';
import { IInventoryUpdate } from '../../shared/models/inventory';

// M13: Initial-connect retry policy. withAutomaticReconnect() only retries AFTER a first
// successful connection drops - it does NOT retry the very first start(). We therefore retry the
// initial connect a bounded number of times with a fixed backoff, then surface the final failure to
// the caller (no longer swallowed into a fulfilled promise).
const INITIAL_START_MAX_ATTEMPTS = 3;
const INITIAL_START_RETRY_DELAY_MS = 2000;

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
      .withAutomaticReconnect()
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

  // M13: Emits AFTER the connection is automatically re-established and retained groups have been
  // rejoined. Consumers subscribe to refresh any state that may have drifted while disconnected
  // (e.g. re-fetch the active sale + live stock via REST). The refresh lives in the consumer so this
  // service stays HTTP-free.
  private reconnectedSource = new Subject<void>();
  reconnected$ = this.reconnectedSource.asObservable();

  // M13: Retained registry of product groups this (shared) connection has joined. Kept so the
  // onreconnected handler can transparently rejoin them after an automatic reconnect, and so groups
  // requested before the socket is up are joined once it connects.
  private joinedGroups = new Set<number>();

  // M13: Reference count of active consumers (product-details instances). The underlying connection
  // starts on the FIRST acquire() and stops only on the LAST release(), so one component can no
  // longer stop the shared root-singleton connection out from under another subscriber.
  private refCount = 0;

  // M13: Shared in-flight start promise so concurrent acquire() callers await the SAME start attempt
  // instead of racing two start() calls (which SignalR rejects unless state === Disconnected).
  private startPromise: Promise<void> | null = null;

  constructor() {
    // Register the server-to-client handlers once. The event names MUST match the backend
    // InventoryHub method names exactly (InventoryUpdated / FlashSaleStarted / FlashSaleEnded).
    this.hubConnection.on('InventoryUpdated', (payload: IInventoryUpdate) => this.inventoryUpdatedSource.next(payload));
    this.hubConnection.on('FlashSaleStarted', (payload: IFlashSale) => this.flashSaleStartedSource.next(payload));
    // M9-fe: forward the { productId, saleId } payload typed as IFlashSaleEnded.
    this.hubConnection.on('FlashSaleEnded', (payload: IFlashSaleEnded) => this.flashSaleEndedSource.next(payload));

    // M13: On an automatic reconnect, transparently rejoin the retained groups and THEN signal
    // consumers to reconcile via REST (server-to-client group membership does not survive a reconnect
    // and events may have been missed while offline). Registering the handler does not open a socket.
    this.hubConnection.onreconnected(() => {
      this.rejoinGroups().then(() => this.reconnectedSource.next());
    });
  }

  // M13: Reference-counted shared ownership. Each consuming component acquires on init; the
  // connection is started only on the first acquisition. Returns the shared start promise so callers
  // can await a live connection (and observe a start failure).
  acquire(): Promise<void> {
    this.refCount++;
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
    // While the automatic-reconnect machinery owns the connection we must not issue a manual start
    // (SignalR throws unless state === Disconnected); the reconnect + onreconnected path handles it.
    if (this.hubConnection.state !== signalR.HubConnectionState.Disconnected) {
      return Promise.resolve();
    }

    this.startPromise = this.startWithRetry(INITIAL_START_MAX_ATTEMPTS)
      // (Re)join any groups requested before the socket finished connecting.
      .then(() => this.rejoinGroups())
      .then(() => {
        this.startPromise = null;
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
      // Not connected yet; membership is retained and joined by start()/onreconnected.
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

  // Small awaitable delay used between initial-connect retries. Isolated as a method so tests can
  // stub the backoff (spy) and run the retry path instantly.
  private delay(ms: number): Promise<void> {
    return new Promise<void>(resolve => setTimeout(resolve, ms));
  }
}
