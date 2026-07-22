import {HttpEvent, HttpHandler, HttpInterceptor, HttpRequest} from '@angular/common/http';
import {Injectable} from '@angular/core';
import {BusyService} from '../services/busy.service';
import {Observable} from 'rxjs';
import {delay, finalize} from 'rxjs/operators';

@Injectable()
export class LoadingInterceptor implements HttpInterceptor {
  constructor(private busyService: BusyService) {
  }
  intercept(req: HttpRequest<any>, next: HttpHandler): Observable<HttpEvent<any>> {
    if (req.method === 'POST' && req.url.includes('orders')) {
      return next.handle(req);
    }
    if (req.method === 'DELETE') {
      return next.handle(req);
    }
    if (req.url.includes('emailexists')) {
      return next.handle(req);
    }
    // QA finding F3 (MINOR, Visual/Performance): the product-details M14 fallback poll issues
    // GET /api/flash-sales/active every environment.pollInterval (5000ms) to reconcile live
    // sale/stock state. Without this exclusion the LoadingInterceptor raises the global
    // full-viewport busy spinner (ngx-spinner, z-index:99999) on every silent background poll -
    // imperceptible on a fast link but a visibly flashing scrim on throttled networks (Fast/Slow 3G).
    // Excluding the active-sale poll keeps background price/stock refreshes silent while every
    // user-initiated request continues to show the spinner. Scoped to 'flash-sales/active' only, so
    // the deliberate POST /api/flash-sales scheduling action (URL '.../flash-sales', no '/active')
    // still shows the spinner as before.
    if (req.url.includes('flash-sales/active')) {
      return next.handle(req);
    }
    this.busyService.busy();
    return next.handle(req).pipe(
      finalize(() => {
        this.busyService.idle();
      })
    );
  }

}
