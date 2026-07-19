import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { HTTP_INTERCEPTORS, HttpClient } from '@angular/common/http';
import {
  HttpClientTestingModule,
  HttpTestingController
} from '@angular/common/http/testing';
import { Router } from '@angular/router';
import { ToastrService } from 'ngx-toastr';
import { ErrorInterceptor } from './error.interceptor';

describe('ErrorInterceptor', () => {
  let httpClient: HttpClient;
  let httpMock: HttpTestingController;
  let routerSpy: jasmine.SpyObj<Router>;
  let toastrSpy: jasmine.SpyObj<ToastrService>;

  beforeEach(() => {
    routerSpy = jasmine.createSpyObj('Router', ['navigateByUrl']);
    toastrSpy = jasmine.createSpyObj('ToastrService', ['error']);

    TestBed.configureTestingModule({
      imports: [HttpClientTestingModule],
      providers: [
        { provide: HTTP_INTERCEPTORS, useClass: ErrorInterceptor, multi: true },
        { provide: Router, useValue: routerSpy },
        { provide: ToastrService, useValue: toastrSpy }
      ]
    });

    httpClient = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('should re-throw the validation payload for a 400 response that contains errors', fakeAsync(() => {
    const body = { errors: { Email: ['Required'] }, message: 'Validation errors', statusCode: 400 };
    let thrown: any;

    httpClient.get('/test').subscribe({
      next: () => {},
      error: err => (thrown = err)
    });

    httpMock.expectOne('/test').flush(body, { status: 400, statusText: 'Bad Request' });
    tick(100);

    expect(thrown).toEqual(body);
    expect(toastrSpy.error).not.toHaveBeenCalled();
  }));

  it('should show a toast for a 400 response without errors', fakeAsync(() => {
    const body = { message: 'bad request', statusCode: 400 };

    httpClient.get('/test').subscribe({
      next: () => {},
      error: () => {}
    });

    httpMock.expectOne('/test').flush(body, { status: 400, statusText: 'Bad Request' });
    tick(100);

    // ToastrService.error types `title` as string, but the interceptor forwards the
    // numeric statusCode; cast the spy to bypass the compile-time param-type check
    // while asserting the exact runtime call (message + numeric statusCode).
    expect(toastrSpy.error as any).toHaveBeenCalledWith('bad request', 400);
  }));

  it('should show a toast for a 401 response', fakeAsync(() => {
    const body = { message: 'unauthorized', statusCode: 401 };

    httpClient.get('/test').subscribe({
      next: () => {},
      error: () => {}
    });

    httpMock.expectOne('/test').flush(body, { status: 401, statusText: 'Unauthorized' });
    tick(100);

    // ToastrService.error types `title` as string, but the interceptor forwards the
    // numeric statusCode; cast the spy to bypass the compile-time param-type check
    // while asserting the exact runtime call (message + numeric statusCode).
    expect(toastrSpy.error as any).toHaveBeenCalledWith('unauthorized', 401);
  }));

  it('should navigate to /not-found for a 404 response', fakeAsync(() => {
    const body = { message: 'not found', statusCode: 404 };

    httpClient.get('/test').subscribe({
      next: () => {},
      error: () => {}
    });

    httpMock.expectOne('/test').flush(body, { status: 404, statusText: 'Not Found' });
    tick(100);

    expect(routerSpy.navigateByUrl).toHaveBeenCalledWith('/not-found');
  }));

  it('should navigate to /server-error with navigation state for a 500 response', fakeAsync(() => {
    const body = { message: 'server error', statusCode: 500 };

    httpClient.get('/test').subscribe({
      next: () => {},
      error: () => {}
    });

    httpMock.expectOne('/test').flush(body, { status: 500, statusText: 'Server Error' });
    tick(100);

    expect(routerSpy.navigateByUrl).toHaveBeenCalledWith('/server-error', { state: { error: body } });
  }));

  it('should pass through a successful response unchanged', fakeAsync(() => {
    const body = { id: 1, name: 'product' };
    let response: any;

    httpClient.get('/test').subscribe({
      next: res => (response = res),
      error: () => {}
    });

    httpMock.expectOne('/test').flush(body);
    tick(100);

    expect(response).toEqual(body);
    expect(routerSpy.navigateByUrl).not.toHaveBeenCalled();
    expect(toastrSpy.error).not.toHaveBeenCalled();
  }));
});
