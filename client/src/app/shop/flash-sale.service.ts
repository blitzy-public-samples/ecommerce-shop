import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import { environment } from '../../environments/environment';
import { IFlashSale } from '../shared/models/flash-sale';
import { IInventoryReservation } from '../shared/models/inventory';

// Real-Time Inventory & Flash Sale - REST client for the flash-sale + reservation endpoints.
// Follows the existing shop.service.ts / basket.service.ts conventions: root-provided,
// baseUrl = environment.apiUrl (already ends with 'api/'), URLs built by string concatenation.
// HTTP errors (409 INSUFFICIENT_STOCK / 409 RESERVATION_CONFLICT / 429 rate-limit) are NOT
// swallowed here - they propagate to the subscribing component. The global ErrorInterceptor
// (core/interceptors/error.interceptor.ts) rethrows those statuses without navigation/toast,
// so the caller receives them intact.
@Injectable({
  providedIn: 'root'
})
export class FlashSaleService {
  baseUrl = environment.apiUrl;

  constructor(private http: HttpClient) {
  }

  // Non-cached endpoint (intentionally NOT [Cached] server-side) so live price/stock stays fresh.
  // N1-fe: an optional productId narrows the read to a single product via the server-side
  // ?productId filter, so a product page no longer downloads every active sale in the catalog.
  // Called with no argument the full active list is returned (behaviour preserved for callers that
  // need it). An empty HttpParams serialises to no query string, so the bare URL is unchanged.
  getActiveFlashSales(productId?: number): Observable<IFlashSale[]> {
    let params = new HttpParams();
    if (productId != null) {
      params = params.set('productId', productId.toString());
    }
    return this.http.get<IFlashSale[]>(this.baseUrl + 'flash-sales/active', { params });
  }

  // Convenience: resolve the single active sale for a product (undefined if none).
  // N1-fe: pushes the productId to the server so only the relevant sale(s) are fetched; the
  // defensive .find still guards against a server that returns an unfiltered list.
  getActiveFlashSale(productId: number): Observable<IFlashSale> {
    return this.getActiveFlashSales(productId).pipe(
      map(sales => sales.find(sale => sale.productId === productId))
    );
  }

  // Session identity reuse: sessionId is the basket UUID persisted in localStorage['basket_id']
  // (set by basket.service.ts createBasket() as Basket.id = uuidv4()). No new identity concept.
  reserve(productId: number, quantity: number): Observable<IInventoryReservation> {
    return this.http.post<IInventoryReservation>(this.baseUrl + 'inventory/reserve', {
      productId,
      quantity,
      sessionId: localStorage.getItem('basket_id')
    });
  }

  // M6-fe: the backend DELETE /api/inventory/reserve/{id} performs an ownership check and REQUIRES
  // the owning sessionId as a query-string parameter ([FromQuery] string sessionId); without it the
  // controller fails fast with HTTP 400. We therefore forward the caller-supplied sessionId,
  // defaulting to the basket UUID in localStorage['basket_id'] — the SAME identity reserve() stamps
  // on the hold — so a shopper can release only a reservation it owns. Repeated owner-release of an
  // already-released hold is idempotent server-side (returns 204), so callers may safely retry.
  releaseReservation(id: number, sessionId: string = localStorage.getItem('basket_id') || ''): Observable<any> {
    const params = new HttpParams().set('sessionId', sessionId);
    return this.http.delete(this.baseUrl + 'inventory/reserve/' + id, { params });
  }
}
