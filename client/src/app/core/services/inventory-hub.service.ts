import { Injectable } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { environment } from '../../../environments/environment';
import { IFlashSale } from '../../shared/models/flash-sale';
import { IInventoryUpdate } from '../../shared/models/inventory';

// Real-Time Inventory & Flash Sale - SignalR client wrapper.
// Surfaces the three server-to-client hub events as RxJS observables consumed by the
// shop widgets and product-details.component. Authentication reuses the existing JWT
// scheme: a WebSocket cannot carry an Authorization header, so the token is supplied via
// accessTokenFactory reading the SAME localStorage 'token' key used by
// core/interceptors/jwt.interceptor.ts. No second auth mechanism is introduced, and the
// hub is single-instance / in-memory (no Redis backplane).
@Injectable({
  providedIn: 'root'
})
export class InventoryHubService {
  private hubConnection: signalR.HubConnection =
    new signalR.HubConnectionBuilder()
      .withUrl(environment.hubUrl, { accessTokenFactory: () => localStorage.getItem('token') || '' })
      .withAutomaticReconnect()
      .build();

  private inventoryUpdatedSource = new Subject<IInventoryUpdate>();
  inventoryUpdated$ = this.inventoryUpdatedSource.asObservable();
  private flashSaleStartedSource = new Subject<IFlashSale>();
  flashSaleStarted$ = this.flashSaleStartedSource.asObservable();
  private flashSaleEndedSource = new Subject<IFlashSale>();
  flashSaleEnded$ = this.flashSaleEndedSource.asObservable();

  constructor() {
    // Register the server-to-client handlers once. The event names MUST match the backend
    // InventoryHub method names exactly (InventoryUpdated / FlashSaleStarted / FlashSaleEnded).
    this.hubConnection.on('InventoryUpdated', (payload: IInventoryUpdate) => this.inventoryUpdatedSource.next(payload));
    this.hubConnection.on('FlashSaleStarted', (payload: IFlashSale) => this.flashSaleStartedSource.next(payload));
    this.hubConnection.on('FlashSaleEnded', (payload: IFlashSale) => this.flashSaleEndedSource.next(payload));
  }

  // Start the connection, guarded against a double-start. A failed hub connection must
  // never crash product-details, so the error is swallowed/logged (console.error is lint-allowed).
  start(): Promise<void> {
    if (this.hubConnection.state !== signalR.HubConnectionState.Disconnected) {
      return Promise.resolve();
    }
    return this.hubConnection.start().catch(err => console.error('InventoryHub start failed', err));
  }

  // Stop the connection, guarded by state; errors swallowed/logged.
  stop(): Promise<void> {
    if (this.hubConnection.state === signalR.HubConnectionState.Disconnected) {
      return Promise.resolve();
    }
    return this.hubConnection.stop().catch(err => console.error('InventoryHub stop failed', err));
  }

  // Best-effort per-product group subscription so product-details receives only relevant
  // updates (AAP 0.4.3 "joins the product's hub group ... leaves on destroy"). The hub is
  // primarily server-to-client; if the backend does not expose these invokable methods the
  // rejected promise is swallowed, making these safe no-ops.
  joinProductGroup(productId: number): Promise<void> {
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected) {
      return Promise.resolve();
    }
    return this.hubConnection.invoke('JoinProductGroup', productId)
      .catch(err => console.error('InventoryHub joinProductGroup failed', err));
  }

  leaveProductGroup(productId: number): Promise<void> {
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected) {
      return Promise.resolve();
    }
    return this.hubConnection.invoke('LeaveProductGroup', productId)
      .catch(err => console.error('InventoryHub leaveProductGroup failed', err));
  }
}
