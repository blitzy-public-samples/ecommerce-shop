import { TestBed } from '@angular/core/testing';
import * as signalR from '@microsoft/signalr';

import { InventoryHubService } from './inventory-hub.service';

// Unit tests for InventoryHubService. Unlike the HTTP-backed services (see
// shop.service.spec.ts), this service performs NO HTTP, so HttpClientTestingModule is
// intentionally omitted. Critically, InventoryHubService.start() opens a REAL SignalR
// WebSocket connection, so these tests NEVER call start()/stop() against a live server:
// constructing the service only builds the connection object and registers handlers (no
// socket is opened until start()), so we verify construction and that the RxJS event
// streams are present and subscribable. Any lifecycle exercise (M13) spies on the underlying
// connection (start/stop/invoke) and stubs its `state` getter rather than opening a real socket.
describe('InventoryHubService', () => {
  let service: InventoryHubService;

  // Reach the private hub connection so lifecycle can be exercised through spies.
  const connectionOf = (svc: InventoryHubService): any => (svc as any).hubConnection;

  // The real HubConnection exposes `state` as a getter; shadow it on the instance to simulate
  // connection-state transitions deterministically (configurable so it can be redefined per step).
  const setState = (conn: any, state: signalR.HubConnectionState): void => {
    Object.defineProperty(conn, 'state', { get: () => state, configurable: true });
  };

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

  // M13: consumers reconcile drifted state after an automatic reconnect via this stream.
  it('should expose a subscribable reconnected$ signal without opening a connection', () => {
    expect(service.reconnected$).toBeTruthy();
    const subscription = service.reconnected$.subscribe();
    expect(subscription).toBeTruthy();
    subscription.unsubscribe();
  });

  describe('reference-counted shared ownership (M13)', () => {
    it('should start once for the first acquire and stop only on the last release', async () => {
      const conn = connectionOf(service);
      setState(conn, signalR.HubConnectionState.Disconnected);
      // Starting flips the simulated state to Connected so a second acquire() is a no-op start.
      const startSpy = spyOn(conn, 'start').and.callFake(() => {
        setState(conn, signalR.HubConnectionState.Connected);
        return Promise.resolve();
      });
      const stopSpy = spyOn(conn, 'stop').and.callFake(() => {
        setState(conn, signalR.HubConnectionState.Disconnected);
        return Promise.resolve();
      });

      await service.acquire(); // first consumer -> starts the shared connection
      await service.acquire(); // second consumer -> shares, must not start again
      expect(startSpy).toHaveBeenCalledTimes(1);

      await service.release(); // one consumer remains -> connection stays open
      expect(stopSpy).not.toHaveBeenCalled();

      await service.release(); // last consumer released -> connection stops
      expect(stopSpy).toHaveBeenCalledTimes(1);
    });
  });

  describe('initial-start error handling (M13)', () => {
    it('should surface the connection failure instead of swallowing it into a fulfilled promise', async () => {
      const conn = connectionOf(service);
      setState(conn, signalR.HubConnectionState.Disconnected);
      spyOn(conn, 'start').and.returnValue(Promise.reject(new Error('connect failed')));
      // Skip the retry backoff so the bounded retries run instantly.
      spyOn<any>(service, 'delay').and.returnValue(Promise.resolve());
      // Suppress + assert the expected diagnostic logging (this test intentionally forces failure).
      const errorSpy = spyOn(console, 'error');
      spyOn(console, 'warn');

      await expectAsync(service.start()).toBeRejectedWithError('connect failed');
      expect(errorSpy).toHaveBeenCalled();
    });
  });

  describe('retained group registry + reconnect rejoin (M13)', () => {
    it('should retain group membership even before the connection is up (deferred join)', async () => {
      const conn = connectionOf(service);
      setState(conn, signalR.HubConnectionState.Disconnected);
      const invokeSpy = spyOn(conn, 'invoke').and.returnValue(Promise.resolve());

      await service.joinProductGroup(42);

      // Not connected -> the invoke is deferred, but the membership is retained for (re)join.
      expect(invokeSpy).not.toHaveBeenCalled();
      expect(Array.from((service as any).joinedGroups)).toContain(42);
    });

    it('should rejoin every retained group when connected (onreconnected recovery path)', async () => {
      const conn = connectionOf(service);
      setState(conn, signalR.HubConnectionState.Connected);
      const invokeSpy = spyOn(conn, 'invoke').and.returnValue(Promise.resolve());
      (service as any).joinedGroups.add(7);
      (service as any).joinedGroups.add(9);

      await (service as any).rejoinGroups();

      expect(invokeSpy).toHaveBeenCalledWith('JoinProductGroup', 7);
      expect(invokeSpy).toHaveBeenCalledWith('JoinProductGroup', 9);
    });

    it('should drop a group from the registry on leaveProductGroup', async () => {
      const conn = connectionOf(service);
      setState(conn, signalR.HubConnectionState.Disconnected);
      spyOn(conn, 'invoke').and.returnValue(Promise.resolve());

      await service.joinProductGroup(5);
      expect(Array.from((service as any).joinedGroups)).toContain(5);

      await service.leaveProductGroup(5);
      expect(Array.from((service as any).joinedGroups)).not.toContain(5);
    });
  });
});
