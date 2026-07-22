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

  // M13: consumers reconcile drifted state after a self-managed reconnect (F4) via this stream.
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

    it('should rejoin every retained group when connected (reconnect recovery path)', async () => {
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

  // QA finding F4 (LOW, resilience/console-hygiene): the fix removed withAutomaticReconnect() (whose
  // internal reconnect loop settles a FLOATING promise that surfaces as an "Unhandled Promise rejection"
  // console.error under zone.js) and replaced it with a self-managed onclose-driven reconnect() that
  // awaits every hubConnection.start() inside its OWN try/catch loop, so NO promise is ever left
  // floating. These tests assert that design: onclose delegates to reconnect(); reconnect() honours the
  // deliberate-stop / ref-count gates, restores the rejoin-groups + reconnected$ recovery contract on
  // success, and - critically - NEVER rejects (its try/catch owns the sustained-outage failure so
  // zone.js sees nothing).
  describe('self-managed reconnect - no floating promises (F4)', () => {
    it('exposes a reconnect() method (replacing withAutomaticReconnect)', () => {
      expect(typeof (service as any).reconnect).toBe('function');
    });

    it('wires onclose to reconnect() so an unexpected drop self-heals', () => {
      const conn = connectionOf(service);
      const reconnectSpy = spyOn<any>(service, 'reconnect').and.returnValue(Promise.resolve());
      // Simulate the socket dropping by invoking the close callbacks SignalR registered via onclose()
      // (this is exactly what HubConnection does internally on a drop). Our handler must delegate to
      // reconnect(). Reaching the internal callback list keeps the test honest about the real wiring.
      const callbacks = (conn as any)._closedCallbacks || [];
      expect(callbacks.length).toBeGreaterThan(0);
      callbacks.forEach((cb: any) => cb.apply(conn, [undefined]));

      expect(reconnectSpy).toHaveBeenCalled();
    });

    it('does NOT call start() while OFFLINE, then self-heals when connectivity returns (core F4)', async () => {
      const conn = connectionOf(service);
      setState(conn, signalR.HubConnectionState.Disconnected);
      (service as any).refCount = 1;
      (service as any).userStopped = false;
      const startSpy = spyOn(conn, 'start').and.callFake(() => {
        setState(conn, signalR.HubConnectionState.Connected);
        return Promise.resolve();
      });
      spyOn(conn, 'invoke').and.returnValue(Promise.resolve());
      // Simulate the browser being offline; reconnect() must park on the 'online' event and NOT call
      // hubConnection.start() (calling a failing start() while offline is what floats the library
      // negotiate-rejection that zone.js logs as "Unhandled Promise rejection").
      const isOnlineSpy = spyOn<any>(service, 'isOnline').and.returnValue(false);

      const done = (service as any).reconnect();  // parks on waitUntilOnline() while offline
      await Promise.resolve();
      await Promise.resolve();
      expect(startSpy).not.toHaveBeenCalled();

      // Connectivity returns: flip online and dispatch the 'online' event the wait is listening for.
      isOnlineSpy.and.returnValue(true);
      window.dispatchEvent(new Event('online'));
      await done;

      // It self-heals: start() is finally called exactly once and the connection is restored.
      expect(startSpy).toHaveBeenCalledTimes(1);
    });

    it('on an unexpected drop reconnects, rejoins retained groups and emits reconnected$', async () => {
      const conn = connectionOf(service);
      (service as any).refCount = 1;            // a consumer is present
      (service as any).userStopped = false;
      (service as any).joinedGroups.add(6);
      setState(conn, signalR.HubConnectionState.Disconnected);
      // The self-managed loop awaits hubConnection.start() directly; the first attempt succeeds and
      // flips the simulated state to Connected so the rejoin + reconnected$ contract runs.
      const startSpy = spyOn(conn, 'start').and.callFake(() => {
        setState(conn, signalR.HubConnectionState.Connected);
        return Promise.resolve();
      });
      const invokeSpy = spyOn(conn, 'invoke').and.returnValue(Promise.resolve());
      let reconnectedEmitted = false;
      const sub = service.reconnected$.subscribe(() => (reconnectedEmitted = true));

      await (service as any).reconnect();

      expect(startSpy).toHaveBeenCalledTimes(1);
      expect(invokeSpy).toHaveBeenCalledWith('JoinProductGroup', 6);
      expect(reconnectedEmitted).toBeTrue();
      sub.unsubscribe();
    });

    it('does NOT reconnect after a deliberate stop (userStopped)', async () => {
      const conn = connectionOf(service);
      setState(conn, signalR.HubConnectionState.Disconnected);
      (service as any).refCount = 1;
      (service as any).userStopped = true;
      const startSpy = spyOn(conn, 'start').and.returnValue(Promise.resolve());

      await (service as any).reconnect();

      expect(startSpy).not.toHaveBeenCalled();
    });

    it('does NOT reconnect once every consumer has released (refCount 0)', async () => {
      const conn = connectionOf(service);
      setState(conn, signalR.HubConnectionState.Disconnected);
      (service as any).refCount = 0;
      (service as any).userStopped = false;
      const startSpy = spyOn(conn, 'start').and.returnValue(Promise.resolve());

      await (service as any).reconnect();

      expect(startSpy).not.toHaveBeenCalled();
    });

    it('swallows the sustained-outage failure (never rejects) and emits ONE give-up warning', async () => {
      const conn = connectionOf(service);
      (service as any).refCount = 1;
      (service as any).userStopped = false;
      setState(conn, signalR.HubConnectionState.Disconnected);
      // Every start() attempt fails (offline) WITHOUT floating - the loop awaits each inside try/catch.
      spyOn(conn, 'start').and.returnValue(Promise.reject(new Error('offline')));
      // Skip the backoff so the bounded budget is exhausted instantly.
      spyOn<any>(service, 'delay').and.returnValue(Promise.resolve());
      const warnSpy = spyOn(console, 'warn');

      // reconnect() MUST resolve (not reject): its own try/catch owns the failure so zone.js never sees
      // an unhandled rejection (the core of F4). A single handled give-up warning keeps it diagnosable.
      await expectAsync((service as any).reconnect()).toBeResolved();
      expect(warnSpy).toHaveBeenCalledTimes(1);
    });

    it('stop() flags a deliberate teardown so a following onclose does not reconnect', async () => {
      const conn = connectionOf(service);
      setState(conn, signalR.HubConnectionState.Connected);
      spyOn(conn, 'stop').and.callFake(() => {
        setState(conn, signalR.HubConnectionState.Disconnected);
        return Promise.resolve();
      });

      await service.stop();

      expect((service as any).userStopped).toBeTrue();
    });

    it('acquire() clears the deliberate-stop flag so a later drop can reconnect', async () => {
      const conn = connectionOf(service);
      setState(conn, signalR.HubConnectionState.Disconnected);
      spyOn(conn, 'start').and.callFake(() => {
        setState(conn, signalR.HubConnectionState.Connected);
        return Promise.resolve();
      });
      (service as any).userStopped = true;

      await service.acquire();

      expect((service as any).userStopped).toBeFalse();
    });
  });
});
