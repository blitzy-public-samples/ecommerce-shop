import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Component, Input, forwardRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import {
  ControlValueAccessor,
  FormBuilder,
  FormControl,
  FormGroup,
  NG_VALUE_ACCESSOR,
  ReactiveFormsModule,
  Validators
} from '@angular/forms';
import { RouterTestingModule } from '@angular/router/testing';
import { of } from 'rxjs';

import { CheckoutPaymentComponent } from './checkout-payment.component';
import { BasketService } from 'src/app/basket/basket.service';
import { CheckoutService } from '../checkout.service';
import { ToastrService } from 'ngx-toastr';

/**
 * Real-DOM specs for the C-3 fix: the checkout "Submit Order" button must be disabled — and
 * submitOrder() must short-circuit — when the parent CheckoutComponent flags an out-of-stock basket
 * product via [disableForStock]="submitDisabledForStock" (AAP §0.5.3). Before the fix the payment step had
 * no gating input, so the button stayed enabled and no test asserted its disabled state at zero stock.
 *
 * The component's real template is exercised (not overridden) so the assertions bind to the ACTUAL
 * Submit Order button. Two lightweight test doubles keep the suite hermetic:
 *  - a global `Stripe` stub so ngAfterViewInit (which mounts Stripe Elements) runs without Stripe.js;
 *  - a `StubTextInputComponent` that provides NG_VALUE_ACCESSOR so the template's
 *    `<app-text-input formControlName="nameOnCard">` binds without importing the real control.
 */

// Minimal ControlValueAccessor stub for the app-text-input the payment template renders, so the
// reactive `formControlName` directive resolves a value accessor and the real template compiles.
@Component({
  selector: 'app-text-input',
  template: '',
  providers: [
    {
      provide: NG_VALUE_ACCESSOR,
      useExisting: forwardRef(() => StubTextInputComponent),
      multi: true
    }
  ]
})
class StubTextInputComponent implements ControlValueAccessor {
  @Input() label: string;
  @Input() type: string;
  writeValue(): void {}
  registerOnChange(): void {}
  registerOnTouched(): void {}
  setDisabledState(): void {}
}

// Installs a minimal global Stripe stub. Each created "element" exposes the mount/addEventListener/
// destroy the component calls; the instance exposes confirmCardPayment for the submit path.
function installStripeStub(): void {
  const cardElement = {
    mount: () => {},
    addEventListener: () => {},
    destroy: () => {}
  };
  (window as any).Stripe = () => ({
    elements: () => ({ create: () => cardElement }),
    confirmCardPayment: () =>
      Promise.resolve({ paymentIntent: null, error: { message: 'stubbed' } })
  });
}

// Locates the real "Submit Order" button (as opposed to the "Back to Review" button) in the DOM.
function submitButton(
  fixture: ComponentFixture<CheckoutPaymentComponent>
): HTMLButtonElement {
  const buttons = Array.from(
    fixture.nativeElement.querySelectorAll('button')
  ) as HTMLButtonElement[];
  return buttons.find(b => (b.textContent || '').includes('Submit Order'));
}

describe('CheckoutPaymentComponent (out-of-stock submit gating — C-3)', () => {
  let component: CheckoutPaymentComponent;
  let fixture: ComponentFixture<CheckoutPaymentComponent>;
  let checkoutServiceSpy: { createOrder: jasmine.Spy };
  let basketServiceStub: any;
  let toastrStub: { error: jasmine.Spy };

  beforeEach(async () => {
    installStripeStub();

    checkoutServiceSpy = {
      createOrder: jasmine.createSpy('createOrder').and.returnValue(of({ id: 1 }))
    };
    basketServiceStub = {
      getCurrentBasketValue: () => ({
        id: 'basket-1',
        clientSecret: 'cs_test',
        deliveryMethodId: 1
      }),
      deleteLocalBasket: jasmine.createSpy('deleteLocalBasket')
    };
    toastrStub = { error: jasmine.createSpy('error') };

    await TestBed.configureTestingModule({
      declarations: [CheckoutPaymentComponent, StubTextInputComponent],
      imports: [CommonModule, ReactiveFormsModule, RouterTestingModule],
      providers: [
        { provide: BasketService, useValue: basketServiceStub },
        { provide: CheckoutService, useValue: checkoutServiceSpy },
        { provide: ToastrService, useValue: toastrStub }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(CheckoutPaymentComponent);
    component = fixture.componentInstance;

    // A checkout form whose paymentForm is valid so the Submit button's [disabled] expression depends
    // only on the out-of-stock gate once the card fields are marked valid below.
    component.checkoutForm = new FormGroup({
      addressForm: new FormGroup({ firstName: new FormControl('Bob') }),
      deliveryForm: new FormGroup({ deliveryMethod: new FormControl('1') }),
      paymentForm: new FormGroup({
        nameOnCard: new FormControl('Bob Tester', Validators.required)
      })
    });

    fixture.detectChanges(); // triggers ngAfterViewInit (Stripe stub) + first render

    // Mark the Stripe card fields valid and not loading so they do not independently disable submit;
    // the only remaining term controlling the button is the out-of-stock gate (disableForStock).
    component.cardNumberValid = true;
    component.cardExpiryValid = true;
    component.cardCvcValid = true;
    component.loading = false;
  });

  it('enables the Submit Order button when not gated and the card fields are valid', () => {
    component.disableForStock = false;
    fixture.detectChanges();

    const btn = submitButton(fixture);
    expect(btn).toBeTruthy();
    expect(btn.disabled).toBeFalse();
  });

  it('disables the Submit Order button when disableForStock is true (out-of-stock gate)', () => {
    component.disableForStock = true;
    fixture.detectChanges();

    const btn = submitButton(fixture);
    expect(btn).toBeTruthy();
    expect(btn.disabled).toBeTrue();
  });

  it('submitOrder() short-circuits (creates no order, never sets loading) when gated', async () => {
    component.disableForStock = true;

    await component.submitOrder();

    expect(checkoutServiceSpy.createOrder).not.toHaveBeenCalled();
    // The guard returns before `this.loading = true`, so the spinner is never shown.
    expect(component.loading).toBeFalse();
  });

  it('submitOrder() proceeds to create the order when NOT gated', async () => {
    component.disableForStock = false;

    await component.submitOrder();

    expect(checkoutServiceSpy.createOrder).toHaveBeenCalledTimes(1);
  });
});


/**
 * Focused unit tests for the stock submission gate added to CheckoutPaymentComponent
 * (QA H-B/F3): order submission must be blocked when the parent CheckoutComponent
 * reports that stock cannot be honoured (out of stock / insufficient / hub not
 * connected) via the `disableForStock` @Input.
 *
 * The component is constructed directly with mocked collaborators rather than through
 * TestBed so the Stripe-dependent `ngAfterViewInit` / `ngOnDestroy` lifecycle hooks
 * (which require the external Stripe.js library) never run. This keeps the suite
 * hermetic and asserts exactly the new gating behaviour in `submitOrder()`.
 */
describe('CheckoutPaymentComponent (stock submission gate — H-B/F3)', () => {
  let component: CheckoutPaymentComponent;
  let basketService: any;
  let checkoutService: any;
  let toastr: any;
  let router: any;

  beforeEach(() => {
    basketService = {
      getCurrentBasketValue: jasmine.createSpy('getCurrentBasketValue')
        .and.returnValue({ id: 'basket-1', clientSecret: 'cs_123', deliveryMethodId: 1 }),
      deleteLocalBasket: jasmine.createSpy('deleteLocalBasket')
    };
    checkoutService = {
      createOrder: jasmine.createSpy('createOrder')
        .and.returnValue({ toPromise: () => Promise.resolve({ id: 999 }) })
    };
    toastr = { error: jasmine.createSpy('error') };
    router = { navigate: jasmine.createSpy('navigate') };

    component = new CheckoutPaymentComponent(basketService, checkoutService, toastr, router);

    const fb = new FormBuilder();
    component.checkoutForm = fb.group({
      addressForm: fb.group({
        firstName: ['a'], lastName: ['b'], street: ['s'], city: ['c'], state: ['st'], zipCode: ['z']
      }),
      deliveryForm: fb.group({ deliveryMethod: ['1'] }),
      paymentForm: fb.group({ nameOnCard: ['John Doe'] })
    });
  });

  it('blocks order submission when disableForStock is true (no order, no payment, no navigation)', async () => {
    component.disableForStock = true;

    await component.submitOrder();

    expect(checkoutService.createOrder).not.toHaveBeenCalled();
    expect(router.navigate).not.toHaveBeenCalled();
    expect(basketService.deleteLocalBasket).not.toHaveBeenCalled();
    expect(toastr.error).toHaveBeenCalled();
    // The gate returns BEFORE the loading spinner is engaged.
    expect(component.loading).toBeFalse();
  });

  it('allows order submission when disableForStock is false (creates order, confirms payment, navigates)', async () => {
    component.disableForStock = false;
    // Minimal Stripe stub so the confirm-payment step resolves successfully without
    // the real Stripe.js library (never loaded in the test environment).
    component.cardNumber = {};
    component.stripe = {
      confirmCardPayment: jasmine.createSpy('confirmCardPayment')
        .and.returnValue(Promise.resolve({ paymentIntent: { id: 'pi_1' } }))
    };

    await component.submitOrder();

    expect(checkoutService.createOrder).toHaveBeenCalled();
    expect(component.stripe.confirmCardPayment).toHaveBeenCalled();
    expect(basketService.deleteLocalBasket).toHaveBeenCalledWith('basket-1');
    expect(router.navigate).toHaveBeenCalledWith(['checkout/success'], jasmine.any(Object));
    expect(component.loading).toBeFalse();
  });
});

