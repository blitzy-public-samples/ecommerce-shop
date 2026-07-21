import { TestBed } from '@angular/core/testing';
import { HubConnectionBuilder } from '@microsoft/signalr';

import { StockService } from './stock.service';

/**
 * Unit tests for StockService - the Angular client of the Real-Time Inventory &
 * Flash-Sale feature's SignalR bridge.
 *
 * The suite is fully hermetic: no real WebSocket is ever opened. StockService
 * builds its hub connection inside its CONSTRUCTOR, so every test stubs
 * HubConnectionBuilder.prototype.build to return a fake HubConnection BEFORE the
 * service is instantiated by TestBed.inject. The fake exposes only the members the
 * service touches: start, on, invoke and onreconnected - and captures the
 * 'StockChanged' and reconnected callbacks so tests can drive a server push or a
 * reconnect deterministically, free of any network or timing dependency.
 *
 * Because subscribeToProduct chains its invoke off the resolved startConnection()
 * promise, the tests that assert invoke arguments await startConnection() to drain
 * the microtask queue before asserting.
 */
describe('StockService', () => {
  let service: StockService;
  let hubSpy: jasmine.SpyObj<any>;
  let stockChangedCb: (productId: number, currentStock: number) => void;
  let reconnectedCb: () => void;

  beforeEach(() => {
    // Arrange: a fake HubConnection exposing only the members StockService uses.
    // start/invoke resolve so the service's promise chains settle; on and
    // onreconnected capture the callbacks the constructor registers so tests can
    // simulate a server push and a reconnect on demand.
    hubSpy = jasmine.createSpyObj('HubConnection', ['start', 'on', 'invoke', 'onreconnected']);
    hubSpy.start.and.returnValue(Promise.resolve());
    hubSpy.invoke.and.returnValue(Promise.resolve(7));
    hubSpy.on.and.callFake((method: string, cb: (productId: number, currentStock: number) => void) => {
      if (method === 'StockChanged') {
        stockChangedCb = cb;
      }
    });
    hubSpy.onreconnected.and.callFake((cb: () => void) => {
      reconnectedCb = cb;
    });

    // Critical: install the build spy BEFORE TestBed.inject(StockService) so the
    // constructor receives the fake connection and never opens a real socket.
    spyOn(HubConnectionBuilder.prototype, 'build').and.returnValue(hubSpy);

    TestBed.configureTestingModule({});
    service = TestBed.inject(StockService);
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });

  it('should emit stock pushed by a StockChanged broadcast on getStock$', () => {
    // Act: subscribe first, then simulate the server push via the captured callback.
    let emitted: number;
    service.getStock$(1).subscribe(stock => (emitted = stock));

    stockChangedCb(1, 42);

    // Assert: the per-product ReplaySubject relayed the broadcast value.
    expect(emitted).toBe(42);
  });

  it('should invoke SubscribeToProduct on the hub with the product id', async () => {
    // Act: subscribe, then await startConnection() to drain the microtask queue so
    // the invoke chained off the resolved start promise has run.
    service.subscribeToProduct(1);
    await service.startConnection();

    // Assert: the exact server method name and id were sent to the hub.
    expect(hubSpy.invoke).toHaveBeenCalledWith('SubscribeToProduct', 1);
  });

  it('should re-subscribe to every tracked product on reconnect (full refetch)', async () => {
    // Arrange: track two products and let their initial subscribe invokes settle.
    service.subscribeToProduct(1);
    service.subscribeToProduct(2);
    await service.startConnection();

    // Act: discard the initial invokes, then trigger the captured reconnect handler.
    hubSpy.invoke.calls.reset();
    reconnectedCb();

    // Assert: reconnect refetches current state for ALL tracked products.
    expect(hubSpy.invoke).toHaveBeenCalledWith('SubscribeToProduct', 1);
    expect(hubSpy.invoke).toHaveBeenCalledWith('SubscribeToProduct', 2);
  });
});
