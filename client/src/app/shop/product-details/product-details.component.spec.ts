import { TestBed, ComponentFixture } from '@angular/core/testing';
import { NO_ERRORS_SCHEMA } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { BreadcrumbService } from 'xng-breadcrumb';
import { Subject, of, throwError } from 'rxjs';

import { ProductDetailsComponent } from './product-details.component';
import { ShopService } from '../shop.service';
import { BasketService } from '../../basket/basket.service';
import { InventoryHubService } from '../../core/services/inventory-hub.service';
import { FlashSaleService } from '../flash-sale.service';
import { IFlashSale } from '../../shared/models/flash-sale';
import { IProduct } from '../../shared/models/product';
import { IInventoryReservation } from '../../shared/models/inventory';

// Behavioural unit tests for the C2-fe reserve-before-basket flow and M14 expiry handling. The hub +
// poll wiring in ngOnInit is intentionally NOT exercised here (we never call detectChanges/ngOnInit),
// so no timers/sockets run - the public methods are driven directly against mocked services.
describe('ProductDetailsComponent (reserve flow)', () => {
  let component: ProductDetailsComponent;
  let fixture: ComponentFixture<ProductDetailsComponent>;

  let shopService: jasmine.SpyObj<ShopService>;
  let basketService: jasmine.SpyObj<BasketService>;
  let flashSaleService: jasmine.SpyObj<FlashSaleService>;
  let hubService: any;

  const product: IProduct = {
    id: 1, name: 'P1', description: 'd', price: 200,
    pictureUrl: 'x.png', productType: 'T', productBrand: 'B'
  };
  const sale: IFlashSale = {
    id: 9, productId: 1, startAt: '2025-01-01T00:00:00Z', endAt: '2025-12-31T00:00:00Z',
    salePrice: 150, stockAllocation: 100, quantityAvailable: 8
  };
  const reservation: IInventoryReservation = {
    id: 55, productId: 1, quantity: 2, sessionId: 'basket-uuid', expiresAt: '2025-01-01T00:05:00Z'
  };

  beforeEach(() => {
    shopService = jasmine.createSpyObj('ShopService', ['getProduct']);
    shopService.getProduct.and.returnValue(of(product));
    basketService = jasmine.createSpyObj('BasketService', ['addItemToBasket', 'getOrCreateBasketId']);
    basketService.getOrCreateBasketId.and.returnValue('basket-uuid');
    flashSaleService = jasmine.createSpyObj('FlashSaleService', ['getActiveFlashSale', 'reserve', 'releaseReservation']);
    flashSaleService.getActiveFlashSale.and.returnValue(of(undefined));
    flashSaleService.reserve.and.returnValue(of(reservation));
    flashSaleService.releaseReservation.and.returnValue(of(null));

    // Hub mock: streams as Subjects, lifecycle as resolved promises.
    hubService = {
      inventoryUpdated$: new Subject(), flashSaleStarted$: new Subject(),
      flashSaleEnded$: new Subject(), reconnected$: new Subject(),
      acquire: jasmine.createSpy('acquire').and.returnValue(Promise.resolve()),
      joinProductGroup: jasmine.createSpy('joinProductGroup').and.returnValue(Promise.resolve()),
      leaveProductGroup: jasmine.createSpy('leaveProductGroup').and.returnValue(Promise.resolve()),
      release: jasmine.createSpy('release').and.returnValue(Promise.resolve())
    };

    TestBed.configureTestingModule({
      declarations: [ProductDetailsComponent],
      // NO_ERRORS_SCHEMA: the child widget elements (app-flash-sale-banner, etc.) are not declared in
      // this focused unit test; the tests drive the component's methods, not its rendered template.
      schemas: [NO_ERRORS_SCHEMA],
      providers: [
        { provide: ShopService, useValue: shopService },
        { provide: BasketService, useValue: basketService },
        { provide: InventoryHubService, useValue: hubService },
        { provide: FlashSaleService, useValue: flashSaleService },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => '1' } } } },
        // xng-breadcrumb BreadcrumbService is injected in the constructor; a minimal stub avoids
        // pulling the real breadcrumb module into this unit test.
        { provide: BreadcrumbService, useValue: { set: () => { } } }
      ]
    });

    fixture = TestBed.createComponent(ProductDetailsComponent);
    component = fixture.componentInstance;
    // Drive method-level behaviour directly (no ngOnInit -> no hub/timer).
    component.product = product;
    (component as any).productId = 1;
    component.quantity = 2;
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('adds a NON-sale product straight to the basket without reserving', () => {
    component.activeFlashSale = undefined;

    component.addItemToBasket();

    expect(flashSaleService.reserve).not.toHaveBeenCalled();
    expect(basketService.addItemToBasket).toHaveBeenCalledWith(product, 2);
  });

  it('RESERVES before mutating the basket for an active-sale product (C2-fe)', () => {
    component.activeFlashSale = sale;
    component.quantityAvailable = 8;

    component.addItemToBasket();

    // Ensures the basket UUID exists first (M15-fe), then reserves, then adds to basket, tracking id.
    expect(basketService.getOrCreateBasketId).toHaveBeenCalled();
    expect(flashSaleService.reserve).toHaveBeenCalledWith(1, 2);
    expect(basketService.addItemToBasket).toHaveBeenCalledWith(product, 2);
    expect((component as any).reservationIds).toEqual([55]);
    expect(component.reservationError).toBeUndefined();
  });

  it('surfaces 409 INSUFFICIENT_STOCK and does NOT mutate the basket (C2-fe)', () => {
    component.activeFlashSale = sale;
    component.quantityAvailable = 8;
    flashSaleService.reserve.and.returnValue(
      throwError({ status: 409, error: { error: 'INSUFFICIENT_STOCK', available: 3 } })
    );

    component.addItemToBasket();

    expect(basketService.addItemToBasket).not.toHaveBeenCalled();
    expect(component.reservationError).toContain('3');
    expect(component.reserving).toBeFalse();
  });

  it('surfaces a 429 rate-limit message and does NOT mutate the basket (C2-fe)', () => {
    component.activeFlashSale = sale;
    component.quantityAvailable = 8;
    flashSaleService.reserve.and.returnValue(throwError({ status: 429, error: {} }));

    component.addItemToBasket();

    expect(basketService.addItemToBasket).not.toHaveBeenCalled();
    expect(component.reservationError).toContain('too quickly');
  });

  it('disables Add to Cart when the active sale is sold out (isSaleOutOfStock)', () => {
    component.activeFlashSale = sale;
    component.quantityAvailable = 0;

    expect(component.isSaleOutOfStock).toBeTrue();
    component.addItemToBasket(); // guarded no-op when sold out
    expect(flashSaleService.reserve).not.toHaveBeenCalled();
  });

  it('caps increment at the live available stock while a sale is active', () => {
    component.activeFlashSale = sale;
    component.quantityAvailable = 2;
    component.quantity = 2;

    component.incrementQuantity();

    expect(component.quantity).toBe(2); // capped
  });

  it('clears and refetches the sale on countdown expiry (M14)', () => {
    component.activeFlashSale = sale;
    component.quantityAvailable = 8;

    component.onFlashSaleExpired();

    expect(component.activeFlashSale).toBeUndefined();
    expect(component.quantityAvailable).toBeUndefined();
    expect(flashSaleService.getActiveFlashSale).toHaveBeenCalledWith(1);
  });

  it('releases tracked reservations with the owning sessionId on destroy (C2-fe/M6-fe)', () => {
    spyOn(localStorage, 'getItem').and.returnValue('basket-uuid');
    (component as any).reservationIds = [55, 56];

    component.ngOnDestroy();

    expect(flashSaleService.releaseReservation).toHaveBeenCalledWith(55, 'basket-uuid');
    expect(flashSaleService.releaseReservation).toHaveBeenCalledWith(56, 'basket-uuid');
    expect((component as any).reservationIds).toEqual([]);
  });
});
