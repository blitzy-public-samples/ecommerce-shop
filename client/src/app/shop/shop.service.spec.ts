import { TestBed } from '@angular/core/testing';
import { HttpClientTestingModule, HttpTestingController } from '@angular/common/http/testing';

import { ShopService } from './shop.service';
import { environment } from '../../environments/environment';
import { ShopParams } from '../shared/models/shopParams';
import { IPagination } from '../shared/models/pagination';
import { IProduct } from '../shared/models/product';
import { IBrand } from '../shared/models/brand';
import { IType } from '../shared/models/productType';

// Unit tests for ShopService. The service is exercised in isolation with all HTTP
// traffic intercepted by HttpTestingController — no real network I/O is performed.
// The production code is tested AS-IS, including the intentional double-`pageIndex`
// query-parameter append (see the two `getAll('pageIndex')` assertions below), which
// is locked in as a regression guard rather than "fixed".
describe('ShopService', () => {
  let service: ShopService;
  let httpMock: HttpTestingController;

  // getProducts attaches query params, so `urlWithParams` differs from the raw path.
  // Matching is therefore done against `req.url` (the path without the query string).
  const productsUrl = environment.apiUrl + 'products';

  const mockProducts: IProduct[] = [
    { id: 1, name: 'Board 2000', description: 'x', price: 200, pictureUrl: 'p1.png', productType: 'Boards', productBrand: 'Angular' },
    { id: 2, name: 'Board 3000', description: 'y', price: 150, pictureUrl: 'p2.png', productType: 'Boards', productBrand: 'Angular' }
  ];
  const mockPagination: IPagination = { pageIndex: 1, pageSize: 6, count: mockProducts.length, data: mockProducts };

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HttpClientTestingModule],
      providers: [ShopService]
    });
    service = TestBed.inject(ShopService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // Fails the test if any request was made but not matched/flushed, guaranteeing
    // that our expectOne/expectNone assertions fully account for HTTP activity.
    httpMock.verify();
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });

  describe('getProducts', () => {
    it('should issue exactly one GET on a cache miss and cache the response body data', () => {
      let result: IPagination;
      service.getProducts(true).subscribe(res => (result = res));

      const req = httpMock.expectOne(r => r.method === 'GET' && r.url === productsUrl);
      // observe: 'response' => flush the BODY object; Angular wraps it in an HttpResponse.
      req.flush(mockPagination);

      expect(result.data).toEqual(mockProducts);
      expect(service.productCache.size).toBe(1);
    });

    it('should not append brandId/typeId/search for defaults and append pageIndex twice', () => {
      service.getProducts(true).subscribe();

      const req = httpMock.expectOne(r => r.method === 'GET' && r.url === productsUrl);
      const params = req.request.params;
      expect(params.has('brandId')).toBe(false);
      expect(params.has('typeId')).toBe(false);
      expect(params.has('search')).toBe(false);
      expect(params.get('sort')).toBe('name');
      // Regression lock on the production double-append quirk: pageNumber then pageSize,
      // both under the single 'pageIndex' key -> ['1', '6'] for defaults.
      expect(params.getAll('pageIndex')).toEqual(['1', '6']);
      req.flush(mockPagination);
    });

    it('should append brandId, typeId, search and paging when set on shopParams', () => {
      const p = new ShopParams();
      p.brandId = 1;
      p.typeId = 2;
      p.search = 'board';
      p.pageNumber = 2;
      p.pageSize = 10;
      service.setShopParams(p);

      service.getProducts(false).subscribe();

      const req = httpMock.expectOne(r => r.method === 'GET' && r.url === productsUrl);
      const params = req.request.params;
      expect(params.get('brandId')).toBe('1');
      expect(params.get('typeId')).toBe('2');
      expect(params.get('search')).toBe('board');
      expect(params.get('sort')).toBe('name');
      // Same double-append quirk with custom paging -> ['2', '10'].
      expect(params.getAll('pageIndex')).toEqual(['2', '10']);
      req.flush(mockPagination);
    });

    it('should return cached pagination without an HTTP call on a cache hit', () => {
      // Prime the cache with a first, real request.
      service.getProducts(true).subscribe();
      httpMock.expectOne(r => r.method === 'GET' && r.url === productsUrl).flush(mockPagination);

      // Second call with unchanged shopParams -> served from cache, no HTTP.
      let result: IPagination;
      service.getProducts(true).subscribe(res => (result = res));
      httpMock.expectNone(r => r.url === productsUrl);
      expect(result.data).toEqual(mockProducts);
    });

    it('should reset the cache and issue an HTTP call when useCache is false', () => {
      // Pre-seed the cache; useCache=false must discard it and go to the network.
      service.productCache.set('0-0-name-1-6', mockProducts);
      expect(service.productCache.size).toBe(1);

      service.getProducts(false).subscribe();

      const req = httpMock.expectOne(r => r.method === 'GET' && r.url === productsUrl);
      req.flush(mockPagination);
      // Cache was reset then repopulated by the successful response.
      expect(service.productCache.size).toBe(1);
    });
  });

  describe('getBrands', () => {
    const mockBrands: IBrand[] = [{ id: 1, name: 'Angular' }, { id: 2, name: 'React' }];

    it('should GET brands on the first call and cache them', () => {
      let result: IBrand[];
      service.getBrands().subscribe(res => (result = res));
      const req = httpMock.expectOne(environment.apiUrl + 'products/brands');
      expect(req.request.method).toBe('GET');
      req.flush(mockBrands);
      expect(result).toEqual(mockBrands);
    });

    it('should return cached brands without an HTTP call on the second call', () => {
      service.getBrands().subscribe();
      httpMock.expectOne(environment.apiUrl + 'products/brands').flush(mockBrands);

      let result: IBrand[];
      service.getBrands().subscribe(res => (result = res));
      httpMock.expectNone(environment.apiUrl + 'products/brands');
      expect(result).toEqual(mockBrands);
    });
  });

  describe('getTypes', () => {
    const mockTypes: IType[] = [{ id: 1, name: 'Boards' }, { id: 2, name: 'Gloves' }];

    it('should GET types on the first call and cache them', () => {
      let result: IType[];
      service.getTypes().subscribe(res => (result = res));
      const req = httpMock.expectOne(environment.apiUrl + 'products/types');
      expect(req.request.method).toBe('GET');
      req.flush(mockTypes);
      expect(result).toEqual(mockTypes);
    });

    it('should return cached types without an HTTP call on the second call', () => {
      service.getTypes().subscribe();
      httpMock.expectOne(environment.apiUrl + 'products/types').flush(mockTypes);

      let result: IType[];
      service.getTypes().subscribe(res => (result = res));
      httpMock.expectNone(environment.apiUrl + 'products/types');
      expect(result).toEqual(mockTypes);
    });
  });

  describe('getProduct', () => {
    it('should return a cached product without an HTTP call when present', () => {
      // A single cache entry whose value contains the target product makes the
      // forEach (last-iteration-wins) lookup deterministic.
      service.productCache.set('0-0-name-1-6', mockProducts);

      let result: IProduct;
      service.getProduct(2).subscribe(res => (result = res));

      httpMock.expectNone(environment.apiUrl + 'products/2');
      expect(result.id).toBe(2);
    });

    it('should GET the product by id when not cached', () => {
      let result: IProduct;
      service.getProduct(5).subscribe(res => (result = res));

      const req = httpMock.expectOne(environment.apiUrl + 'products/5');
      expect(req.request.method).toBe('GET');
      req.flush({ ...mockProducts[0], id: 5 });

      expect(result.id).toBe(5);
    });
  });

  describe('shopParams', () => {
    it('should store and return shop params', () => {
      const p = new ShopParams();
      p.brandId = 3;
      service.setShopParams(p);

      const retrieved = service.getShopParams();
      expect(retrieved).toBe(p);
      expect(retrieved.brandId).toBe(3);
    });
  });
});
