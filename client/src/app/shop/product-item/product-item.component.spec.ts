import { TestBed, ComponentFixture } from '@angular/core/testing';
import { CommonModule } from '@angular/common';
import { RouterTestingModule } from '@angular/router/testing';
import { of } from 'rxjs';

import { ProductItemComponent } from './product-item.component';
import { StockService } from '../../core/services/stock.service';
import { BasketService } from '../../basket/basket.service';

describe('ProductItemComponent', () => {
  let component: ProductItemComponent;
  let fixture: ComponentFixture<ProductItemComponent>;
  let stockServiceStub: { subscribeToProduct: jasmine.Spy; getStock$: jasmine.Spy };
  let basketServiceStub: { addItemToBasket: jasmine.Spy };

  beforeEach(async () => {
    stockServiceStub = {
      subscribeToProduct: jasmine.createSpy('subscribeToProduct'),
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
});
