import { TestBed } from '@angular/core/testing';
import { HttpClientTestingModule, HttpTestingController } from '@angular/common/http/testing';

import { FlashSaleService } from './flash-sale.service';
import { environment } from '../../environments/environment';
import { IFlashSale } from '../shared/models/flash-sale';
import { IInventoryReservation } from '../shared/models/inventory';

// Unit tests for FlashSaleService. All HTTP traffic is intercepted by HttpTestingController -
// no real network I/O is performed. The reservation POST body MUST carry
// sessionId = localStorage['basket_id'] (the basket UUID), so the session-identity reuse is
// locked in below as a regression guard. The global ErrorInterceptor is intentionally NOT
// registered here, so 409/429 responses surface directly to the subscriber's error callback.
describe('FlashSaleService', () => {
  let service: FlashSaleService;
  let httpMock: HttpTestingController;

  const activeUrl = environment.apiUrl + 'flash-sales/active';
  const reserveUrl = environment.apiUrl + 'inventory/reserve';

  const mockFlashSales: IFlashSale[] = [
    { id: 1, productId: 1, startAt: '2025-01-01T00:00:00Z', endAt: '2025-12-31T00:00:00Z',
      salePrice: 150, stockAllocation: 100, quantityAvailable: 80 },
    { id: 2, productId: 2, startAt: '2025-01-01T00:00:00Z', endAt: '2025-12-31T00:00:00Z',
      salePrice: 90, stockAllocation: 50, quantityAvailable: 10 }
  ];
  const mockReservation: IInventoryReservation = {
    id: 10, productId: 1, quantity: 2, sessionId: 'basket-123', expiresAt: '2025-01-01T00:05:00Z'
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HttpClientTestingModule],
      providers: [FlashSaleService]
    });
    service = TestBed.inject(FlashSaleService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // Fails the test if any request was made but not matched/flushed.
    httpMock.verify();
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });

  describe('getActiveFlashSales', () => {
    it('should GET the (non-cached) active flash-sales endpoint', () => {
      let result: IFlashSale[];
      service.getActiveFlashSales().subscribe(res => (result = res));

      const req = httpMock.expectOne(activeUrl);
      expect(req.request.method).toBe('GET');
      req.flush(mockFlashSales);

      expect(result).toEqual(mockFlashSales);
    });
  });

  describe('getActiveFlashSale', () => {
    it('should resolve the single active sale for a product from the active list', () => {
      let result: IFlashSale;
      service.getActiveFlashSale(2).subscribe(res => (result = res));

      const req = httpMock.expectOne(activeUrl);
      expect(req.request.method).toBe('GET');
      req.flush(mockFlashSales);

      expect(result).toEqual(mockFlashSales[1]);
    });
  });

  describe('reserve', () => {
    it('should POST with sessionId taken from localStorage basket_id', () => {
      // Session-identity reuse: sessionId must be the basket UUID from localStorage['basket_id'].
      spyOn(localStorage, 'getItem').and.returnValue('basket-123');

      let result: IInventoryReservation;
      service.reserve(1, 2).subscribe(res => (result = res));

      const req = httpMock.expectOne(reserveUrl);
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({ productId: 1, quantity: 2, sessionId: 'basket-123' });
      expect(localStorage.getItem).toHaveBeenCalledWith('basket_id');
      req.flush(mockReservation);

      expect(result).toEqual(mockReservation);
    });

    it('should surface a 409 INSUFFICIENT_STOCK error to the caller', () => {
      spyOn(localStorage, 'getItem').and.returnValue('basket-123');

      let errorResponse: any;
      service.reserve(1, 999).subscribe({
        next: () => { /* success is not expected in this test */ },
        error: err => (errorResponse = err)
      });

      const req = httpMock.expectOne(reserveUrl);
      req.flush({ error: 'INSUFFICIENT_STOCK', available: 5 }, { status: 409, statusText: 'Conflict' });

      expect(errorResponse.status).toBe(409);
      expect(errorResponse.error.error).toBe('INSUFFICIENT_STOCK');
      expect(errorResponse.error.available).toBe(5);
    });
  });

  describe('releaseReservation', () => {
    it('should DELETE the reservation by id', () => {
      service.releaseReservation(10).subscribe();

      const req = httpMock.expectOne(reserveUrl + '/10');
      expect(req.request.method).toBe('DELETE');
      req.flush({});
    });
  });
});
