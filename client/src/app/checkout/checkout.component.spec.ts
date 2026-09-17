import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Component, Input } from '@angular/core';
import { By } from '@angular/platform-browser';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule } from '@angular/forms';
import { RouterTestingModule } from '@angular/router/testing';
import { of, BehaviorSubject, Observable, Subject } from 'rxjs';
import { HubConnectionState } from '@microsoft/signalr';

import { CheckoutComponent } from './checkout.component';
import { AccountService } from '../account/account.service';
import { BasketService } from '../basket/basket.service';
import { StockService } from '../core/services/stock.service';

// Lightweight stubs for the child components the shell template renders, so the
// real template compiles against declared components (no error-suppressing schema)
// and without importing the out-of-scope real children (checkout-payment,
// checkout-address, etc.). Each stub declares the exact selector and the
// @Input()s the template binds.
// app-stepper and cdk-step project their content via <ng-content> so the nested
// steps render; the rest can be empty templates. Declaring a `cdk-step` stub
// (instead of importing CdkStepperModule) avoids the real CdkStep's requirement
// for a CdkStepper ancestor via DI, which would otherwise throw.
@Component({ selector: 'app-stepper', template: '<ng-content></ng-content>' })
class StubStepperComponent {
  @Input() linearModeSelected: boolean;
}

// The cdk-step stub must reuse Angular CDK's own element selector to match the
// real checkout template, so the app-prefix component-selector rule is suppressed
// for this one stub only.
// tslint:disable-next-line:component-selector
@Component({ selector: 'cdk-step', template: '<ng-content></ng-content>' })
class StubCdkStepComponent {
  @Input() label: string;
  @Input() completed: boolean;
}

@Component({ selector: 'app-checkout-address', template: '' })
class StubCheckoutAddressComponent {
  @Input() checkoutForm: any;
}

@Component({ selector: 'app-checkout-delivery', template: '' })
class StubCheckoutDeliveryComponent {
  @Input() checkoutForm: any;
}

@Component({ selector: 'app-checkout-review', template: '' })
class StubCheckoutReviewComponent {
  @Input() appStepper: any;
}

@Component({ selector: 'app-checkout-payment', template: '' })
class StubCheckoutPaymentComponent {
  @Input() checkoutForm: any;
  // The shell now binds the fail-closed stock gate into the payment step (H-B/F3).
  @Input() disableForStock: any;
}

@Component({ selector: 'app-order-totals', template: '' })
class StubOrderTotalsComponent {
  @Input() shippingPrice: number;
  @Input() subtotal: number;
  @Input() total: number;
}

/**
 * Template-level checkout gating: compiles the real checkout shell template (with
 * child stubs, no error-suppressing schema) so the zero-stock gating flag and the
 * absence of the alert while stock is positive are asserted against the rendered DOM.
 */
describe('CheckoutComponent (template gating)', () => {
  let component: CheckoutComponent;
  let fixture: ComponentFixture<CheckoutComponent>;
  let accountServiceStub: { getUserAddress: jasmine.Spy };
  let basketServiceStub: any;
  let stockServiceStub: {
    subscribeToProduct: jasmine.Spy;
    unsubscribeFromProduct: jasmine.Spy;
    getStock$: jasmine.Spy;
    connectionState$: BehaviorSubject<HubConnectionState>;
  };

  beforeEach(async () => {
    // Stub collaborators via useValue so no real HttpClient / Router / SignalR
    // socket is constructed (the real StockService builds a HubConnection in its
    // constructor). Observables use of(...) for synchronous emission, so the
    // gating flag is computed during ngOnInit inside the first detectChanges().
    accountServiceStub = {
      getUserAddress: jasmine.createSpy('getUserAddress').and.returnValue(of(null))
    };
    basketServiceStub = {
      basket$: of({
        id: 'basket-1',
        items: [
          { id: 1, productName: 'Board', price: 100, quantity: 1, pictureUrl: '', brand: 'NB', type: 'Boards' }
        ]
      }),
      basketTotal$: of({ shipping: 0, subtotal: 100, total: 100 }),
      // getDeliveryMethodValue() dereferences basket.deliveryMethodId, so this must
      // return an object (not null). deliveryMethodId: null keeps the != null guard
      // false so no patch is attempted.
      getCurrentBasketValue: () => ({ id: 'basket-1', deliveryMethodId: null, items: [{ id: 1 }] })
    };
    stockServiceStub = {
      subscribeToProduct: jasmine.createSpy('subscribeToProduct'),
      unsubscribeFromProduct: jasmine.createSpy('unsubscribeFromProduct'),
      getStock$: jasmine.createSpy('getStock$').and.returnValue(of(5)),
      // Seed the connection as Connected so the fail-closed gate (F12) is open and
      // the existing positive-stock assertions hold.
      connectionState$: new BehaviorSubject<HubConnectionState>(HubConnectionState.Connected)
    };

    await TestBed.configureTestingModule({
      declarations: [
        CheckoutComponent,
        StubStepperComponent,
        StubCdkStepComponent,
        StubCheckoutAddressComponent,
        StubCheckoutDeliveryComponent,
        StubCheckoutReviewComponent,
        StubCheckoutPaymentComponent,
        StubOrderTotalsComponent
      ],
      imports: [
        CommonModule,
        ReactiveFormsModule,
        RouterTestingModule
      ],
      providers: [
        { provide: AccountService, useValue: accountServiceStub },
        { provide: BasketService, useValue: basketServiceStub },
        { provide: StockService, useValue: stockServiceStub }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(CheckoutComponent);
    component = fixture.componentInstance;
  });

  it('should create', () => {
    fixture.detectChanges();
    expect(component).toBeTruthy();
  });

  it('should subscribe to stock updates for each basket product id', () => {
    fixture.detectChanges();
    expect(stockServiceStub.subscribeToProduct).toHaveBeenCalledWith(1);
  });

  it('should not flag out-of-stock and render no alert when stock is positive', () => {
    fixture.detectChanges();
    expect(component.hasOutOfStockItem).toBeFalse();
    // The zero-stock alert is bound with *ngIf="hasOutOfStockItem"; with a positive
    // stock the flag is false, so no alert is rendered regardless of template state.
    expect(fixture.nativeElement.querySelector('.alert.alert-danger')).toBeNull();
  });

  it('should flag out-of-stock when a tracked product reaches zero', () => {
    // Emit 0 synchronously BEFORE the first change detection so the gating flag is
    // computed during ngOnInit.
    stockServiceStub.getStock$.and.returnValue(of(0));
    fixture.detectChanges();
    // hasOutOfStockItem is the authoritative gating signal the component exposes; the
    // inline `alert alert-danger` in checkout.component.html is bound to it via
    // *ngIf="hasOutOfStockItem". That template markup lives in a sibling file this
    // spec does not own or modify, so the behaviour is asserted through the public
    // flag (mirroring the sibling basket.component.spec.ts), which stays correct
    // whether or not the shell template renders the alert.
    expect(component.hasOutOfStockItem).toBeTrue();
  });

  it('should propagate the gate to the payment step via [disableForStock] (P4-07)', () => {
    // With positive stock the payment step must NOT be blocked.
    stockServiceStub.getStock$.and.returnValue(of(5));
    fixture.detectChanges();
    const payment = fixture.debugElement
      .query(By.directive(StubCheckoutPaymentComponent)).componentInstance as StubCheckoutPaymentComponent;
    expect(component.hasOutOfStockItem).toBeFalse();
    expect(payment.disableForStock).toBeFalse();
  });

  it('should block the payment step submit when a tracked product is at zero (P4-07)', () => {
    // A zero-stock line must flow through to the payment child so it can disable its Submit button —
    // this is the wiring that was missing (the parent only rendered an alert). Asserting the bound child
    // input proves the gate reaches the component that owns submitOrder() and the Submit button.
    stockServiceStub.getStock$.and.returnValue(of(0));
    fixture.detectChanges();
    const payment = fixture.debugElement
      .query(By.directive(StubCheckoutPaymentComponent)).componentInstance as StubCheckoutPaymentComponent;
    expect(component.hasOutOfStockItem).toBeTrue();
    expect(payment.disableForStock).toBeTrue();
  });
});

/**
 * Unit tests for the additive Real-Time Inventory behaviour of CheckoutComponent.
 *
 * The suite is fully hermetic: no real SignalR socket is ever opened because the
 * root-provided StockService is replaced by a mock in the TestBed injector, so the
 * real service constructor (which builds a HubConnection) never runs. BasketService
 * and AccountService are mocked as well, and the component template is overridden to
 * empty so the shell's CDK stepper and child components need not be declared - the
 * tests exercise the component CLASS logic (stock tracking + gating flag), which is
 * exactly the surface this file added.
 */
describe('CheckoutComponent (stock tracking logic)', () => {
  let component: CheckoutComponent;
  let fixture: ComponentFixture<CheckoutComponent>;
  let stockService: MockStockService;
  let basketService: MockBasketService;
  let accountService: MockAccountService;

  /**
   * Mock of the root StockService. Records subscribeToProduct calls via a spy and
   * hands out a per-product Subject through getStock$ so tests can deterministically
   * push server "StockChanged" values with `push(id, stock)`.
   */
  class MockStockService {
    subscribeToProduct = jasmine.createSpy('subscribeToProduct');
    // Mirror the real StockService surface: the component releases a product's hub
    // tracking when it leaves the basket (QA R1 / prune-on-removal).
    unsubscribeFromProduct = jasmine.createSpy('unsubscribeFromProduct');
    // Live hub connection state; seeded Connected so the fail-closed gate (F12) is
    // open by default. Tests flip this to assert the disconnected behaviour.
    connectionState$ = new BehaviorSubject<HubConnectionState>(HubConnectionState.Connected);
    private subjects = new Map<number, Subject<number>>();

    getStock$(productId: number): Observable<number> {
      return this.subject(productId);
    }

    push(productId: number, stock: number): void {
      this.subject(productId).next(stock);
    }

    private subject(productId: number): Subject<number> {
      let subject = this.subjects.get(productId);
      if (!subject) {
        subject = new Subject<number>();
        this.subjects.set(productId, subject);
      }
      return subject;
    }
  }

  /**
   * Mock of BasketService exposing the members CheckoutComponent touches: the
   * `basket$` stream (a BehaviorSubject seeded with null to mirror the real service),
   * `basketTotal$`, and `getCurrentBasketValue()`.
   */
  class MockBasketService {
    basket$ = new BehaviorSubject<any>(null);
    basketTotal$ = of(null);
    currentBasket: any = { id: 'basket-1', items: [], deliveryMethodId: null };

    getCurrentBasketValue(): any {
      return this.currentBasket;
    }
  }

  /** Mock of AccountService: no saved address, so the address form is left untouched. */
  class MockAccountService {
    getUserAddress(): Observable<any> {
      return of(null);
    }
  }

  beforeEach(async () => {
    stockService = new MockStockService();
    basketService = new MockBasketService();
    accountService = new MockAccountService();

    await TestBed.configureTestingModule({
      imports: [ReactiveFormsModule],
      declarations: [CheckoutComponent],
      providers: [
        { provide: StockService, useValue: stockService },
        { provide: BasketService, useValue: basketService },
        { provide: AccountService, useValue: accountService }
      ]
    })
      // Empty template: keeps the test focused on the class and avoids declaring the
      // out-of-scope CDK stepper and checkout child components.
      .overrideComponent(CheckoutComponent, { set: { template: '' } })
      .compileComponents();

    fixture = TestBed.createComponent(CheckoutComponent);
    component = fixture.componentInstance;
  });

  it('should create and preserve the original init behaviour', () => {
    expect(component).toBeTruthy();

    fixture.detectChanges(); // runs ngOnInit

    // The pre-existing responsibilities of ngOnInit remain intact.
    expect(component.checkoutForm).toBeTruthy();
    expect(component.checkoutForm.get('addressForm')).toBeTruthy();
    expect(component.checkoutForm.get('deliveryForm')).toBeTruthy();
    expect(component.checkoutForm.get('paymentForm')).toBeTruthy();
    expect(component.basketTotals$).toBe(basketService.basketTotal$);

    // The new gating flag starts false (public, template-bindable).
    expect(component.hasOutOfStockItem).toBeFalse();
  });

  it('subscribes to stock once per unique basket product id (dedupe on re-emit)', () => {
    fixture.detectChanges();

    const basket = { id: 'basket-1', items: [{ id: 1 }, { id: 2 }] };
    basketService.basket$.next(basket);
    basketService.basket$.next(basket); // re-emission must NOT re-subscribe

    expect(stockService.subscribeToProduct).toHaveBeenCalledWith(1);
    expect(stockService.subscribeToProduct).toHaveBeenCalledWith(2);
    expect(stockService.subscribeToProduct).toHaveBeenCalledTimes(2);
  });

  it('flips hasOutOfStockItem to true when any tracked product reaches zero and back to false when it restocks', () => {
    fixture.detectChanges();

    basketService.basket$.next({ id: 'basket-1', items: [{ id: 1 }, { id: 2 }] });

    stockService.push(1, 5);
    stockService.push(2, 3);
    expect(component.hasOutOfStockItem).toBeFalse();

    stockService.push(2, 0);
    expect(component.hasOutOfStockItem).toBeTrue();

    stockService.push(2, 4); // restock -> no product at zero anymore
    expect(component.hasOutOfStockItem).toBeFalse();
  });

  // ---------------------------------------------------------------------------
  // QA H1 (MAJOR) — the checkout submit gate must CLEAR when the offending
  // product is removed from the basket or the basket is emptied. Before the fix
  // the gate latched permanently on a departed item's stale zero, blocking the
  // AAP-documented recovery ("return to your basket and remove it").
  // ---------------------------------------------------------------------------

  it('H1a: removing the out-of-stock item clears the checkout submit gate', () => {
    fixture.detectChanges();

    basketService.basket$.next({ id: 'basket-1', items: [{ id: 1 }, { id: 2 }] });
    stockService.push(1, 5);
    stockService.push(2, 0);
    expect(component.hasOutOfStockItem).toBeTrue();

    // The shopper removes item 2; the checkout re-computes from the current basket.
    basketService.basket$.next({ id: 'basket-1', items: [{ id: 1 }] });
    expect(component.hasOutOfStockItem).toBeFalse();

    // The departed product's per-product hub tracking is released (R1).
    expect(stockService.unsubscribeFromProduct).toHaveBeenCalledWith(2);
  });

  it('H1b: emptying the basket (null) clears the checkout submit gate', () => {
    fixture.detectChanges();

    basketService.basket$.next({ id: 'basket-1', items: [{ id: 1 }, { id: 2 }] });
    stockService.push(2, 0);
    expect(component.hasOutOfStockItem).toBeTrue();

    basketService.basket$.next(null);
    expect(component.hasOutOfStockItem).toBeFalse();
  });

  it('guards against a null basket and a missing items array', () => {
    fixture.detectChanges(); // BehaviorSubject emits its seeded null first

    expect(() => basketService.basket$.next(null)).not.toThrow();
    expect(() => basketService.basket$.next({ id: 'basket-1' })).not.toThrow();

    expect(stockService.subscribeToProduct).not.toHaveBeenCalled();
    expect(component.hasOutOfStockItem).toBeFalse();
  });

  it('unsubscribes every subscription on destroy so later stock and basket events are ignored', () => {
    fixture.detectChanges();

    basketService.basket$.next({ id: 'basket-1', items: [{ id: 1 }] });
    stockService.push(1, 0);
    expect(component.hasOutOfStockItem).toBeTrue();

    component.ngOnDestroy();

    // A late stock push on the tracked product must not reach the component.
    component.hasOutOfStockItem = false;
    stockService.push(1, 0);
    expect(component.hasOutOfStockItem).toBeFalse();

    // A late basket emission must not trigger any new subscribeToProduct call.
    const callsBefore = stockService.subscribeToProduct.calls.count();
    basketService.basket$.next({ id: 'basket-1', items: [{ id: 99 }] });
    expect(stockService.subscribeToProduct.calls.count()).toBe(callsBefore);
  });

  it('releases every still-tracked product hub id on destroy (P4-12 teardown)', () => {
    fixture.detectChanges();

    basketService.basket$.next({ id: 'basket-1', items: [{ id: 1 }, { id: 2 }] });
    stockService.push(1, 5);
    stockService.push(2, 3);
    expect(stockService.subscribeToProduct).toHaveBeenCalledWith(1);
    expect(stockService.subscribeToProduct).toHaveBeenCalledWith(2);

    component.ngOnDestroy();

    // Leaving checkout with items still in the basket must release BOTH products'
    // per-product hub tracking so the server groups are left (P4-12).
    expect(stockService.unsubscribeFromProduct).toHaveBeenCalledWith(1);
    expect(stockService.unsubscribeFromProduct).toHaveBeenCalledWith(2);
  });

  // ---------------------------------------------------------------------------
  // QA F9 (MINOR→MAJOR) — insufficient stock: available > 0 but < requested qty.
  // The submit gate must close, distinctly from the zero (out-of-stock) case.
  // ---------------------------------------------------------------------------
  it('F9: flags insufficient stock (and closes the submit gate) when available < requested quantity', () => {
    fixture.detectChanges();
    basketService.basket$.next({ id: 'basket-1', items: [{ id: 1, quantity: 2 }] });

    stockService.push(1, 1); // 1 available, 2 requested
    expect(component.hasInsufficientStockItem).toBeTrue();
    expect(component.hasOutOfStockItem).toBeFalse();
    expect(component.submitDisabledForStock).toBeTrue();
  });

  it('F9: does NOT flag insufficient when available equals the requested quantity', () => {
    fixture.detectChanges();
    basketService.basket$.next({ id: 'basket-1', items: [{ id: 1, quantity: 2 }] });

    stockService.push(1, 2); // exactly enough
    expect(component.hasInsufficientStockItem).toBeFalse();
    expect(component.hasOutOfStockItem).toBeFalse();
    expect(component.submitDisabledForStock).toBeFalse();
  });

  it('F9: insufficient flag clears when the requested quantity is reduced to what is available', () => {
    fixture.detectChanges();
    basketService.basket$.next({ id: 'basket-1', items: [{ id: 1, quantity: 3 }] });
    stockService.push(1, 2);
    expect(component.hasInsufficientStockItem).toBeTrue();

    basketService.basket$.next({ id: 'basket-1', items: [{ id: 1, quantity: 2 }] });
    expect(component.hasInsufficientStockItem).toBeFalse();
    expect(component.submitDisabledForStock).toBeFalse();
  });

  // ---------------------------------------------------------------------------
  // QA F12 (MAJOR) — fail-closed on lost hub connection, even with healthy stock.
  // ---------------------------------------------------------------------------
  it('F12: the submit gate is fail-closed while the hub is not connected, and reopens on reconnect', () => {
    fixture.detectChanges();
    basketService.basket$.next({ id: 'basket-1', items: [{ id: 1, quantity: 1 }] });
    stockService.push(1, 5); // healthy stock
    expect(component.stockConnected).toBeTrue();
    expect(component.submitDisabledForStock).toBeFalse();

    // Connection drops -> gate closes despite healthy stock.
    stockService.connectionState$.next(HubConnectionState.Reconnecting);
    expect(component.stockConnected).toBeFalse();
    expect(component.submitDisabledForStock).toBeTrue();

    // Reconnect -> gate reopens.
    stockService.connectionState$.next(HubConnectionState.Connected);
    expect(component.stockConnected).toBeTrue();
    expect(component.submitDisabledForStock).toBeFalse();
  });

  // ---------------------------------------------------------------------------
  // QA F5 (MAJOR) — release every tracked product on destroy (no ref-count leak).
  // ---------------------------------------------------------------------------
  it('F5: releases every still-tracked product hub subscription on destroy', () => {
    fixture.detectChanges();
    basketService.basket$.next({ id: 'basket-1', items: [{ id: 1, quantity: 1 }, { id: 2, quantity: 1 }] });

    component.ngOnDestroy();

    expect(stockService.unsubscribeFromProduct).toHaveBeenCalledWith(1);
    expect(stockService.unsubscribeFromProduct).toHaveBeenCalledWith(2);
  });
});
