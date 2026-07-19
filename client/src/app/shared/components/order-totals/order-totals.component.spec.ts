import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CommonModule } from '@angular/common';
import { NO_ERRORS_SCHEMA } from '@angular/core';
import { OrderTotalsComponent } from './order-totals.component';

describe('OrderTotalsComponent', () => {
  let component: OrderTotalsComponent;
  let fixture: ComponentFixture<OrderTotalsComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [CommonModule],
      declarations: [OrderTotalsComponent],
      schemas: [NO_ERRORS_SCHEMA],
    }).compileComponents();

    fixture = TestBed.createComponent(OrderTotalsComponent);
    component = fixture.componentInstance;
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('should render the subtotal, shipping and total through the currency pipe', () => {
    component.subtotal = 100.5;
    component.shippingPrice = 5.25;
    component.total = 105.75;

    fixture.detectChanges();

    const text: string = fixture.nativeElement.textContent;
    expect(text).toContain('100.5');
    expect(text).toContain('5.25');
    expect(text).toContain('105.75');

    // The template renders six <strong> elements: three muted labels interleaved
    // with three value cells, so the value cells are the odd indices (1, 3, 5).
    const values = fixture.nativeElement.querySelectorAll('strong');
    expect(values[1].textContent).toContain('100.5');
    expect(values[3].textContent).toContain('5.25');
    expect(values[5].textContent).toContain('105.75');
  });
});
