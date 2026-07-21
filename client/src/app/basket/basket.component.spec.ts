import { TestBed, ComponentFixture } from '@angular/core/testing';
import { Component, EventEmitter, Input, Output, NO_ERRORS_SCHEMA } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterTestingModule } from '@angular/router/testing';
import { of, BehaviorSubject, Observable, ReplaySubject } from 'rxjs';

import { BasketComponent } from './basket.component';
import { BasketService } from './basket.service';
import { StockService } from '../core/services/stock.service';
import { IBasket, IBasketItem } from '../shared/models/basket';

/**
 * Lightweight stub for the <app-basket-summary> child. Declares the EXACT surface
 * the basket template binds ([items] input plus the decrement/increment/remove
 * outputs) so the real template compiles WITHOUT NO_ERRORS_SCHEMA. Stubbing keeps
 * the suite lighter than importing the full SharedModule.
 */
@Component({ selector: 'app-basket-summary', template: '' })
class BasketSummaryStubComponent {
  @Input() items: IBasketItem[];
  @Output() decrement = new EventEmitter<IBasketItem>();
  @Output() increment = new EventEmitter<IBasketItem>();
  @Output() remove = new EventEmitter<IBasketItem>();
}

/**
 * Lightweight stub for the <app-order-totals> child. Declares the three price
 * inputs the basket template binds so the real template compiles WITHOUT
 * NO_ERRORS_SCHEMA.
 */
@Component({ selector: 'app-order-totals', template: '' })
class OrderTotalsStubComponent {
  @Input() shippingPrice: number;
  @Input() subtotal: number;
  @Input() total: number;
}

/**
 * Template-level checkout gating: compiles the real basket template (with child
 * stubs, no NO_ERRORS_SCHEMA) so the out-of-stock alert and the disabled
 * proceed-to-checkout anchor are asserted against the rendered DOM.
 */
describe('BasketComponent (template gating)', () => {
  let component: BasketComponent;
  let fixture: ComponentFixture<BasketComponent>;
  let basketServiceStub: { basket$: any; basketTotal$: any };
  let stockServiceStub: { subscribeToProduct: jasmine.Spy; getStock$: jasmine.Spy; unsubscribeFromProduct: jasmine.Spy };

  // A basket item's id IS the product id, so stock is tracked on item.id. Two items
  // (ids 1 and 2) make the per-item subscribe assertions and the "ANY item at zero"
  // gating semantics meaningful.
  const mockBasket = {
    id: 'basket-1',
    items: [
      { id: 1, productName: 'Prod 1', price: 10, quantity: 1, pictureUrl: '', brand: 'b', type: 't' },
      { id: 2, productName: 'Prod 2', price: 20, quantity: 2, pictureUrl: '', brand: 'b', type: 't' }
    ]
  };

  beforeEach(async () => {
    // Stub BasketService via useValue so its real HttpClient is never constructed.
    // basket$ replays the two-item basket synchronously; basketTotal$ feeds the
    // <app-order-totals> stub's *ngIf and price inputs.
    basketServiceStub = {
      basket$: of(mockBasket),
      basketTotal$: of({ shipping: 0, subtotal: 50, total: 50 })
    };

    // Stub StockService via useValue so its real constructor (which builds a live
    // real-time hub connection) never runs and no socket is opened. subscribeToProduct is a spy
    // asserted per item id; getStock$ returns an in-stock (5) stream by default.
    // Individual tests override the getStock$ return BEFORE detectChanges to drive
    // the out-of-stock gating.
    stockServiceStub = {
      subscribeToProduct: jasmine.createSpy('subscribeToProduct'),
      getStock$: jasmine.createSpy('getStock$').and.returnValue(of(5)),
      // Mirror the real StockService surface: the component releases a product's
      // hub subscription when it leaves the basket (QA R1 / prune-on-removal).
      unsubscribeFromProduct: jasmine.createSpy('unsubscribeFromProduct')
    };

    await TestBed.configureTestingModule({
      declarations: [
        BasketComponent,
        BasketSummaryStubComponent,
        OrderTotalsStubComponent
      ],
      imports: [
        // Real template compilation (no NO_ERRORS_SCHEMA masking): CommonModule
        // supplies the async pipe and *ngIf; RouterTestingModule supplies the
        // routerLink="/checkout" on the proceed anchor.
        CommonModule,
        RouterTestingModule
      ],
      providers: [
        { provide: BasketService, useValue: basketServiceStub },
        { provide: StockService, useValue: stockServiceStub }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(BasketComponent);
    component = fixture.componentInstance;
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('subscribes to live stock for each basket item id on init', () => {
    // detectChanges() runs ngOnInit, which subscribes to basket$ and tracks each item.
    fixture.detectChanges();

    expect(stockServiceStub.subscribeToProduct).toHaveBeenCalledWith(1);
    expect(stockServiceStub.subscribeToProduct).toHaveBeenCalledWith(2);
  });

  it('gates checkout and renders the alert when any tracked item is out of stock', () => {
    // Drive item 1 to zero stock (item 2 stays in stock) BEFORE detectChanges so the
    // synchronous of(...) emissions in ngOnInit set hasOutOfStockItem before the view
    // bindings are evaluated in the same change-detection pass.
    stockServiceStub.getStock$.and.callFake((id: number) => of(id === 1 ? 0 : 5));

    fixture.detectChanges();

    expect(component.hasOutOfStockItem).toBe(true);
    // The out-of-stock alert is rendered by the real template's *ngIf.
    expect(fixture.nativeElement.querySelector('.alert.alert-danger')).toBeTruthy();
    // The proceed-to-checkout anchor is gated with the Bootstrap disabled class.
    const proceed = fixture.nativeElement.querySelector('a.btn-outline-primary');
    expect(proceed.classList.contains('disabled')).toBe(true);
    // ...and exposes the accessible disabled state for assistive technology.
    expect(proceed.getAttribute('aria-disabled')).toBe('true');
  });

  it('does not gate checkout and hides the alert when all items are in stock', () => {
    // All tracked products report positive stock, so nothing is gated.
    stockServiceStub.getStock$.and.returnValue(of(5));

    fixture.detectChanges();

    expect(component.hasOutOfStockItem).toBe(false);
    expect(fixture.nativeElement.querySelector('.alert.alert-danger')).toBeNull();
    const proceed = fixture.nativeElement.querySelector('a.btn-outline-primary');
    expect(proceed.classList.contains('disabled')).toBe(false);
  });
});

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
describe('BasketComponent (stock tracking logic)', () => {
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
    unsubscribeFromProduct: jasmine.Spy;
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
        .and.callFake((id: number) => stockStream(id).asObservable()),
      // The component calls this when a product leaves the basket so the service
      // can release its per-product hub tracking (QA R1 / prune-on-removal).
      unsubscribeFromProduct: jasmine.createSpy('unsubscribeFromProduct')
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

  // ---------------------------------------------------------------------------
  // QA H1 (MAJOR) — the checkout gate must CLEAR when the offending product is
  // removed from the basket or the basket is emptied. Before the fix the gate was
  // a one-way latch: a departed item's stale `0` stayed in the component's stock
  // map and kept `.some(v => v === 0)` true forever. These specs lock the
  // AAP-documented recovery flow ("remove the zero-stock item and continue").
  // ---------------------------------------------------------------------------

  it('H1a: removing the out-of-stock item clears the checkout gate', () => {
    // Two items; item 2 goes to zero -> gate ON.
    basketSubject.next(makeBasket([makeItem(1), makeItem(2)]));
    stockStream(1).next(5);
    stockStream(2).next(0);
    expect(component.hasOutOfStockItem).toBe(true);

    // The shopper removes item 2, so basket$ re-emits with only item 1. The gate
    // must recompute from the CURRENT basket and clear.
    basketSubject.next(makeBasket([makeItem(1)]));
    expect(component.hasOutOfStockItem).toBe(false);

    // The departed product's per-product hub tracking is released (R1).
    expect(stockServiceMock.unsubscribeFromProduct).toHaveBeenCalledWith(2);
  });

  it('H1b: emptying the basket (null) clears the checkout gate', () => {
    basketSubject.next(makeBasket([makeItem(1), makeItem(2)]));
    stockStream(2).next(0);
    expect(component.hasOutOfStockItem).toBe(true);

    // A null basket (fully emptied) must clear the gate, not leave it latched.
    basketSubject.next(null);
    expect(component.hasOutOfStockItem).toBe(false);
  });

  it('H1c: emptying the basket (items: []) clears the checkout gate', () => {
    basketSubject.next(makeBasket([makeItem(1), makeItem(2)]));
    stockStream(2).next(0);
    expect(component.hasOutOfStockItem).toBe(true);

    // An empty items array must clear the gate too.
    basketSubject.next(makeBasket([]));
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

  it('releases every still-tracked product hub id on destroy (P4-12 teardown)', () => {
    basketSubject.next(makeBasket([makeItem(1), makeItem(2)]));
    stockStream(1).next(5);
    stockStream(2).next(3);
    expect(stockServiceMock.subscribeToProduct).toHaveBeenCalledWith(1);
    expect(stockServiceMock.subscribeToProduct).toHaveBeenCalledWith(2);

    fixture.destroy(); // triggers ngOnDestroy

    // Navigating away with items still in the basket must release BOTH products'
    // per-product hub tracking so the server groups are left (P4-12).
    expect(stockServiceMock.unsubscribeFromProduct).toHaveBeenCalledWith(1);
    expect(stockServiceMock.unsubscribeFromProduct).toHaveBeenCalledWith(2);
  });
});
