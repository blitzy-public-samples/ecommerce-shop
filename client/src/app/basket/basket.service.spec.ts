import { TestBed } from '@angular/core/testing';
import { HttpClientTestingModule, HttpTestingController } from '@angular/common/http/testing';

import { BasketService } from './basket.service';
import { environment } from '../../environments/environment';
import { IBasket, IBasketItem, IBasketTotals } from '../shared/models/basket';
import { IProduct } from '../shared/models/product';
import { IDeliveryMethod } from '../shared/models/deliveryMethod';

// Unit tests for BasketService. The service is exercised in complete isolation:
// every HTTP call is intercepted by HttpTestingController (no real network I/O) and
// localStorage interactions are observed with Jasmine spies. Tests follow the
// Jasmine describe/it + Arrange-Act-Assert style established by app.component.spec.ts.
describe('BasketService', () => {
  let service: BasketService;
  let httpMock: HttpTestingController;
  const apiUrl = environment.apiUrl;

  // --- Arrange helpers ---------------------------------------------------------
  // Build a basket item matching the IBasketItem contract.
  const makeItem = (id: number, price: number, quantity: number): IBasketItem => ({
    id,
    productName: 'Product ' + id,
    price,
    quantity,
    pictureUrl: 'img/' + id + '.png',
    brand: 'BrandName',
    type: 'TypeName'
  });

  // Build a basket (defaults shippingPrice to 0 unless supplied).
  const makeBasket = (id: string, items: IBasketItem[], shippingPrice = 0): IBasket => ({
    id,
    items,
    shippingPrice
  });

  // Build a product matching the IProduct contract, used to exercise addItemToBasket.
  const makeProduct = (id: number, price: number): IProduct => ({
    id,
    name: 'Product ' + id,
    description: 'desc',
    price,
    pictureUrl: 'img/' + id + '.png',
    productType: 'TypeName',
    productBrand: 'BrandName'
  });

  // Prime the service with a "current" basket. After the POST from setBasket is
  // flushed, getCurrentBasketValue() returns the same object reference that was
  // flushed, so subsequent mutating methods act on it deterministically.
  const primeBasket = (basket: IBasket): void => {
    service.setBasket(basket);
    httpMock.expectOne(apiUrl + 'basket').flush(basket);
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HttpClientTestingModule],
      providers: [BasketService]
    });
    service = TestBed.inject(BasketService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // Guarantees no unexpected or outstanding HTTP requests remain.
    httpMock.verify();
  });

  // 1. Creation / initial state
  it('should be created', () => {
    expect(service).toBeTruthy();
  });

  it('should start with shipping 0 and null basket/total streams', () => {
    // Arrange
    let basket: IBasket;
    let totals: IBasketTotals;
    service.basket$.subscribe(b => basket = b);
    service.basketTotal$.subscribe(t => totals = t);

    // Assert
    expect(service.shipping).toBe(0);
    expect(service.getCurrentBasketValue()).toBeNull();
    expect(basket).toBeNull();
    expect(totals).toBeNull();
  });

  // 2. getBasket
  describe('getBasket', () => {
    it('should GET the basket, set shipping and compute totals', () => {
      // Arrange
      const mock = makeBasket('b1', [makeItem(1, 10, 2), makeItem(2, 5, 1)], 10);
      let basket: IBasket;
      let totals: IBasketTotals;
      service.basket$.subscribe(b => basket = b);
      service.basketTotal$.subscribe(t => totals = t);

      // Act — cold observable, must subscribe to fire the request.
      service.getBasket('b1').subscribe();
      const req = httpMock.expectOne(apiUrl + 'basket?id=b1');
      expect(req.request.method).toBe('GET');
      req.flush(mock);

      // Assert — shipping taken from basket.shippingPrice; totals use this.shipping.
      expect(basket).toEqual(mock);
      expect(service.getCurrentBasketValue()).toEqual(mock);
      expect(service.shipping).toBe(10);
      expect(totals).toEqual({ shipping: 10, subtotal: 25, total: 35 });
    });
  });

  // 3. setBasket (QUIRK #1: totals use this.shipping, default 0, not basket.shippingPrice)
  describe('setBasket', () => {
    it('should POST the basket and update state (shipping stays 0)', () => {
      // Arrange
      const mock = makeBasket('b2', [makeItem(1, 20, 3)], 5);
      let basket: IBasket;
      let totals: IBasketTotals;
      service.basket$.subscribe(b => basket = b);
      service.basketTotal$.subscribe(t => totals = t);

      // Act — setBasket subscribes internally, so the request fires immediately.
      service.setBasket(mock);
      const req = httpMock.expectOne(apiUrl + 'basket');
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual(mock);
      req.flush(mock);

      // Assert — shipping is 0 despite basket.shippingPrice === 5 (QUIRK #1).
      expect(basket).toEqual(mock);
      expect(totals).toEqual({ shipping: 0, subtotal: 60, total: 60 });
    });
  });

  // 4. deleteBasket
  describe('deleteBasket', () => {
    it('should DELETE, clear streams and remove basket_id', () => {
      // Arrange
      spyOn(localStorage, 'removeItem');
      const mock = makeBasket('b3', [makeItem(1, 10, 1)]);
      let basket: IBasket;
      service.basket$.subscribe(b => basket = b);

      // Act — deleteBasket subscribes internally, so the request fires immediately.
      service.deleteBasket(mock);
      const req = httpMock.expectOne(apiUrl + 'basket?id=b3');
      expect(req.request.method).toBe('DELETE');
      req.flush(null);

      // Assert
      expect(basket).toBeNull();
      expect(service.getCurrentBasketValue()).toBeNull();
      expect(localStorage.removeItem).toHaveBeenCalledWith('basket_id');
    });
  });

  // 5 & 6. addItemToBasket
  describe('addItemToBasket', () => {
    it('should create a basket, persist basket_id and POST the mapped item', () => {
      // Arrange
      const setItem = spyOn(localStorage, 'setItem');
      let basket: IBasket;
      service.basket$.subscribe(b => basket = b);

      // Act — no current basket, so a new Basket() is created (auto uuid + items []).
      service.addItemToBasket(makeProduct(1, 15), 2);
      const req = httpMock.expectOne(apiUrl + 'basket');
      expect(req.request.method).toBe('POST');
      const sent = req.request.body as IBasket;

      // Assert — exact product -> basket item mapping.
      expect(sent.items.length).toBe(1);
      expect(sent.items[0]).toEqual({
        id: 1,
        productName: 'Product 1',
        price: 15,
        quantity: 2,
        pictureUrl: 'img/1.png',
        brand: 'BrandName',
        type: 'TypeName'
      });
      req.flush(sent);

      expect(setItem).toHaveBeenCalledWith('basket_id', jasmine.any(String));
      expect(basket.items[0].id).toBe(1);
    });

    it('should increment quantity when the product already exists in the basket', () => {
      // Arrange — prime a current basket already containing item id=1 qty=1.
      primeBasket(makeBasket('bx', [makeItem(1, 10, 1)]));

      // Act — add the same product id again with quantity 2 (addOrUpdateItem else branch).
      service.addItemToBasket(makeProduct(1, 15), 2);
      const req = httpMock.expectOne(apiUrl + 'basket');
      const sent = req.request.body as IBasket;

      // Assert — existing item quantity accumulates to 3.
      expect(sent.items.length).toBe(1);
      expect(sent.items[0].id).toBe(1);
      expect(sent.items[0].quantity).toBe(3);
      req.flush(sent);
      expect(service.getCurrentBasketValue().items[0].quantity).toBe(3);
    });
  });

  // 7. incrementItemQuantity
  describe('incrementItemQuantity', () => {
    it('should increment matching item and POST', () => {
      // Arrange
      const item = makeItem(1, 10, 1);
      primeBasket(makeBasket('b4', [item]));

      // Act
      service.incrementItemQuantity({ ...item });
      const req = httpMock.expectOne(apiUrl + 'basket');
      const sent = req.request.body as IBasket;

      // Assert
      expect(sent.items[0].quantity).toBe(2);
      req.flush(sent);
      expect(service.getCurrentBasketValue().items[0].quantity).toBe(2);
    });
  });

  // 8 & 9. decrementItemQuantity
  describe('decrementItemQuantity', () => {
    it('should decrement when quantity > 1 and POST once', () => {
      // Arrange — single item, quantity 2, so the quantity>1 branch runs (one POST).
      const item = makeItem(1, 10, 2);
      primeBasket(makeBasket('b5', [item]));

      // Act
      service.decrementItemQuantity({ ...item });
      const req = httpMock.expectOne(apiUrl + 'basket');
      const sent = req.request.body as IBasket;

      // Assert
      expect(sent.items[0].quantity).toBe(1);
      req.flush(sent);
      expect(service.getCurrentBasketValue().items[0].quantity).toBe(1);
    });

    it('should remove item then re-persist when dropping below 1 (two POSTs)', () => {
      // Arrange — two items each qty 1. Decrementing item1 hits the else branch:
      // removeItemFromBasket -> setBasket (POST #1), then the trailing setBasket
      // in decrementItemQuantity fires POST #2 (QUIRK #2).
      const item1 = makeItem(1, 10, 1);
      const item2 = makeItem(2, 20, 1);
      const basket = makeBasket('b6', [item1, item2]);
      primeBasket(basket);

      // Act
      service.decrementItemQuantity({ ...item1 });
      const reqs = httpMock.match(apiUrl + 'basket');

      // Assert — exactly two POSTs; remaining basket has only item id=2.
      expect(reqs.length).toBe(2);
      reqs.forEach(r => {
        expect(r.request.method).toBe('POST');
        r.flush(basket);
      });

      const current = service.getCurrentBasketValue();
      expect(current.items.length).toBe(1);
      expect(current.items[0].id).toBe(2);
    });
  });

  // 10 & 11. removeItemFromBasket
  describe('removeItemFromBasket', () => {
    it('should filter and POST when items remain', () => {
      // Arrange
      const item1 = makeItem(1, 10, 1);
      const item2 = makeItem(2, 20, 2);
      const basket = makeBasket('b7', [item1, item2]);
      primeBasket(basket);

      // Act
      service.removeItemFromBasket({ ...item1 });
      const req = httpMock.expectOne(apiUrl + 'basket');
      const sent = req.request.body as IBasket;

      // Assert — item1 removed, item2 remains.
      expect(sent.items.length).toBe(1);
      expect(sent.items[0].id).toBe(2);
      req.flush(sent);
      expect(service.getCurrentBasketValue().items.length).toBe(1);
    });

    it('should DELETE when removing the last item', () => {
      // Arrange
      spyOn(localStorage, 'removeItem');
      const item1 = makeItem(1, 10, 1);
      primeBasket(makeBasket('b8', [item1]));

      // Act — removing the only item takes the deleteBasket branch.
      service.removeItemFromBasket({ ...item1 });
      const req = httpMock.expectOne(apiUrl + 'basket?id=b8');
      expect(req.request.method).toBe('DELETE');
      req.flush(null);

      // Assert
      expect(service.getCurrentBasketValue()).toBeNull();
      expect(localStorage.removeItem).toHaveBeenCalledWith('basket_id');
    });
  });

  // 12. deleteLocalBasket (no HTTP)
  describe('deleteLocalBasket', () => {
    it('should clear streams and remove basket_id without HTTP', () => {
      // Arrange
      spyOn(localStorage, 'removeItem');
      let basket: IBasket;
      let totals: IBasketTotals;
      service.basket$.subscribe(b => basket = b);
      service.basketTotal$.subscribe(t => totals = t);

      // Act — purely local; no HTTP request is issued (enforced by afterEach verify()).
      service.deleteLocalBasket('x');

      // Assert
      expect(basket).toBeNull();
      expect(totals).toBeNull();
      expect(service.getCurrentBasketValue()).toBeNull();
      expect(localStorage.removeItem).toHaveBeenCalledWith('basket_id');
    });
  });

  // 13. createPaymentIntent
  describe('createPaymentIntent', () => {
    it('should POST to payments/{id} and update basket$', () => {
      // Arrange
      const basket = makeBasket('b9', [makeItem(1, 10, 1)]);
      primeBasket(basket);

      const updated: IBasket = { ...basket, clientSecret: 'secret', paymentIntentId: 'pi_1' };
      let emitted: IBasket;
      service.basket$.subscribe(b => emitted = b);

      // Act — cold observable, must subscribe to fire the request.
      service.createPaymentIntent().subscribe();
      const req = httpMock.expectOne(apiUrl + 'payments/b9');
      expect(req.request.method).toBe('POST');
      req.flush(updated);

      // Assert
      expect(emitted).toEqual(updated);
    });
  });

  // 14. setShippingPrice
  describe('setShippingPrice', () => {
    it('should apply delivery method, recalc totals and POST', () => {
      // Arrange
      const basket = makeBasket('b10', [makeItem(1, 10, 2)]);
      primeBasket(basket);

      const dm: IDeliveryMethod = {
        id: 3,
        shortName: 'UPS1',
        deliveryTime: '1-2',
        description: 'fast',
        price: 10
      };
      let totals: IBasketTotals;
      service.basketTotal$.subscribe(t => totals = t);

      // Act
      service.setShippingPrice(dm);
      const req = httpMock.expectOne(apiUrl + 'basket');
      const sent = req.request.body as IBasket;

      // Assert — delivery method applied to basket and shipping reflected in totals.
      expect(sent.deliveryMethodId).toBe(3);
      expect(sent.shippingPrice).toBe(10);
      req.flush(sent);

      expect(service.shipping).toBe(10);
      expect(totals).toEqual({ shipping: 10, subtotal: 20, total: 30 });
    });
  });

  // 15. getOrCreateBasketId (M15-fe: shared ensure-basket-ID path; no HTTP)
  describe('getOrCreateBasketId', () => {
    it('should return the existing basket_id without creating a new one', () => {
      // Arrange — a basket UUID already exists in localStorage.
      spyOn(localStorage, 'getItem').and.returnValue('existing-basket-uuid');
      const setItem = spyOn(localStorage, 'setItem');

      // Act — purely local; afterEach verify() guarantees no HTTP was issued.
      const id = service.getOrCreateBasketId();

      // Assert — the persisted id is returned verbatim and no new basket is created/persisted.
      expect(id).toBe('existing-basket-uuid');
      expect(localStorage.getItem).toHaveBeenCalledWith('basket_id');
      expect(setItem).not.toHaveBeenCalled();
    });

    it('should create and persist a new basket UUID when none exists', () => {
      // Arrange — no basket UUID yet.
      spyOn(localStorage, 'getItem').and.returnValue(null);
      const setItem = spyOn(localStorage, 'setItem');

      // Act
      const id = service.getOrCreateBasketId();

      // Assert — a fresh UUID is generated, persisted under basket_id and returned (same
      // createBasket() path used by addItemToBasket). No HTTP is issued.
      expect(id).toBeTruthy();
      expect(setItem).toHaveBeenCalledWith('basket_id', id);
    });
  });

  // 16. F2 regression (CRITICAL): reserve-then-add must reuse ONE basket UUID.
  // A first-time shopper reserves a flash-sale item via getOrCreateBasketId() (which mints and
  // seeds a basket) and THEN adds it via addItemToBasket(). Both must operate on the SAME basket
  // UUID so the reservation session_id equals the order's basket UUID (AAP R8/R5) and no orphaned
  // hold can re-release sold stock (zero-oversell, AAP R3). Guards QA finding F2: before the fix,
  // createBasket() did not seed basketSource, so addItemToBasket()'s
  // `getCurrentBasketValue() ?? createBasket()` minted a SECOND UUID and overwrote basket_id.
  describe('F2 reserve-then-add session identity', () => {
    it('should reuse the same basket UUID for getOrCreateBasketId() then addItemToBasket()', () => {
      // Arrange — first-time shopper: no basket_id persisted yet.
      spyOn(localStorage, 'getItem').and.returnValue(null);
      const setItem = spyOn(localStorage, 'setItem');

      // Act (step 1) — the reserve path resolves/mints the session id (the basket UUID).
      const sessionId = service.getOrCreateBasketId();

      // The just-minted basket must be the current basket immediately (the fix seeds basketSource),
      // otherwise the subsequent add would mint a divergent UUID.
      expect(sessionId).toBeTruthy();
      expect(service.getCurrentBasketValue()).toBeTruthy();
      expect(service.getCurrentBasketValue().id).toBe(sessionId);

      // Act (step 2) — add the item, exactly as product-details does after a successful reserve.
      service.addItemToBasket(makeProduct(1, 15), 2);
      const req = httpMock.expectOne(apiUrl + 'basket');
      expect(req.request.method).toBe('POST');
      const sent = req.request.body as IBasket;

      // Assert — the POSTed basket carries the SAME UUID returned to the reserve call (no divergence).
      expect(sent.id).toBe(sessionId);
      expect(sent.items.length).toBe(1);
      expect(sent.items[0].id).toBe(1);
      req.flush(sent);

      // Assert — basket_id was persisted exactly once and never overwritten with a different UUID.
      const basketIdWrites = setItem.calls.allArgs().filter(args => args[0] === 'basket_id');
      expect(basketIdWrites.length).toBe(1);
      expect(basketIdWrites[0][1]).toBe(sessionId);
    });
  });
});
