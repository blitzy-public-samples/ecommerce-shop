import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
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
  getActiveFlashSales(): Observable<IFlashSale[]> {
    return this.http.get<IFlashSale[]>(this.baseUrl + 'flash-sales/active');
  }

  // Convenience: resolve the single active sale for a product (undefined if none) from the list.
  getActiveFlashSale(productId: number): Observable<IFlashSale> {
    return this.getActiveFlashSales().pipe(
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

  releaseReservation(id: number): Observable<any> {
    return this.http.delete(this.baseUrl + 'inventory/reserve/' + id);
  }
}
