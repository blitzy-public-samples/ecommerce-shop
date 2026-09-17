import { TestBed } from '@angular/core/testing';
import { Router, UrlTree } from '@angular/router';

import { StockGuard } from './stock.guard';
import { BasketService } from '../../basket/basket.service';
import { StockService } from '../services/stock.service';
import { IBasket, IBasketItem } from '../../shared/models/basket';

/**
 * Unit tests for the /checkout StockGuard (QA H-C direct-navigation defense).
 *
 * The guard is fully hermetic: BasketService and StockService are replaced with
 * lightweight mocks (no HTTP, no SignalR). A sentinel UrlTree returned by a
 * Router.createUrlTree spy lets us assert the /basket redirect without a real
 * router configuration.
 */
describe('StockGuard', () => {
  let guard: StockGuard;
  let basketServiceMock: { getCurrentBasketValue: jasmine.Spy };
  let stockServiceMock: { getCurrentStock: jasmine.Spy };
  let routerMock: { createUrlTree: jasmine.Spy };
  const sentinelUrlTree = {} as UrlTree;

  const makeItem = (id: number, quantity = 1): IBasketItem => ({
    id,
    productName: 'Product ' + id,
    price: 10,
    quantity,
    pictureUrl: 'img/' + id + '.png',
    brand: 'BrandName',
    type: 'TypeName'
  });

  const makeBasket = (items: IBasketItem[]): IBasket => ({ id: 'basket-1', items });

  beforeEach(() => {
    basketServiceMock = { getCurrentBasketValue: jasmine.createSpy('getCurrentBasketValue') };
    stockServiceMock = { getCurrentStock: jasmine.createSpy('getCurrentStock') };
    routerMock = {
      createUrlTree: jasmine.createSpy('createUrlTree').and.returnValue(sentinelUrlTree)
    };

    TestBed.configureTestingModule({
      providers: [
        StockGuard,
        { provide: BasketService, useValue: basketServiceMock },
        { provide: StockService, useValue: stockServiceMock },
        { provide: Router, useValue: routerMock }
      ]
    });

    guard = TestBed.inject(StockGuard);
  });

  it('is created', () => {
    expect(guard).toBeTruthy();
  });

  it('allows activation when there is no basket', () => {
    basketServiceMock.getCurrentBasketValue.and.returnValue(null);
    expect(guard.canActivate()).toBe(true);
    expect(stockServiceMock.getCurrentStock).not.toHaveBeenCalled();
  });

  it('allows activation when the basket has no items', () => {
    basketServiceMock.getCurrentBasketValue.and.returnValue(makeBasket([]));
    expect(guard.canActivate()).toBe(true);
  });

  it('allows activation when every line has sufficient known stock', () => {
    basketServiceMock.getCurrentBasketValue.and.returnValue(makeBasket([makeItem(1, 1), makeItem(2, 2)]));
    stockServiceMock.getCurrentStock.and.callFake((id: number) => (id === 1 ? 5 : 4));

    expect(guard.canActivate()).toBe(true);
    expect(routerMock.createUrlTree).not.toHaveBeenCalled();
  });

  it('allows activation when stock is unknown (does not block fresh navigation)', () => {
    basketServiceMock.getCurrentBasketValue.and.returnValue(makeBasket([makeItem(1, 1)]));
    stockServiceMock.getCurrentStock.and.returnValue(undefined);

    expect(guard.canActivate()).toBe(true);
    expect(routerMock.createUrlTree).not.toHaveBeenCalled();
  });

  it('allows activation when known stock exactly equals the requested quantity', () => {
    basketServiceMock.getCurrentBasketValue.and.returnValue(makeBasket([makeItem(1, 2)]));
    stockServiceMock.getCurrentStock.and.returnValue(2);

    expect(guard.canActivate()).toBe(true);
  });

  it('redirects to /basket when a line is out of stock (known 0)', () => {
    basketServiceMock.getCurrentBasketValue.and.returnValue(makeBasket([makeItem(1, 1), makeItem(2, 1)]));
    stockServiceMock.getCurrentStock.and.callFake((id: number) => (id === 2 ? 0 : 5));

    const result = guard.canActivate();

    expect(result).toBe(sentinelUrlTree);
    expect(routerMock.createUrlTree).toHaveBeenCalledWith(['/basket']);
  });

  it('redirects to /basket when a line has known stock below its quantity (F9)', () => {
    basketServiceMock.getCurrentBasketValue.and.returnValue(makeBasket([makeItem(1, 2)]));
    stockServiceMock.getCurrentStock.and.returnValue(1); // only 1 for a requested 2

    const result = guard.canActivate();

    expect(result).toBe(sentinelUrlTree);
    expect(routerMock.createUrlTree).toHaveBeenCalledWith(['/basket']);
  });
});
