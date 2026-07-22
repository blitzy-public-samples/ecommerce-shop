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
    it('should request the product-filtered active endpoint and resolve the single sale', () => {
      // N1-fe: the productId is pushed to the server as a ?productId query parameter so the page no
      // longer downloads every active sale; the server returns only the matching sale(s).
      let result: IFlashSale;
      service.getActiveFlashSale(2).subscribe(res => (result = res));

      const req = httpMock.expectOne(
        r => r.method === 'GET' && r.url === activeUrl && r.params.get('productId') === '2'
      );
      req.flush([mockFlashSales[1]]);

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
    it('should DELETE the reservation by id and send the explicit owning sessionId', () => {
      // M6-fe: the backend requires the owning sessionId ([FromQuery] string sessionId) and returns
      // 400 without it. An explicit sessionId is forwarded verbatim as a query parameter, and the
      // successful release responds 204 No Content (no body).
      service.releaseReservation(10, 'basket-123').subscribe();

      const req = httpMock.expectOne(
        r => r.method === 'DELETE' && r.url === reserveUrl + '/10' && r.params.get('sessionId') === 'basket-123'
      );
      req.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('should default the sessionId to the basket UUID in localStorage when omitted', () => {
      // M6-fe: when the caller omits sessionId it defaults to localStorage['basket_id'] - the same
      // identity reserve() stamps on the hold - so an owner can release without re-supplying it.
      spyOn(localStorage, 'getItem').and.returnValue('ls-basket-uuid');

      service.releaseReservation(11).subscribe();

      const req = httpMock.expectOne(
        r => r.method === 'DELETE' && r.url === reserveUrl + '/11' && r.params.get('sessionId') === 'ls-basket-uuid'
      );
      expect(localStorage.getItem).toHaveBeenCalledWith('basket_id');
      req.flush(null, { status: 204, statusText: 'No Content' });
    });
  });
});
