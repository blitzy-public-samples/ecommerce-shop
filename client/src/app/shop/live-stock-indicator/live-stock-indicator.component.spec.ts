import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CommonModule } from '@angular/common';

import { LiveStockIndicatorComponent } from './live-stock-indicator.component';

describe('LiveStockIndicatorComponent', () => {
  let component: LiveStockIndicatorComponent;
  let fixture: ComponentFixture<LiveStockIndicatorComponent>;

  beforeEach(async () => {
    // Real template compilation (no NO_ERRORS_SCHEMA masking): CommonModule
    // supplies *ngIf and the class bindings this presentational widget renders.
    await TestBed.configureTestingModule({
      imports: [CommonModule],
      declarations: [LiveStockIndicatorComponent]
    }).compileComponents();
  });

  beforeEach(() => {
    fixture = TestBed.createComponent(LiveStockIndicatorComponent);
    component = fixture.componentInstance;
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('should show the available quantity without a danger style when stock is healthy', () => {
    component.quantityAvailable = 50;
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('50');
    expect(fixture.nativeElement.querySelector('.text-danger')).toBeNull();
  });

  it('should apply a low-stock danger style when at or below the threshold', () => {
    component.quantityAvailable = 2;
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('2');
    expect(fixture.nativeElement.querySelector('.text-danger')).toBeTruthy();
  });

  it('should show an out-of-stock message when quantity is zero', () => {
    component.quantityAvailable = 0;
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Out of stock');
  });
});
