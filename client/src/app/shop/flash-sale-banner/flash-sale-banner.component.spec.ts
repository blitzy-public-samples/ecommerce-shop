import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CommonModule } from '@angular/common';

import { FlashSaleBannerComponent } from './flash-sale-banner.component';
import { IFlashSale } from '../../shared/models/flash-sale';

describe('FlashSaleBannerComponent', () => {
  let component: FlashSaleBannerComponent;
  let fixture: ComponentFixture<FlashSaleBannerComponent>;

  const mockSale: IFlashSale = {
    id: 1,
    productId: 1,
    startAt: '2025-01-01T00:00:00Z',
    endAt: '2025-12-31T00:00:00Z',
    salePrice: 150,
    stockAllocation: 100,
    quantityAvailable: 80
  };

  beforeEach(async () => {
    // Real template compilation (no NO_ERRORS_SCHEMA masking): CommonModule supplies the
    // currency pipe, which is the only template dependency this banner renders.
    await TestBed.configureTestingModule({
      imports: [CommonModule],
      declarations: [FlashSaleBannerComponent]
    }).compileComponents();
  });

  beforeEach(() => {
    fixture = TestBed.createComponent(FlashSaleBannerComponent);
    component = fixture.componentInstance;
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('should render the sale price and the struck-through base price', () => {
    component.flashSale = mockSale;
    component.basePrice = 200;
    fixture.detectChanges();

    const del = fixture.nativeElement.querySelector('del');
    expect(fixture.nativeElement.textContent).toContain('$150.00');
    expect(del).toBeTruthy();
    expect(del.textContent).toContain('$200.00');
  });
});
