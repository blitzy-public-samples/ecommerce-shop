import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ReactiveFormsModule } from '@angular/forms';
import { BehaviorSubject, Observable, of, Subject } from 'rxjs';

import { CheckoutComponent } from './checkout.component';
import { StockService } from '../core/services/stock.service';
import { BasketService } from '../basket/basket.service';
import { AccountService } from '../account/account.service';

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
describe('CheckoutComponent', () => {
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
});
