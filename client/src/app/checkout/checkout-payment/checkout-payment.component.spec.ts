import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Component, Input, forwardRef } from '@angular/core';
import {
  ReactiveFormsModule, FormGroup, FormControl, Validators,
  ControlValueAccessor, NG_VALUE_ACCESSOR
} from '@angular/forms';
import { RouterTestingModule } from '@angular/router/testing';
import { of } from 'rxjs';
import { ToastrService } from 'ngx-toastr';

import { CheckoutPaymentComponent } from './checkout-payment.component';
import { BasketService } from 'src/app/basket/basket.service';
import { CheckoutService } from '../checkout.service';

/**
 * Coverage for the P4-07 checkout submit gate on CheckoutPaymentComponent — the component that actually
 * owns the "Submit Order" button and submitOrder(). Before the fix the parent CheckoutComponent computed
 * the zero-stock gate but only rendered an alert; the payment child never received it, so a shopper could
 * still finalize an order containing an out-of-stock line. The fix adds an @Input() stockBlocked that both
 * disables the Submit button ([disabled]="... || stockBlocked") and short-circuits submitOrder().
 */

// Builds a fully-valid checkout form (address + delivery + payment) so the ONLY driver of the Submit
// button's disabled state under test is the stockBlocked flag (card-validity flags are set by the tests).
function buildValidCheckoutForm(): FormGroup {
  return new FormGroup({
    addressForm: new FormGroup({
      street: new FormControl('1 Test St'), city: new FormControl('Town'),
      state: new FormControl('ST'), zipcode: new FormControl('00000')
    }),
    deliveryForm: new FormGroup({ deliveryMethod: new FormControl('1') }),
    paymentForm: new FormGroup({ nameOnCard: new FormControl('Jane Cardholder', Validators.required) })
  });
}

describe('CheckoutPaymentComponent submitOrder() gate (P4-07)', () => {
  let component: CheckoutPaymentComponent;
  let basketService: jasmine.SpyObj<BasketService>;
  let checkoutService: jasmine.SpyObj<CheckoutService>;
  let toastr: jasmine.SpyObj<ToastrService>;
  let router: jasmine.SpyObj<any>;

  beforeEach(() => {
    // Direct instantiation (no TestBed/ngAfterViewInit) keeps this a pure unit test of the guard.
    basketService = jasmine.createSpyObj<BasketService>('BasketService',
      ['getCurrentBasketValue', 'deleteLocalBasket']);
    checkoutService = jasmine.createSpyObj<CheckoutService>('CheckoutService', ['createOrder']);
    toastr = jasmine.createSpyObj<ToastrService>('ToastrService', ['error']);
    router = jasmine.createSpyObj('Router', ['navigate']);

    component = new CheckoutPaymentComponent(basketService, checkoutService, toastr, router);
  });

  it('does NOT submit and does NOT start loading when stockBlocked is true', async () => {
    component.stockBlocked = true;

    await component.submitOrder();

    // The guard returns before touching the basket, the order API, or the loading flag.
    expect(basketService.getCurrentBasketValue).not.toHaveBeenCalled();
    expect(checkoutService.createOrder).not.toHaveBeenCalled();
    expect(component.loading).toBeFalse();
  });

  it('proceeds to create the order when stockBlocked is false', async () => {
    component.stockBlocked = false;
    component.checkoutForm = buildValidCheckoutForm();
    // Stand in for the Stripe SDK object normally created in ngAfterViewInit (not run here): report a
    // successful payment so the happy path completes cleanly through to the success navigation.
    (component as any).stripe = { confirmCardPayment: async () => ({ paymentIntent: { id: 'pi_1' } }) };
    basketService.getCurrentBasketValue.and.returnValue({ id: 'b1', clientSecret: 'cs_1' } as any);
    checkoutService.createOrder.and.returnValue(of({ id: 1 } as any));

    await component.submitOrder();

    // The guard let the submission through: the order API was invoked and the local basket cleared.
    expect(checkoutService.createOrder).toHaveBeenCalled();
    expect(basketService.deleteLocalBasket).toHaveBeenCalledWith('b1');
    expect(router.navigate).toHaveBeenCalled();
  });
});

// Minimal ControlValueAccessor stub for <app-text-input> so the real checkout-payment template renders
// its reactive-form control without pulling in the real text-input component.
@Component({
  selector: 'app-text-input',
  template: '',
  providers: [
    { provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => StubTextInputComponent), multi: true }
  ]
})
class StubTextInputComponent implements ControlValueAccessor {
  @Input() label: string;
  writeValue(): void { }
  registerOnChange(): void { }
  registerOnTouched(): void { }
}

describe('CheckoutPaymentComponent Submit button disabled state (P4-07)', () => {
  let component: CheckoutPaymentComponent;
  let fixture: ComponentFixture<CheckoutPaymentComponent>;
  let originalStripe: any;

  beforeEach(async () => {
    // Provide a global Stripe stub so the real template's ngAfterViewInit (which mounts Stripe card
    // elements) runs without a network/SDK dependency.
    originalStripe = (window as any).Stripe;
    (window as any).Stripe = () => ({
      elements: () => ({
        create: () => ({ mount: () => { }, addEventListener: () => { }, destroy: () => { } })
      })
    });

    await TestBed.configureTestingModule({
      declarations: [CheckoutPaymentComponent, StubTextInputComponent],
      imports: [ReactiveFormsModule, RouterTestingModule],
      providers: [
        { provide: BasketService, useValue: jasmine.createSpyObj('BasketService', ['getCurrentBasketValue']) },
        { provide: CheckoutService, useValue: jasmine.createSpyObj('CheckoutService', ['createOrder']) },
        { provide: ToastrService, useValue: jasmine.createSpyObj('ToastrService', ['error']) }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(CheckoutPaymentComponent);
    component = fixture.componentInstance;
    component.checkoutForm = buildValidCheckoutForm();
    // Satisfy every OTHER disabled condition so stockBlocked is the sole remaining driver.
    component.loading = false;
    component.cardNumberValid = true;
    component.cardExpiryValid = true;
    component.cardCvcValid = true;
  });

  afterEach(() => {
    (window as any).Stripe = originalStripe;
  });

  function submitButton(): HTMLButtonElement {
    // The Submit Order button is the one wired to (click)="submitOrder()"; select it by its label text.
    const buttons = Array.from(fixture.nativeElement.querySelectorAll('button')) as HTMLButtonElement[];
    return buttons.find(b => b.textContent && b.textContent.includes('Submit Order'));
  }

  it('enables the Submit button when stockBlocked is false and the form/card state is valid', () => {
    component.stockBlocked = false;
    fixture.detectChanges();
    expect(submitButton().disabled).toBeFalse();
  });

  it('disables the Submit button when stockBlocked is true (all else valid)', () => {
    component.stockBlocked = true;
    fixture.detectChanges();
    expect(submitButton().disabled).toBeTrue();
  });
});
