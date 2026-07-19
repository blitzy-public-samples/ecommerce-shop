import { TestBed } from '@angular/core/testing';
import { HttpClientTestingModule, HttpTestingController } from '@angular/common/http/testing';
import { RouterTestingModule } from '@angular/router/testing';
import { Router } from '@angular/router';

import { AccountService } from './account.service';
import { environment } from '../../environments/environment';
import { IUser } from '../shared/models/user';
import { IAddress } from '../shared/models/address';

/**
 * Unit tests for AccountService.
 *
 * The service is exercised exactly as written (no production changes). All HTTP
 * traffic is intercepted with Angular's HttpTestingController so the suite is
 * hermetic (no real network I/O) and deterministic.
 *
 * Critical behavioural nuance: the `map(...)` callbacks inside `login`,
 * `register` and `loadCurrentUser` return `void`, so the observables those
 * methods return emit `undefined` rather than the user. The token-persistence
 * and state-update side effects therefore only run once the returned observable
 * is subscribed. Every test that drives one of those methods subscribes BEFORE
 * flushing the mocked response, and asserts the resulting user through the
 * `currentUser$` stream rather than the method's return value.
 */
describe('AccountService', () => {
  let service: AccountService;
  let httpMock: HttpTestingController;
  let router: Router;

  // Shared fixtures satisfying the IUser / IAddress contracts.
  const mockUser: IUser = {
    email: 'bob@test.com',
    displayName: 'bob',
    token: 'fake-jwt-token'
  };

  const mockAddress: IAddress = {
    id: 1,
    firstName: 'Bob',
    lastName: 'Bobbity',
    street: '10 The Street',
    city: 'New York',
    state: 'NY',
    zipCode: '90210'
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HttpClientTestingModule, RouterTestingModule],
      providers: [AccountService]
    });

    service = TestBed.inject(AccountService);
    httpMock = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);

    // Replace localStorage mutators with recording no-op stubs. A plain spyOn
    // already swaps in a stub that records calls without writing to real
    // storage, so each test observes fresh, isolated spy state.
    spyOn(localStorage, 'setItem');
    spyOn(localStorage, 'removeItem');
  });

  afterEach(() => {
    // Fail the test if any unexpected or outstanding HTTP request remains.
    httpMock.verify();
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });

  // ---------------------------------------------------------------------------
  // login
  // ---------------------------------------------------------------------------
  it('should POST the credentials to account/login', () => {
    const values = { email: 'bob@test.com', password: 'Pa$$w0rd' };

    service.login(values).subscribe();

    const req = httpMock.expectOne(environment.apiUrl + 'account/login');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(values);
    req.flush(mockUser);
  });

  it('should persist the token and emit the user on currentUser$ after a successful login', () => {
    let emittedUser: IUser;
    service.currentUser$.subscribe(user => (emittedUser = user));

    service.login({ email: 'bob@test.com', password: 'Pa$$w0rd' }).subscribe();

    const req = httpMock.expectOne(environment.apiUrl + 'account/login');
    req.flush(mockUser);

    expect(localStorage.setItem).toHaveBeenCalledWith('token', mockUser.token);
    expect(emittedUser).toEqual(mockUser);
  });

  // ---------------------------------------------------------------------------
  // register
  // ---------------------------------------------------------------------------
  it('should POST the registration payload to account/register', () => {
    const values = { displayName: 'bob', email: 'bob@test.com', password: 'Pa$$w0rd' };

    service.register(values).subscribe();

    const req = httpMock.expectOne(environment.apiUrl + 'account/register');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(values);
    req.flush(mockUser);
  });

  it('should persist the token and emit the user on currentUser$ after a successful registration', () => {
    let emittedUser: IUser;
    service.currentUser$.subscribe(user => (emittedUser = user));

    service.register({ displayName: 'bob', email: 'bob@test.com', password: 'Pa$$w0rd' }).subscribe();

    const req = httpMock.expectOne(environment.apiUrl + 'account/register');
    req.flush(mockUser);

    expect(localStorage.setItem).toHaveBeenCalledWith('token', mockUser.token);
    expect(emittedUser).toEqual(mockUser);
  });

  // ---------------------------------------------------------------------------
  // loadCurrentUser
  // ---------------------------------------------------------------------------
  it('should emit null and issue no HTTP request when the token is null', () => {
    let emittedUser: IUser = mockUser; // non-null sentinel; expected to become null
    service.currentUser$.subscribe(user => (emittedUser = user));

    let result: any = mockUser; // non-null sentinel; expected to become null
    service.loadCurrentUser(null).subscribe(value => (result = value));

    httpMock.expectNone(environment.apiUrl + 'account');
    expect(result).toBeNull();
    expect(emittedUser).toBeNull();
  });

  it('should GET account with a bearer header and update state when a token is supplied', () => {
    let emittedUser: IUser;
    service.currentUser$.subscribe(user => (emittedUser = user));

    const token = 'existing-token';
    service.loadCurrentUser(token).subscribe();

    const req = httpMock.expectOne(environment.apiUrl + 'account');
    expect(req.request.method).toBe('GET');
    expect(req.request.headers.get('Authorization')).toBe('Bearer ' + token);
    req.flush(mockUser);

    expect(localStorage.setItem).toHaveBeenCalledWith('token', mockUser.token);
    expect(emittedUser).toEqual(mockUser);
  });

  // ---------------------------------------------------------------------------
  // logout
  // ---------------------------------------------------------------------------
  it('should clear the token, emit null and navigate home on logout', () => {
    const navSpy = spyOn(router, 'navigateByUrl');
    let emittedUser: IUser = mockUser; // non-null sentinel; expected to become null
    service.currentUser$.subscribe(user => (emittedUser = user));

    service.logout();

    expect(localStorage.removeItem).toHaveBeenCalledWith('token');
    expect(emittedUser).toBeNull();
    expect(navSpy).toHaveBeenCalledWith('/');
  });

  // ---------------------------------------------------------------------------
  // checkEmailExists
  // ---------------------------------------------------------------------------
  it('should GET account/emailexists with the email query parameter', () => {
    const email = 'test@test.com';

    service.checkEmailExists(email).subscribe();

    const req = httpMock.expectOne(environment.apiUrl + 'account/emailexists?email=' + email);
    expect(req.request.method).toBe('GET');
    req.flush(true);
  });

  // ---------------------------------------------------------------------------
  // getUserAddress
  // ---------------------------------------------------------------------------
  it('should GET the user address from account/address', () => {
    service.getUserAddress().subscribe(address => expect(address).toEqual(mockAddress));

    const req = httpMock.expectOne(environment.apiUrl + 'account/address');
    expect(req.request.method).toBe('GET');
    req.flush(mockAddress);
  });

  // ---------------------------------------------------------------------------
  // updateUserAddress
  // ---------------------------------------------------------------------------
  it('should PUT the updated address to account/address', () => {
    service.updateUserAddress(mockAddress).subscribe(address => expect(address).toEqual(mockAddress));

    const req = httpMock.expectOne(environment.apiUrl + 'account/address');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual(mockAddress);
    req.flush(mockAddress);
  });

  // ---------------------------------------------------------------------------
  // guard branch: falsy response body must not mutate client state
  // ---------------------------------------------------------------------------
  it('should not persist a token or emit when the login response body is null', () => {
    let emitted = false;
    service.currentUser$.subscribe(() => (emitted = true));

    service.login({ email: 'bob@test.com', password: 'Pa$$w0rd' }).subscribe();

    const req = httpMock.expectOne(environment.apiUrl + 'account/login');
    req.flush(null);

    expect(localStorage.setItem).not.toHaveBeenCalled();
    expect(emitted).toBe(false);
  });
});
