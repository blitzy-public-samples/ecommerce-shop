import { TestBed, ComponentFixture } from '@angular/core/testing';
import { CommonModule } from '@angular/common';
import { RouterTestingModule } from '@angular/router/testing';
import { ActivatedRoute } from '@angular/router';
import { BreadcrumbService } from 'xng-breadcrumb';
import { of } from 'rxjs';

import { ProductDetailsComponent } from './product-details.component';
import { StockService } from '../../core/services/stock.service';
import { ShopService } from '../shop.service';
import { BasketService } from '../../basket/basket.service';

describe('ProductDetailsComponent', () => {
  let component: ProductDetailsComponent;
  let fixture: ComponentFixture<ProductDetailsComponent>;
  let stockServiceStub: { subscribeToProduct: jasmine.Spy; getStock$: jasmine.Spy };
  let shopServiceStub: { getProduct: jasmine.Spy };
  let bcServiceStub: { set: jasmine.Spy };
  let basketServiceStub: { addItemToBasket: jasmine.Spy };
  let activatedRouteStub: { snapshot: { paramMap: { get: () => string } } };

  beforeEach(async () => {
    // Stubs via useValue so the real HttpClient / SignalR / Router deps are never constructed.
    stockServiceStub = {
      subscribeToProduct: jasmine.createSpy('subscribeToProduct'),
      getStock$: jasmine.createSpy('getStock$').and.returnValue(of(3))
    };
    shopServiceStub = {
      getProduct: jasmine.createSpy('getProduct').and.returnValue(of({
        id: 1, name: 'x', price: 1, pictureUrl: '', productType: '', productBrand: '', description: ''
      }))
    };
    bcServiceStub = { set: jasmine.createSpy('set') };
    basketServiceStub = { addItemToBasket: jasmine.createSpy('addItemToBasket') };
    activatedRouteStub = { snapshot: { paramMap: { get: () => '1' } } };

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
});
