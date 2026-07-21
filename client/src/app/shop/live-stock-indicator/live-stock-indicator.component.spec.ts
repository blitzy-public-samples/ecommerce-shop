import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CommonModule } from '@angular/common';

import { LiveStockIndicatorComponent } from './live-stock-indicator.component';

describe('LiveStockIndicatorComponent', () => {
  let component: LiveStockIndicatorComponent;
  let fixture: ComponentFixture<LiveStockIndicatorComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [CommonModule],
      declarations: [LiveStockIndicatorComponent]
    }).compileComponents();
  });

  beforeEach(() => {
    fixture = TestBed.createComponent(LiveStockIndicatorComponent);
    component = fixture.componentInstance;
  });

  /** The single persistent live region (M02). */
  function liveRegion(): HTMLElement {
    return fixture.nativeElement.querySelector('[role="status"]');
  }

  function renderedText(): string {
    return (liveRegion().textContent || '').trim().replace(/\s+/g, ' ');
  }

  function setQuantity(value: number, threshold?: number): void {
    component.quantityAvailable = value;
    if (threshold !== undefined) {
      component.lowStockThreshold = threshold;
    }
    fixture.detectChanges();
  }

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('exposes ONE persistent role=status aria-live region (M02)', () => {
    setQuantity(50);
    const region = liveRegion();
    expect(region).toBeTruthy();
    expect(region.getAttribute('role')).toBe('status');
    expect(region.getAttribute('aria-live')).toBe('polite');
    expect(region.getAttribute('aria-atomic')).toBe('true');
  });

  it('shows healthy stock with the accessible success class and no danger style (M03)', () => {
    setQuantity(50);
    expect(renderedText()).toBe('50 in stock');
    expect(fixture.nativeElement.querySelector('.stock-in')).toBeTruthy();
    // M03: must NOT use the low-contrast Bootstrap .text-success utility.
    expect(fixture.nativeElement.querySelector('.text-success')).toBeNull();
    expect(fixture.nativeElement.querySelector('.text-danger')).toBeNull();
  });

  it('treats threshold + 1 as healthy stock (boundary, m05)', () => {
    setQuantity(6, 5);
    expect(renderedText()).toBe('6 in stock');
    expect(fixture.nativeElement.querySelector('.stock-in')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.text-danger')).toBeNull();
  });

  it('treats exactly the threshold as low stock (boundary, m05)', () => {
    setQuantity(5, 5);
    expect(renderedText()).toBe('Only 5 left!');
    expect(fixture.nativeElement.querySelector('.text-danger')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.stock-in')).toBeNull();
  });

  it('treats a quantity of 1 as low stock (boundary, m05)', () => {
    setQuantity(1, 5);
    expect(renderedText()).toBe('Only 1 left!');
    expect(fixture.nativeElement.querySelector('.text-danger')).toBeTruthy();
  });

  it('shows an out-of-stock message when quantity is zero', () => {
    setQuantity(0);
    expect(renderedText()).toBe('Out of stock');
    expect(fixture.nativeElement.querySelector('.text-danger')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.stock-in')).toBeNull();
  });

  it('floors a non-integer quantity to a whole count (m04)', () => {
    setQuantity(3.9, 5);
    // 3.9 -> floor 3 -> low stock, rendered as an integer.
    expect(renderedText()).toBe('Only 3 left!');
  });

  it('renders an explicit unavailable state for undefined quantity (m04)', () => {
    setQuantity(undefined as unknown as number);
    expect(renderedText()).toBe('Stock unavailable');
    expect(fixture.nativeElement.querySelector('.stock-unavailable')).toBeTruthy();
    // Must never masquerade as a green "in stock" state.
    expect(fixture.nativeElement.querySelector('.stock-in')).toBeNull();
    expect(fixture.nativeElement.querySelector('.text-success')).toBeNull();
  });

  it('renders an explicit unavailable state for NaN quantity (m04)', () => {
    setQuantity(NaN);
    expect(renderedText()).toBe('Stock unavailable');
    expect(fixture.nativeElement.querySelector('.stock-unavailable')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.stock-in')).toBeNull();
  });

  it('renders an explicit unavailable state for a negative quantity (m04)', () => {
    setQuantity(-3);
    expect(renderedText()).toBe('Stock unavailable');
    expect(fixture.nativeElement.querySelector('.stock-unavailable')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.text-danger')).toBeNull();
  });

  it('updates content within the SAME live region after change detection (M02, m05)', () => {
    setQuantity(50);
    const regionBefore = liveRegion();
    expect(renderedText()).toBe('50 in stock');

    setQuantity(0);
    const regionAfter = liveRegion();
    // The live region element itself is persistent; only its content changes (M02).
    expect(regionAfter).toBe(regionBefore);
    expect(renderedText()).toBe('Out of stock');

    setQuantity(2, 5);
    expect(liveRegion()).toBe(regionBefore);
    expect(renderedText()).toBe('Only 2 left!');
  });
});
