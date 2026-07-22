import { TestBed, ComponentFixture } from '@angular/core/testing';
import { CommonModule } from '@angular/common';
import { RouterTestingModule } from '@angular/router/testing';
import { ActivatedRoute, convertToParamMap, ParamMap } from '@angular/router';
import { BreadcrumbService } from 'xng-breadcrumb';
import { of, BehaviorSubject } from 'rxjs';
import { HubConnectionState } from '@microsoft/signalr';

import { ProductDetailsComponent } from './product-details.component';
import { StockService } from '../../core/services/stock.service';
import { ShopService } from '../shop.service';
import { BasketService } from '../../basket/basket.service';

describe('ProductDetailsComponent', () => {
  let component: ProductDetailsComponent;
  let fixture: ComponentFixture<ProductDetailsComponent>;
  let stockServiceStub: {
    subscribeToProduct: jasmine.Spy;
    getStock$: jasmine.Spy;
    unsubscribeFromProduct: jasmine.Spy;
    connectionState$: any;
    lowStockThreshold$: any;
    lowStockThreshold: number;
  };
  let connectionState$: BehaviorSubject<HubConnectionState>;
  let lowStockThreshold$: BehaviorSubject<number>;
  let shopServiceStub: { getProduct: jasmine.Spy };
  let bcServiceStub: { set: jasmine.Spy };
  let basketServiceStub: { addItemToBasket: jasmine.Spy };
  // The component now resolves the id from the paramMap OBSERVABLE so a same-component
  // route reuse rebinds to the new product (QA R2). A BehaviorSubject drives it and
  // lets a test push a new id. A `snapshot` is retained for backward compatibility.
  let paramMapSubject: BehaviorSubject<ParamMap>;
  let activatedRouteStub: { snapshot: { paramMap: { get: () => string } }; paramMap: any };

  beforeEach(async () => {
    // Stubs via useValue so the real HttpClient / SignalR / Router deps are never constructed.
    connectionState$ = new BehaviorSubject<HubConnectionState>(HubConnectionState.Connected);
    lowStockThreshold$ = new BehaviorSubject<number>(5);
    stockServiceStub = {
      subscribeToProduct: jasmine.createSpy('subscribeToProduct'),
      getStock$: jasmine.createSpy('getStock$').and.returnValue(of(3)),
      // The component releases the previous product's hub subscription when the
      // route rebinds to a different product or on destroy (QA R2).
      unsubscribeFromProduct: jasmine.createSpy('unsubscribeFromProduct'),
      connectionState$: connectionState$.asObservable(),
      lowStockThreshold$: lowStockThreshold$.asObservable(),
      lowStockThreshold: 5
    };
    shopServiceStub = {
      // Return a product whose id matches the requested id so route-rebind tests can
      // assert the component tracks the NEW product.
      getProduct: jasmine.createSpy('getProduct').and.callFake((id: number) => of({
        id, name: 'x', price: 1, pictureUrl: '', productType: '', productBrand: '', description: ''
      }))
    };
    bcServiceStub = { set: jasmine.createSpy('set') };
    basketServiceStub = { addItemToBasket: jasmine.createSpy('addItemToBasket') };
    paramMapSubject = new BehaviorSubject<ParamMap>(convertToParamMap({ id: '1' }));
    activatedRouteStub = {
      snapshot: { paramMap: { get: () => '1' } },
      paramMap: paramMapSubject.asObservable()
    };

    await TestBed.configureTestingModule({
      declarations: [ProductDetailsComponent],
      imports: [CommonModule, RouterTestingModule],
      providers: [
        { provide: StockService, useValue: stockServiceStub },
        { provide: ShopService, useValue: shopServiceStub },
        { provide: BreadcrumbService, useValue: bcServiceStub },
        { provide: BasketService, useValue: basketServiceStub },
        { provide: ActivatedRoute, useValue: activatedRouteStub }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(ProductDetailsComponent);
    component = fixture.componentInstance;
  });

  it('should create', () => {
    fixture.detectChanges();
    expect(component).toBeTruthy();
  });

  it('should render the low-stock badge when stock is 3', () => {
    // of(3) resolves synchronously: ngOnInit -> loadProduct -> product id 1 -> getStock$ -> stock = 3.
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector('.badge');
    expect(badge.textContent).toContain('Only 3 left!');
    expect(badge.textContent).not.toContain('Out of stock');
  });

  it('should render the out-of-stock badge and disable Add to Cart when stock is 0', () => {
    stockServiceStub.getStock$.and.returnValue(of(0));

    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector('.badge');
    expect(badge.textContent).toContain('Out of stock');
    expect(badge.textContent).not.toContain('Only 0 left');

    const addButton = fixture.nativeElement.querySelector('.btn-outline-primary');
    expect(addButton.disabled).toBe(true);
  });

  it('should call subscribeToProduct with the resolved product id', () => {
    fixture.detectChanges();

    expect(stockServiceStub.subscribeToProduct).toHaveBeenCalledWith(1);
  });

  // ---------------------------------------------------------------------------
  // Boundary matrix (QA Issue #3 coverage gap).
  // ---------------------------------------------------------------------------

  // QA F4 (MAJOR): unknown stock is fail-CLOSED. Previously this asserted the button
  // stayed ENABLED with no badge (the defect); per D1 it is updated to fail-closed.
  it('fails closed while stock is unknown (undefined): disables Add to Cart and shows "Checking availability" (QA F4)', () => {
    stockServiceStub.getStock$.and.returnValue(of(undefined));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.badge-warning')).toBeNull();
    expect(fixture.nativeElement.querySelector('.badge-danger')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Checking availability');
    const addButton = fixture.nativeElement.querySelector('.btn-outline-primary');
    expect(addButton.disabled).toBe(true);
  });

  it('renders "Only 1 left!" at the lower low-stock boundary (stock 1)', () => {
    stockServiceStub.getStock$.and.returnValue(of(1));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.badge-warning').textContent).toContain('Only 1 left!');
  });

  it('renders "Only 5 left!" at the inclusive upper low-stock boundary (stock 5)', () => {
    stockServiceStub.getStock$.and.returnValue(of(5));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.badge-warning').textContent).toContain('Only 5 left!');
  });

  it('renders NO badge above the low-stock threshold (stock 6)', () => {
    stockServiceStub.getStock$.and.returnValue(of(6));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.badge')).toBeNull();
  });

  it('updates the badge as stock changes dynamically (6 -> 5 -> 1 -> 0 -> 3)', () => {
    const stock$ = new BehaviorSubject<number>(6);
    stockServiceStub.getStock$.and.returnValue(stock$.asObservable());
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.badge')).toBeNull();

    stock$.next(5);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.badge-warning').textContent).toContain('Only 5 left!');

    stock$.next(1);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.badge-warning').textContent).toContain('Only 1 left!');

    stock$.next(0);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.badge-danger').textContent).toContain('Out of stock');

    stock$.next(3);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.badge-warning').textContent).toContain('Only 3 left!');
  });

  // ---------------------------------------------------------------------------
  // QA H2 (MINOR): quantity cap + programmatic add-to-basket guard.
  // ---------------------------------------------------------------------------

  it('caps incrementQuantity at the available stock (stock 2)', () => {
    stockServiceStub.getStock$.and.returnValue(of(2));
    fixture.detectChanges(); // stock = 2, quantity resets to 1
    component.incrementQuantity(); // 1 -> 2
    component.incrementQuantity(); // capped at 2 (quantity < stock is false)
    expect(component.quantity).toBe(2);
  });

  it('still increments quantity while stock is unknown (undefined)', () => {
    stockServiceStub.getStock$.and.returnValue(of(undefined));
    fixture.detectChanges();
    component.incrementQuantity();
    component.incrementQuantity();
    expect(component.quantity).toBe(3);
  });

  it('addItemToBasket caps the requested quantity to available stock', () => {
    stockServiceStub.getStock$.and.returnValue(of(2));
    fixture.detectChanges();
    component.quantity = 4; // force an over-stock quantity
    component.addItemToBasket();
    expect(basketServiceStub.addItemToBasket).toHaveBeenCalledWith(component.product, 2);
  });

  it('addItemToBasket is a no-op when stock is 0 (programmatic guard)', () => {
    stockServiceStub.getStock$.and.returnValue(of(0));
    fixture.detectChanges();
    component.addItemToBasket();
    expect(basketServiceStub.addItemToBasket).not.toHaveBeenCalled();
  });

  it('addItemToBasket adds the requested quantity when within stock', () => {
    stockServiceStub.getStock$.and.returnValue(of(5));
    fixture.detectChanges();
    component.quantity = 3;
    component.addItemToBasket();
    expect(basketServiceStub.addItemToBasket).toHaveBeenCalledWith(component.product, 3);
  });

  // ---------------------------------------------------------------------------
  // QA R2 (INFO): a same-component route reuse (cross-product navigation) must
  // rebind stock tracking to the new product id and release the previous one.
  // ---------------------------------------------------------------------------

  it('rebinds stock tracking when the route product id changes', () => {
    fixture.detectChanges(); // initial id 1
    expect(shopServiceStub.getProduct).toHaveBeenCalledWith(1);
    expect(stockServiceStub.subscribeToProduct).toHaveBeenCalledWith(1);

    // Simulate navigating to a different product on the SAME component instance.
    paramMapSubject.next(convertToParamMap({ id: '2' }));
    fixture.detectChanges();

    expect(shopServiceStub.getProduct).toHaveBeenCalledWith(2);
    expect(stockServiceStub.subscribeToProduct).toHaveBeenCalledWith(2);
    // The previous product's per-product hub subscription is released.
    expect(stockServiceStub.unsubscribeFromProduct).toHaveBeenCalledWith(1);
  });

  // ---------------------------------------------------------------------------
  // QA F12/F4 (MAJOR): connection-state awareness + fail-closed gating.
  // ---------------------------------------------------------------------------

  it('fails closed when the hub link is not Connected: disables Add to Cart and shows "Live stock unavailable" (QA F12/F4)', () => {
    connectionState$.next(HubConnectionState.Disconnected);
    stockServiceStub.getStock$.and.returnValue(of(3));
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Live stock unavailable');
    expect(fixture.nativeElement.querySelector('.badge-warning')).toBeNull();
    const addButton = fixture.nativeElement.querySelector('.btn-outline-primary');
    expect(addButton.disabled).toBe(true);
  });

  it('addItemToBasket is a no-op while the hub link is not Connected (QA F4)', () => {
    connectionState$.next(HubConnectionState.Disconnected);
    stockServiceStub.getStock$.and.returnValue(of(3));
    fixture.detectChanges();
    component.addItemToBasket();
    expect(basketServiceStub.addItemToBasket).not.toHaveBeenCalled();
  });

  // ---------------------------------------------------------------------------
  // QA F6 (MINOR): threshold follows the server-broadcast value, not hardcoded 5.
  // ---------------------------------------------------------------------------

  it('uses the server-provided low-stock threshold rather than a hardcoded 5 (QA F6)', () => {
    const stock$ = new BehaviorSubject<number>(3);
    stockServiceStub.getStock$.and.returnValue(stock$.asObservable());
    lowStockThreshold$.next(2);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.badge-warning')).toBeNull(); // 3 > 2

    stock$.next(2);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.badge-warning').textContent).toContain('Only 2 left!');
  });

  // QA Issue #4 (w-010 coverage): the badge boundary is the SERVER-pushed threshold, not a
  // hard-coded literal. Above the default the badge shows at a HIGHER stock; below the default
  // it hides at a stock the old literal 5 would have shown.
  it('shows the low-stock badge using a server-driven threshold ABOVE the default (threshold 8, stock 7)', () => {
    lowStockThreshold$.next(8);
    stockServiceStub.getStock$.and.returnValue(of(7));
    fixture.detectChanges();
    const badge = fixture.nativeElement.querySelector('.badge-warning');
    expect(badge).toBeTruthy();
    expect(badge.textContent).toContain('Only 7 left!');
  });

  it('hides the low-stock badge when stock exceeds a server-driven threshold BELOW the default (threshold 3, stock 4)', () => {
    lowStockThreshold$.next(3);
    stockServiceStub.getStock$.and.returnValue(of(4));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.badge')).toBeNull();
  });

  // ---------------------------------------------------------------------------
  // QA INF-2 (INFO): a live stock drop below the selected quantity clamps the
  // displayed quantity down (never below the available stock, never while unknown).
  // ---------------------------------------------------------------------------

  it('clamps the displayed quantity down when live stock drops below it (QA INF-2)', () => {
    const stock$ = new BehaviorSubject<number>(5);
    stockServiceStub.getStock$.and.returnValue(stock$.asObservable());
    fixture.detectChanges();      // stock 5, quantity resets to 1
    component.quantity = 4;       // shopper selects 4 (<= 5, allowed)
    stock$.next(2);               // live stock drops to 2
    fixture.detectChanges();
    expect(component.quantity).toBe(2); // clamped down to available stock

    stock$.next(0);               // out of stock
    fixture.detectChanges();
    expect(component.quantity).toBe(2); // clamp never drives quantity below the last value
  });

  // ---------------------------------------------------------------------------
  // QA F7 (MINOR): keyboard-operable quantity steppers with accessible names.
  // ---------------------------------------------------------------------------

  it('renders the quantity steppers as real buttons with accessible names (QA F7)', () => {
    fixture.detectChanges();
    const buttons: HTMLButtonElement[] = Array.from(fixture.nativeElement.querySelectorAll('button.quantity-control'));
    expect(buttons.length).toBe(2);
    const labels = buttons.map(b => b.getAttribute('aria-label'));
    expect(labels).toContain('Decrease quantity');
    expect(labels).toContain('Increase quantity');
  });

  it('increments quantity when the increase stepper button is activated (QA F7)', () => {
    stockServiceStub.getStock$.and.returnValue(of(5));
    fixture.detectChanges();
    const increase: HTMLButtonElement =
      fixture.nativeElement.querySelector('button.quantity-control[aria-label="Increase quantity"]');
    increase.click(); // a native <button> is activated by Enter/Space AND click
    expect(component.quantity).toBe(2);
  });
});
