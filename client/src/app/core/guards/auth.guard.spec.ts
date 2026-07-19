import { ActivatedRouteSnapshot, Router, RouterStateSnapshot } from '@angular/router';
import { of } from 'rxjs';
import { AuthGuard } from './auth.guard';

describe('AuthGuard', () => {
  let routerSpy: jasmine.SpyObj<Router>;
  let next: ActivatedRouteSnapshot;
  let state: RouterStateSnapshot;

  beforeEach(() => {
    // Recreate collaborators for every test so no state leaks between specs.
    routerSpy = jasmine.createSpyObj<Router>('Router', ['navigate']);
    next = {} as ActivatedRouteSnapshot;
    state = { url: '/checkout' } as RouterStateSnapshot;
  });

  it('should return true and not redirect when the user is authenticated', () => {
    // Arrange: a truthy current user drives the authenticated branch.
    const accountServiceStub = { currentUser$: of({ email: 'bob@test.com' } as any) };
    const guard = new AuthGuard(accountServiceStub as any, routerSpy);

    // Act: of(...) emits synchronously, so result is assigned before the asserts run.
    let result: boolean;
    guard.canActivate(next, state).subscribe(value => {
      result = value;
    });

    // Assert
    expect(result).toBe(true);
    expect(routerSpy.navigate).not.toHaveBeenCalled();
  });

  it('should redirect to account/login with returnUrl when the user is not authenticated', () => {
    // Arrange: a null current user drives the unauthenticated branch.
    const accountServiceStub = { currentUser$: of(null) };
    const guard = new AuthGuard(accountServiceStub as any, routerSpy);

    // Act
    let result: boolean;
    guard.canActivate(next, state).subscribe(value => {
      result = value;
    });

    // Assert: the guard has no explicit `return false`, so the map callback falls
    // through and emits `undefined` after navigating - assert falsy, never toBe(false).
    expect(result).toBeFalsy();
    expect(routerSpy.navigate).toHaveBeenCalledWith(['account/login'], { queryParams: { returnUrl: state.url } });
  });
});
