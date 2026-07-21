import { TestBed, ComponentFixture } from '@angular/core/testing';
import { CommonModule } from '@angular/common';
import { RouterTestingModule } from '@angular/router/testing';
import { of, BehaviorSubject } from 'rxjs';

import { ProductItemComponent } from './product-item.component';
import { StockService } from '../../core/services/stock.service';
import { BasketService } from '../../basket/basket.service';

describe('ProductItemComponent', () => {
  let component: ProductItemComponent;
  let fixture: ComponentFixture<ProductItemComponent>;
  let stockServiceStub: {
    subscribeToProduct: jasmine.Spy;
    unsubscribeFromProduct: jasmine.Spy;
    getStock$: jasmine.Spy;
  };
  let basketServiceStub: { addItemToBasket: jasmine.Spy };

  beforeEach(async () => {
    stockServiceStub = {
      subscribeToProduct: jasmine.createSpy('subscribeToProduct'),
      unsubscribeFromProduct: jasmine.createSpy('unsubscribeFromProduct'),
      getStock$: jasmine.createSpy('getStock$').and.returnValue(of(3))
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

  it('renders NO badge and does NOT disable add-to-cart while stock is unknown (undefined)', () => {
    stockServiceStub.getStock$.and.returnValue(of(undefined));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.badge')).toBeNull();
    const cartButton = fixture.nativeElement.querySelector('button.fa-shopping-cart');
    expect(cartButton.disabled).toBe(false);
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
  // Component lifecycle (P4-12 / P4-24): the card must release its per-product hub
  // tracking on destroy, and rebind when the bound @Input product changes identity
  // on a reused instance.
  // ---------------------------------------------------------------------------

  it('releases the per-product hub subscription on destroy (P4-12)', () => {
    fixture.detectChanges(); // ngOnInit -> subscribe to product 1
    expect(stockServiceStub.subscribeToProduct).toHaveBeenCalledWith(1);

    component.ngOnDestroy();

    // The hub tracking for the destroyed card's product is released (leaves the server group).
    expect(stockServiceStub.unsubscribeFromProduct).toHaveBeenCalledWith(1);
  });

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
