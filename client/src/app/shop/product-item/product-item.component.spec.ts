import { TestBed, ComponentFixture } from '@angular/core/testing';
import { CommonModule } from '@angular/common';
import { RouterTestingModule } from '@angular/router/testing';
import { of, BehaviorSubject } from 'rxjs';
import { HubConnectionState } from '@microsoft/signalr';

import { ProductItemComponent } from './product-item.component';
import { StockService } from '../../core/services/stock.service';
import { BasketService } from '../../basket/basket.service';

describe('ProductItemComponent', () => {
  let component: ProductItemComponent;
  let fixture: ComponentFixture<ProductItemComponent>;
  let stockServiceStub: {
    subscribeToProduct: jasmine.Spy;
    getStock$: jasmine.Spy;
    unsubscribeFromProduct: jasmine.Spy;
    connectionState$: any;
    lowStockThreshold$: any;
    lowStockThreshold: number;
  };
  let basketServiceStub: { addItemToBasket: jasmine.Spy };
  // BehaviorSubjects let a test flip the live-link state or push a new threshold
  // before detectChanges(); the component reads the replayed latest value on init.
  let connectionState$: BehaviorSubject<HubConnectionState>;
  let lowStockThreshold$: BehaviorSubject<number>;

  beforeEach(async () => {
    connectionState$ = new BehaviorSubject<HubConnectionState>(HubConnectionState.Connected);
    lowStockThreshold$ = new BehaviorSubject<number>(5);
    stockServiceStub = {
      subscribeToProduct: jasmine.createSpy('subscribeToProduct'),
      getStock$: jasmine.createSpy('getStock$').and.returnValue(of(3)),
      unsubscribeFromProduct: jasmine.createSpy('unsubscribeFromProduct'),
      connectionState$: connectionState$.asObservable(),
      lowStockThreshold$: lowStockThreshold$.asObservable(),
      lowStockThreshold: 5
    };
    basketServiceStub = {
      addItemToBasket: jasmine.createSpy('addItemToBasket')
    };

    await TestBed.configureTestingModule({
      declarations: [ProductItemComponent],
      imports: [CommonModule, RouterTestingModule],
      providers: [
        { provide: StockService, useValue: stockServiceStub },
        { provide: BasketService, useValue: basketServiceStub }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(ProductItemComponent);
    component = fixture.componentInstance;
    component.product = { id: 1, name: 'x', description: '', price: 1, pictureUrl: '', productType: '', productBrand: '' };
  });

  it('should create', () => {
    fixture.detectChanges();
    expect(component).toBeTruthy();
  });

  it('should render the low-stock badge when 0 < stock <= 5', () => {
    fixture.detectChanges();
    const badge = fixture.nativeElement.querySelector('.badge');
    expect(badge.textContent).toContain('Only 3 left!');
    expect(badge.textContent).not.toContain('Out of stock');
    expect(fixture.nativeElement.querySelector('.badge-warning')).toBeTruthy();
  });

  it('should render out-of-stock badge and disable add-to-cart when stock === 0', () => {
    stockServiceStub.getStock$.and.returnValue(of(0));
    fixture.detectChanges();
    const badge = fixture.nativeElement.querySelector('.badge');
    expect(badge.textContent).toContain('Out of stock');
    expect(badge.textContent).not.toContain('Only 0 left');
    expect(fixture.nativeElement.querySelector('.badge-danger')).toBeTruthy();
    const cartButton = fixture.nativeElement.querySelector('button.fa-shopping-cart');
    expect(cartButton.disabled).toBe(true);
  });

  it('should subscribe to product stock on init using the product id', () => {
    fixture.detectChanges();
    expect(stockServiceStub.subscribeToProduct).toHaveBeenCalledWith(1);
  });

  // ---------------------------------------------------------------------------
  // Boundary matrix (QA Issue #3 coverage gap): the low-stock badge shows for
  // 0 < stock <= 5 and the out-of-stock badge for stock === 0; no badge otherwise.
  // ---------------------------------------------------------------------------

  // QA F4 (MAJOR): unknown stock is fail-CLOSED, not fail-open. Previously this
  // asserted the button stayed ENABLED with no badge; that encoded the defect, so
  // per D1 (tests align to the AAP fail-closed requirement) it is updated here.
  it('fails closed while stock is unknown (undefined): disables add-to-cart and shows "Checking availability" (QA F4)', () => {
    stockServiceStub.getStock$.and.returnValue(of(undefined));
    fixture.detectChanges();
    // No numeric stock badge yet...
    expect(fixture.nativeElement.querySelector('.badge-warning')).toBeNull();
    expect(fixture.nativeElement.querySelector('.badge-danger')).toBeNull();
    // ...instead a non-blocking "checking availability" indicator is shown...
    expect(fixture.nativeElement.textContent).toContain('Checking availability');
    // ...and the add-to-cart control is disabled (fail-closed).
    const cartButton = fixture.nativeElement.querySelector('button.fa-shopping-cart');
    expect(cartButton.disabled).toBe(true);
  });

  it('renders "Only 1 left!" at the lower low-stock boundary (stock 1)', () => {
    stockServiceStub.getStock$.and.returnValue(of(1));
    fixture.detectChanges();
    const badge = fixture.nativeElement.querySelector('.badge-warning');
    expect(badge.textContent).toContain('Only 1 left!');
  });

  it('renders "Only 5 left!" at the inclusive upper low-stock boundary (stock 5)', () => {
    stockServiceStub.getStock$.and.returnValue(of(5));
    fixture.detectChanges();
    const badge = fixture.nativeElement.querySelector('.badge-warning');
    expect(badge.textContent).toContain('Only 5 left!');
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

  it('does not call BasketService when the disabled add button is activated at stock 0', () => {
    stockServiceStub.getStock$.and.returnValue(of(0));
    fixture.detectChanges();
    const cartButton = fixture.nativeElement.querySelector('button.fa-shopping-cart');
    cartButton.click(); // a disabled button ignores activation
    expect(basketServiceStub.addItemToBasket).not.toHaveBeenCalled();
  });

  // QA H2 (MINOR): programmatic add-to-basket must also be guarded at zero stock,
  // not only the template [disabled] binding.
  it('addItemToBasket() is a no-op when stock is 0 (programmatic guard)', () => {
    stockServiceStub.getStock$.and.returnValue(of(0));
    fixture.detectChanges();
    component.addItemToBasket();
    expect(basketServiceStub.addItemToBasket).not.toHaveBeenCalled();
  });

  it('addItemToBasket() adds the product when it is in stock', () => {
    stockServiceStub.getStock$.and.returnValue(of(3));
    fixture.detectChanges();
    component.addItemToBasket();
    expect(basketServiceStub.addItemToBasket).toHaveBeenCalledWith(component.product);
  });

  // ---------------------------------------------------------------------------
  // QA F12 (MAJOR): connection-state awareness — when the hub link is not
  // Connected the UI fails closed and announces unavailability.
  // ---------------------------------------------------------------------------

  it('fails closed when the hub link is not Connected: disables add-to-cart and shows "Live stock unavailable" (QA F12/F4)', () => {
    connectionState$.next(HubConnectionState.Disconnected);
    stockServiceStub.getStock$.and.returnValue(of(3)); // stock known-positive, but link is down
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Live stock unavailable');
    // The low-stock badge is suppressed while disconnected.
    expect(fixture.nativeElement.querySelector('.badge-warning')).toBeNull();
    const cartButton = fixture.nativeElement.querySelector('button.fa-shopping-cart');
    expect(cartButton.disabled).toBe(true);
  });

  it('addItemToBasket() is a no-op while the hub link is not Connected (QA F4 fail-closed)', () => {
    connectionState$.next(HubConnectionState.Disconnected);
    stockServiceStub.getStock$.and.returnValue(of(3));
    fixture.detectChanges();
    component.addItemToBasket();
    expect(basketServiceStub.addItemToBasket).not.toHaveBeenCalled();
  });

  it('re-enables add-to-cart when the hub link recovers to Connected (QA F12)', () => {
    connectionState$.next(HubConnectionState.Disconnected);
    stockServiceStub.getStock$.and.returnValue(of(3));
    fixture.detectChanges();
    let cartButton = fixture.nativeElement.querySelector('button.fa-shopping-cart');
    expect(cartButton.disabled).toBe(true);

    connectionState$.next(HubConnectionState.Connected);
    fixture.detectChanges();
    cartButton = fixture.nativeElement.querySelector('button.fa-shopping-cart');
    expect(cartButton.disabled).toBe(false);
  });

  // ---------------------------------------------------------------------------
  // QA F6 (MINOR): the low-stock threshold follows the server-broadcast value,
  // not a hardcoded client constant of 5.
  // ---------------------------------------------------------------------------

  it('uses the server-provided low-stock threshold rather than a hardcoded 5 (QA F6)', () => {
    const stock$ = new BehaviorSubject<number>(3);
    stockServiceStub.getStock$.and.returnValue(stock$.asObservable());
    lowStockThreshold$.next(2); // server says "low stock" means <= 2
    fixture.detectChanges();
    // stock 3 > threshold 2 => no low-stock badge (a hardcoded-5 impl WOULD show one).
    expect(fixture.nativeElement.querySelector('.badge-warning')).toBeNull();

    stock$.next(2); // now at the (lowered) threshold
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.badge-warning').textContent).toContain('Only 2 left!');

    stock$.next(5); // above the lowered threshold again
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.badge-warning')).toBeNull();
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
  // QA F5/H-A (MAJOR): release the per-product hub subscription on destroy so the
  // StockService reference count can reach zero (no monotonic subscription growth).
  // ---------------------------------------------------------------------------

  it('releases the per-product hub subscription on destroy (QA F5)', () => {
    fixture.detectChanges();
    expect(stockServiceStub.subscribeToProduct).toHaveBeenCalledWith(1);
    fixture.destroy();
    expect(stockServiceStub.unsubscribeFromProduct).toHaveBeenCalledWith(1);
  });

  // ---------------------------------------------------------------------------
  // QA F7 (MINOR): the icon-only add-to-cart button exposes an accessible name.
  // ---------------------------------------------------------------------------

  it('exposes an accessible name on the icon-only add-to-cart button (QA F7)', () => {
    fixture.detectChanges();
    const cartButton = fixture.nativeElement.querySelector('button.fa-shopping-cart');
    expect(cartButton.getAttribute('aria-label')).toContain('Add');
    expect(cartButton.getAttribute('aria-label')).toContain('to cart');
  });

  // ---------------------------------------------------------------------------
  // Route/instance reuse (P4-24): the catalog reuses ProductItem instances across
  // list re-renders and paging, so the bound @Input product can change identity on
  // an existing instance. The component must release the previous product's hub
  // tracking and rebind to the new product.
  // ---------------------------------------------------------------------------

  it('rebinds live-stock tracking when the @Input product changes identity (P4-24)', () => {
    fixture.detectChanges(); // bound to product 1
    expect(stockServiceStub.subscribeToProduct).toHaveBeenCalledWith(1);

    // The catalog swaps the product bound to this reused component instance.
    component.product = { id: 2, name: 'y', description: '', price: 2, pictureUrl: '', productType: '', productBrand: '' };
    component.ngOnChanges({
      product: { previousValue: undefined, currentValue: component.product, firstChange: false, isFirstChange: () => false }
    } as any);

    // The previous product's hub tracking is released and the new product is subscribed.
    expect(stockServiceStub.unsubscribeFromProduct).toHaveBeenCalledWith(1);
    expect(stockServiceStub.subscribeToProduct).toHaveBeenCalledWith(2);
  });
});
