import { TestBed } from '@angular/core/testing';

import { InventoryHubService } from './inventory-hub.service';

// Unit tests for InventoryHubService. Unlike the HTTP-backed services (see
// shop.service.spec.ts), this service performs NO HTTP, so HttpClientTestingModule is
// intentionally omitted. Critically, InventoryHubService.start() opens a REAL SignalR
// WebSocket connection, so these tests NEVER call start()/stop() against a live server:
// constructing the service only builds the connection object and registers handlers (no
// socket is opened until start()), so we verify construction and that the RxJS event
// streams are present and subscribable. Any lifecycle exercise must spy on the underlying
// connection rather than open a real socket.
describe('InventoryHubService', () => {
  let service: InventoryHubService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [InventoryHubService]
    });
    service = TestBed.inject(InventoryHubService);
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });

  it('should expose subscribable server-to-client event streams without opening a connection', () => {
    expect(service.inventoryUpdated$).toBeTruthy();
    expect(service.flashSaleStarted$).toBeTruthy();
    expect(service.flashSaleEnded$).toBeTruthy();

    // Subscribing must not throw and must return a Subscription; no live WebSocket is opened
    // because start() is never called.
    const subscription = service.inventoryUpdated$.subscribe();
    expect(subscription).toBeTruthy();
    subscription.unsubscribe();
  });
});
