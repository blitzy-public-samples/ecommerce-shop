import { Component, NO_ERRORS_SCHEMA } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { RouterTestingModule } from '@angular/router/testing';
import { of } from 'rxjs';
import { AppComponent } from './app.component';
import { BasketService } from './basket/basket.service';
import { AccountService } from './account/account.service';

// -----------------------------------------------------------------------------
// AppComponent spec — repaired to validate the REAL root shell component.
//
// The Angular-CLI-generated scaffold spec that previously lived here asserted the
// original scaffold defaults (title === 'client' and a `.content span` reading
// "client app is running!") and configured the TestBed with only RouterTestingModule.
// The production AppComponent diverged from that scaffold long ago: it sets
// `title = 'Web Store'`, injects BasketService/AccountService (BasketService depends on
// HttpClient), and renders <ngx-spinner>/<app-nav-bar>/<app-section-header>/<router-outlet>.
// Consequently the scaffold assertions could never pass (component creation threw
// NullInjectorError: BasketService -> HttpClient, and the title/markup no longer matched).
//
// This spec is rewritten to test the component as it actually exists, following the
// repository's established testing convention (see nav-bar/section-header specs): the
// real collaborators are replaced with lightweight useValue stubs so their HttpClient /
// Router dependencies are never constructed, and the app's own child elements are
// supplied as selector-matching stub components so the template compiles without masking.
// -----------------------------------------------------------------------------

// Selector-matching stub components for the app's own child widgets rendered by the
// AppComponent template. Declaring them lets the real template compile so genuine
// binding errors on AppComponent's own markup are still surfaced.
@Component({ selector: 'app-nav-bar', template: '' })
class NavBarStubComponent {}

@Component({ selector: 'app-section-header', template: '' })
class SectionHeaderStubComponent {}

describe('AppComponent', () => {
  // BasketService stub: AppComponent.loadBasket() only calls getBasket() when a
  // basket_id exists in localStorage; the spy returns an observable so any invocation
  // is harmless and no real HttpClient is required.
  let basketServiceStub: { getBasket: jasmine.Spy };
  // AccountService stub: AppComponent.loadCurrentUser() calls loadCurrentUser(token);
  // the spy returns of(null) mirroring the real null-token fast-path.
  let accountServiceStub: { loadCurrentUser: jasmine.Spy };

  beforeEach(async () => {
    basketServiceStub = {
      getBasket: jasmine.createSpy('getBasket').and.returnValue(of(null))
    };
    accountServiceStub = {
      loadCurrentUser: jasmine.createSpy('loadCurrentUser').and.returnValue(of(null))
    };

    await TestBed.configureTestingModule({
      imports: [
        // RouterTestingModule supplies the <router-outlet> the template renders and the
        // Router that AccountService would otherwise require.
        RouterTestingModule
      ],
      declarations: [
        AppComponent,
        NavBarStubComponent,
        SectionHeaderStubComponent
      ],
      providers: [
        { provide: BasketService, useValue: basketServiceStub },
        { provide: AccountService, useValue: accountServiceStub }
      ],
      // The template also renders the third-party <ngx-spinner> element from the
      // ngx-spinner package; NO_ERRORS_SCHEMA is scoped here solely to tolerate that
      // single external element without importing the whole NgxSpinnerModule. The app's
      // own <app-nav-bar>/<app-section-header> are real stub declarations above, so their
      // binding errors are NOT masked.
      schemas: [NO_ERRORS_SCHEMA]
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(AppComponent);
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });

  it(`should have as title 'Web Store'`, () => {
    const fixture = TestBed.createComponent(AppComponent);
    const app = fixture.componentInstance;
    expect(app.title).toEqual('Web Store');
  });

  it('should render the application shell (nav bar and router outlet)', () => {
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();
    const compiled: HTMLElement = fixture.nativeElement;
    // The real shell renders the nav bar and a router outlet rather than the CLI scaffold.
    expect(compiled.querySelector('app-nav-bar')).toBeTruthy();
    expect(compiled.querySelector('router-outlet')).toBeTruthy();
  });

  it('should load the current user and basket on init', () => {
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();
    // ngOnInit wires the two bootstrap loads; loadCurrentUser always runs, and getBasket
    // runs only when a basket_id is present in localStorage.
    expect(accountServiceStub.loadCurrentUser).toHaveBeenCalled();
    const basketId = localStorage.getItem('basket_id');
    if (basketId) {
      expect(basketServiceStub.getBasket).toHaveBeenCalledWith(basketId);
    }
  });
});
