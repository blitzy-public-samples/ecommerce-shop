import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CommonModule } from '@angular/common';
import { RouterTestingModule } from '@angular/router/testing';

import { BasketSummaryComponent } from './basket-summary.component';
import { IBasketItem } from '../models/basket';
import { IOrderItem } from '../models/order';

describe('BasketSummaryComponent', () => {
  let component: BasketSummaryComponent;
  let fixture: ComponentFixture<BasketSummaryComponent>;

  const mockBasketItems: IBasketItem[] = [
    {
      id: 1,
      productName: 'Angular Speedster Board 2000',
      price: 200,
      quantity: 1,
      pictureUrl: 'https://test.com/images/product-1.png',
      brand: 'Angular',
      type: 'Boards'
    },
    {
      id: 2,
      productName: 'Green Angular Board 3000',
      price: 150,
      quantity: 2,
      pictureUrl: 'https://test.com/images/product-2.png',
      brand: 'Angular',
      type: 'Boards'
    }
  ];

  const mockOrderItems: IOrderItem[] = [
    {
      productId: 10,
      productName: 'Core Purple Boots',
      price: 250,
      quantity: 1,
      pictureUrl: 'https://test.com/images/product-10.png'
    }
  ];

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      // Real template compilation (no NO_ERRORS_SCHEMA masking): CommonModule
      // supplies *ngIf/*ngFor/currency and RouterTestingModule supplies routerLink,
      // which are the only template dependencies BasketSummaryComponent renders.
      imports: [CommonModule, RouterTestingModule],
      declarations: [BasketSummaryComponent]
    }).compileComponents();
  });

  beforeEach(() => {
    fixture = TestBed.createComponent(BasketSummaryComponent);
    component = fixture.componentInstance;
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('should not render a table when there are no items', () => {
    component.items = [];
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('table')).toBeNull();
    expect(fixture.nativeElement.querySelectorAll('tbody tr').length).toEqual(0);
  });

  it('should render one row per item when items are provided', () => {
    component.items = mockBasketItems;
    fixture.detectChanges();

    const rows = fixture.nativeElement.querySelectorAll('tbody tr');
    expect(rows.length).toEqual(mockBasketItems.length);
    expect(fixture.nativeElement.textContent).toContain('Angular Speedster Board 2000');
  });

  it('should show quantity and remove controls in basket mode', () => {
    component.items = mockBasketItems;
    component.isBasket = true;
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('.fa-minus-circle').length).toEqual(mockBasketItems.length);
    expect(fixture.nativeElement.querySelectorAll('.fa-plus-circle').length).toEqual(mockBasketItems.length);
    expect(fixture.nativeElement.querySelectorAll('.fa-trash').length).toEqual(mockBasketItems.length);
  });

  it('QA F7: quantity and remove controls are keyboard-operable buttons with accessible names', () => {
    component.items = mockBasketItems;
    component.isBasket = true;
    fixture.detectChanges();

    // Decrease / increase are real <button>s (keyboard-operable, not mouse-only
    // <i> icons) carrying explicit accessible names.
    const decBtns = fixture.nativeElement.querySelectorAll('button[aria-label="Decrease quantity"]');
    const incBtns = fixture.nativeElement.querySelectorAll('button[aria-label="Increase quantity"]');
    expect(decBtns.length).toEqual(mockBasketItems.length);
    expect(incBtns.length).toEqual(mockBasketItems.length);

    // The remove control — the documented gate-recovery path — is a real <button>
    // with a descriptive, per-product accessible name.
    const removeBtn = fixture.nativeElement.querySelector(
      'button[aria-label="Remove Angular Speedster Board 2000 from basket"]'
    );
    expect(removeBtn).toBeTruthy();
    expect(removeBtn.tagName).toBe('BUTTON');

    // The decorative glyphs are hidden from assistive technology.
    const icon = fixture.nativeElement.querySelector('.fa-trash');
    expect(icon.getAttribute('aria-hidden')).toBe('true');
  });

  it('QA F7: clicking the button controls still emits the expected item outputs', () => {
    component.items = mockBasketItems;
    component.isBasket = true;
    fixture.detectChanges();

    const emitted: { dec?: IBasketItem; inc?: IBasketItem; rem?: IBasketItem } = {};
    component.decrement.subscribe((v: IBasketItem) => (emitted.dec = v));
    component.increment.subscribe((v: IBasketItem) => (emitted.inc = v));
    component.remove.subscribe((v: IBasketItem) => (emitted.rem = v));

    fixture.nativeElement.querySelector('button[aria-label="Decrease quantity"]').click();
    fixture.nativeElement.querySelector('button[aria-label="Increase quantity"]').click();
    fixture.nativeElement
      .querySelector('button[aria-label="Remove Angular Speedster Board 2000 from basket"]')
      .click();

    expect(emitted.dec).toBe(mockBasketItems[0]);
    expect(emitted.inc).toBe(mockBasketItems[0]);
    expect(emitted.rem).toBe(mockBasketItems[0]);
  });

  it('should hide quantity and remove controls when not in basket mode', () => {
    component.items = mockOrderItems;
    component.isBasket = false;
    component.isOrder = true;
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('.fa-minus-circle').length).toEqual(0);
    expect(fixture.nativeElement.querySelectorAll('.fa-plus-circle').length).toEqual(0);
    expect(fixture.nativeElement.querySelectorAll('.fa-trash').length).toEqual(0);
    expect(fixture.nativeElement.querySelectorAll('tbody tr').length).toEqual(mockOrderItems.length);
  });

  it('should emit the item on decrement when decrementItemQuantity is called', () => {
    const item = mockBasketItems[0];
    let emitted: IBasketItem | undefined;
    component.decrement.subscribe((value: IBasketItem) => (emitted = value));

    component.decrementItemQuantity(item);

    expect(emitted).toBe(item);
  });

  it('should emit the item on increment when incrementItemQuantity is called', () => {
    const item = mockBasketItems[0];
    let emitted: IBasketItem | undefined;
    component.increment.subscribe((value: IBasketItem) => (emitted = value));

    component.incrementItemQuantity(item);

    expect(emitted).toBe(item);
  });

  it('should emit the item on remove when removeBasketItem is called', () => {
    const item = mockBasketItems[0];
    let emitted: IBasketItem | undefined;
    component.remove.subscribe((value: IBasketItem) => (emitted = value));

    component.removeBasketItem(item);

    expect(emitted).toBe(item);
  });
});
