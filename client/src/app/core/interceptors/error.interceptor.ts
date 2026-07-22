import { Injectable } from '@angular/core';
import {
  HttpRequest,
  HttpHandler,
  HttpEvent,
  HttpInterceptor
} from '@angular/common/http';
import {Observable, throwError} from 'rxjs';
import {NavigationExtras, Router} from "@angular/router";
import {catchError, delay} from 'rxjs/operators';
import {ToastrService} from 'ngx-toastr';

@Injectable()
export class ErrorInterceptor implements HttpInterceptor {

  constructor(private router: Router, private toastr:ToastrService) {}

  intercept(request: HttpRequest<any>, next: HttpHandler): Observable<HttpEvent<any>> {
    return next.handle(request).pipe(
      delay(100),
      catchError(error =>
      {
        if (error) {
          if (error.status === 400) {
            if (error.error.errors) {
              throw error.error;
            }else {
              this.toastr.error(error.error.message,error.error.statusCode);
            }
          }
          if (error.status === 401) {
            this.toastr.error(error.error.message,error.error.statusCode);
          }
          // Real-Time Inventory & Flash-Sale System: the basket reserve path
          // (BasketController.UpdateBasket -> IInventoryService) returns HTTP 409 Conflict
          // when the requested quantity exceeds available stock. Surface the server's
          // ApiResponse envelope message to the shopper via a toast (with a safe fallback
          // when the response body is absent or shaped unexpectedly) instead of silently
          // swallowing it. The error is still re-thrown below so BasketService.setBasket()'s
          // error handler can revert the optimistic (by-reference) quantity mutation to
          // server truth, preventing a phantom basket quantity.
          if (error.status === 409) {
            const conflictMessage = (error.error && error.error.message)
              ? error.error.message
              : 'Insufficient stock - your basket was not updated.';
            const conflictTitle = (error.error && error.error.statusCode)
              ? error.error.statusCode
              : error.status;
            this.toastr.error(conflictMessage, conflictTitle);
          }
          if (error.status === 404) {
            this.router.navigateByUrl('/not-found');
          }
          if (error.status === 500) {
            const navigationExtras: NavigationExtras = {state: {error:error.error}};
            this.router.navigateByUrl('/server-error',navigationExtras);
          }
        }
          return throwError(error);
      })
    );
  }
}
