import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CommonModule } from '@angular/common';
import { RouterTestingModule } from '@angular/router/testing';
import { NO_ERRORS_SCHEMA } from '@angular/core';
import { BehaviorSubject, Observable, ReplaySubject } from 'rxjs';

import { BasketComponent } from './basket.component';
import { BasketService } from './basket.service';
import { StockService } from '../core/services/stock.service';
import { IBasket, IBasketItem } from '../shared/models/basket';

/**
 * Unit tests for BasketComponent's mid-session zero-stock checkout gating.
 *
 * The suite is fully hermetic: BasketService and StockService are replaced with
 * lightweight mocks so no HTTP request and no SignalR/WebSocket is ever opened.
 * basket$ is driven by a BehaviorSubject that mirrors the real service (which
 * seeds null), so the component's null/empty-basket guard is exercised on init.
 * Each product's live stock is driven by a per-id ReplaySubject standing in for
 * StockService's per-product stream. The basket template renders the child
 * components app-basket-summary and app-order-totals; this spec targets the
 * controller logic only, so those elements are ignored via NO_ERRORS_SCHEMA -
 * the same pattern the other component specs in this workspace use.
 */
describe('BasketComponent', () => {
  let component: BasketComponent;
  let fixture: ComponentFixture<BasketComponent>;

  let basketSubject: BehaviorSubject<IBasket>;
  let stockSubjects: Map<number, ReplaySubject<number>>;

  let basketServiceMock: {
    basket$: Observable<IBasket>;
    basketTotal$: Observable<any>;
    removeItemFromBasket: jasmine.Spy;
    incrementItemQuantity: jasmine.Spy;
    decrementItemQuantity: jasmine.Spy;
  };
  let stockServiceMock: {
    subscribeToProduct: jasmine.Spy;
    getStock$: jasmine.Spy;
  };

  // Arrange helper: build a basket item matching the IBasketItem contract. A
  // basket item's id IS the product id, which is what stock is keyed on.
  const makeItem = (id: number): IBasketItem => ({
    id,
    productName: 'Product ' + id,
    price: 10,
    quantity: 1,
    pictureUrl: 'img/' + id + '.png',
    brand: 'BrandName',
    type: 'TypeName'
  });

  const makeBasket = (items: IBasketItem[]): IBasket => ({ id: 'basket-1', items });

  // Lazily create (or return) the per-product stock stream the mock StockService
  // hands back, so a test can push live stock values for a given product id.
  const stockStream = (id: number): ReplaySubject<number> => {
    let subject = stockSubjects.get(id);
    if (!subject) {
      subject = new ReplaySubject<number>(1);
      stockSubjects.set(id, subject);
    }
    return subject;
  };

  beforeEach(async () => {
    // BehaviorSubject(null) mirrors the real BasketService.basket$ so the
    // component receives null first (guarded) before any real basket.
    basketSubject = new BehaviorSubject<IBasket>(null);
    stockSubjects = new Map<number, ReplaySubject<number>>();

    basketServiceMock = {
      basket$: basketSubject.asObservable(),
      basketTotal$: new BehaviorSubject<any>(null).asObservable(),
      removeItemFromBasket: jasmine.createSpy('removeItemFromBasket'),
      incrementItemQuantity: jasmine.createSpy('incrementItemQuantity'),
      decrementItemQuantity: jasmine.createSpy('decrementItemQuantity')
    };

    stockServiceMock = {
      subscribeToProduct: jasmine.createSpy('subscribeToProduct'),
      getStock$: jasmine
        .createSpy('getStock$')
        .and.callFake((id: number) => stockStream(id).asObservable())
    };

    await TestBed.configureTestingModule({
      // CommonModule supplies *ngIf/async; RouterTestingModule supplies routerLink.
      imports: [CommonModule, RouterTestingModule],
      declarations: [BasketComponent],
      providers: [
        { provide: BasketService, useValue: basketServiceMock },
        { provide: StockService, useValue: stockServiceMock }
      ],
      schemas: [NO_ERRORS_SCHEMA]
    }).compileComponents();

    fixture = TestBed.createComponent(BasketComponent);
    component = fixture.componentInstance;
    // First change-detection runs ngOnInit; basket$ replays null (guarded).
    fixture.detectChanges();
  });

  it('should create with hasOutOfStockItem defaulting to false', () => {
    expect(component).toBeTruthy();
    expect(component.hasOutOfStockItem).toBe(false);
  });

  it('exposes basket$ and basketTotals$ from BasketService in ngOnInit', () => {
    // The two pre-existing observable assignments must be preserved verbatim.
    expect(component.basket$).toBe(basketServiceMock.basket$);
    expect(component.basketTotals$).toBe(basketServiceMock.basketTotal$);
  });

  it('ignores a null or item-less basket emission (no stock tracking)', () => {
    basketSubject.next(null);
    basketSubject.next({ id: 'b', items: null } as IBasket);

    expect(stockServiceMock.subscribeToProduct).not.toHaveBeenCalled();
    expect(stockServiceMock.getStock$).not.toHaveBeenCalled();
  });

  it('tracks stock for each basket item (subscribeToProduct + getStock$ per id)', () => {
    basketSubject.next(makeBasket([makeItem(1), makeItem(2)]));

    expect(stockServiceMock.subscribeToProduct).toHaveBeenCalledWith(1);
    expect(stockServiceMock.subscribeToProduct).toHaveBeenCalledWith(2);
    expect(stockServiceMock.getStock$).toHaveBeenCalledWith(1);
    expect(stockServiceMock.getStock$).toHaveBeenCalledWith(2);
  });

  it('sets hasOutOfStockItem to true when any tracked product stock hits 0', () => {
    basketSubject.next(makeBasket([makeItem(1), makeItem(2)]));

    stockStream(1).next(5);
    stockStream(2).next(3);
    expect(component.hasOutOfStockItem).toBe(false);

    stockStream(2).next(0);
    expect(component.hasOutOfStockItem).toBe(true);
  });

  it('recomputes hasOutOfStockItem back to false when the product is replenished', () => {
    basketSubject.next(makeBasket([makeItem(1)]));

    stockStream(1).next(0);
    expect(component.hasOutOfStockItem).toBe(true);

    stockStream(1).next(4);
    expect(component.hasOutOfStockItem).toBe(false);
  });

  it('does not subscribe twice for the same product across repeated basket emissions', () => {
    const basket = makeBasket([makeItem(1)]);
    basketSubject.next(basket);
    basketSubject.next(basket); // an increment/remove re-emits basket$

    expect(stockServiceMock.subscribeToProduct).toHaveBeenCalledTimes(1);
    expect(stockServiceMock.getStock$).toHaveBeenCalledTimes(1);
  });

  it('unsubscribes every subscription on destroy (no further state changes)', () => {
    basketSubject.next(makeBasket([makeItem(1)]));
    stockStream(1).next(5);
    expect(component.hasOutOfStockItem).toBe(false);

    fixture.destroy(); // triggers ngOnDestroy

    // A post-destroy stock drop must NOT flip the flag (stock sub torn down)...
    stockStream(1).next(0);
    expect(component.hasOutOfStockItem).toBe(false);

    // ...and a new basket must NOT begin tracking (basket sub torn down).
    basketSubject.next(makeBasket([makeItem(2)]));
    expect(stockServiceMock.subscribeToProduct).not.toHaveBeenCalledWith(2);
  });

  it('delegates the three basket handlers to BasketService unchanged', () => {
    const item = makeItem(1);

    component.removeBasketItem(item);
    component.incrementItemQuantity(item);
    component.decrementItemQuantity(item);

    expect(basketServiceMock.removeItemFromBasket).toHaveBeenCalledWith(item);
    expect(basketServiceMock.incrementItemQuantity).toHaveBeenCalledWith(item);
    expect(basketServiceMock.decrementItemQuantity).toHaveBeenCalledWith(item);
  });
});
