import { TestBed, ComponentFixture } from '@angular/core/testing';
import { RouterTestingModule } from '@angular/router/testing';
import { of } from 'rxjs';

import { AppComponent } from './app.component';
import { StockService } from './core/services/stock.service';
import { BasketService } from './basket/basket.service';
import { AccountService } from './account/account.service';

/**
 * Unit tests for the root AppComponent.
 *
 * AppComponent now injects THREE collaborators — BasketService, AccountService and
 * (new, Real-Time Inventory feature) StockService — and its ngOnInit starts the
 * SignalR stock hub connection before rehydrating the basket and current user:
 *
 *   ngOnInit() {
 *     this.stockService.startConnection();
 *     this.loadBasket();
 *     this.loadCurrentUser();
 *   }
 *
 * The suite follows the nav-bar.component.spec.ts convention: every collaborator is
 * replaced with a { provide, useValue } stub exposing ONLY the members ngOnInit
 * touches, so the real SignalR socket, HttpClient and Router are never constructed.
 * Observable-returning members emit `of(null)` so the component's `.subscribe(...)`
 * calls are safe. NO_ERRORS_SCHEMA is deliberately NOT used.
 *
 * The production app.component.html renders shell components that are intentionally
 * NOT declared in this focused root spec (<ngx-spinner>, <app-nav-bar>,
 * <app-section-header>). Rather than pull those unrelated modules in — or mask them
 * with NO_ERRORS_SCHEMA — the template is overridden (a test-only, in-memory
 * override that never touches app.component.html) with a minimal
 * `<router-outlet>`-only template satisfied by RouterTestingModule. This keeps the
 * "real providers, no schema-masking" convention while producing a completely clean
 * run with no unknown-element rendering.
 *
 * The tests then exercise the component CLASS: the fixture is created and
 * `ngOnInit()` is invoked directly (never `detectChanges()`), and assertions target
 * the component instance and the collaborator spies, never the DOM.
 */
describe('AppComponent', () => {
  let component: AppComponent;
  let fixture: ComponentFixture<AppComponent>;
  let stockServiceStub: { startConnection: jasmine.Spy };
  let basketServiceStub: { getBasket: jasmine.Spy };
  let accountServiceStub: { loadCurrentUser: jasmine.Spy };

  beforeEach(async () => {
    // Arrange: stub the three injected collaborators via useValue so their real
    // dependencies (SignalR HubConnection, HttpClient, Router) are never built.
    //   - startConnection: a bare spy — ngOnInit calls it fire-and-forget.
    //   - getBasket / loadCurrentUser: return of(null) so any .subscribe(...) the
    //     component performs completes immediately without a real HTTP round-trip.
    stockServiceStub = { startConnection: jasmine.createSpy('startConnection') };
    basketServiceStub = {
      getBasket: jasmine.createSpy('getBasket').and.returnValue(of(null))
    };
    accountServiceStub = {
      loadCurrentUser: jasmine.createSpy('loadCurrentUser').and.returnValue(of(null))
    };

    // Clear persisted state so loadBasket() deterministically takes its no-op branch
    // (it only calls getBasket when a 'basket_id' is present) and loadCurrentUser()
    // resolves a null token — independent of any state left by earlier suites.
    localStorage.clear();

    await TestBed.configureTestingModule({
      // RouterTestingModule satisfies the <router-outlet> in the overridden template.
      imports: [
        RouterTestingModule
      ],
      declarations: [
        AppComponent
      ],
      providers: [
        { provide: StockService, useValue: stockServiceStub },
        { provide: BasketService, useValue: basketServiceStub },
        { provide: AccountService, useValue: accountServiceStub }
      ]
    })
      // Replace the shell template (which renders undeclared <ngx-spinner>,
      // <app-nav-bar> and <app-section-header>) with a minimal router-outlet-only
      // template. This is an in-memory test override — app.component.html is NOT
      // modified — that avoids unknown-element errors without NO_ERRORS_SCHEMA and
      // without declaring unrelated shell components.
      .overrideComponent(AppComponent, {
        set: { template: '<router-outlet></router-outlet>' }
      })
      .compileComponents();

    fixture = TestBed.createComponent(AppComponent);
    component = fixture.componentInstance;
  });

  it('should create', () => {
    // The component constructs even with three injected services because all three
    // are resolved from the provided stubs.
    expect(component).toBeTruthy();
  });

  it(`should have as title 'Web Store'`, () => {
    // Locks the real title (the stale default CLI spec asserted 'client').
    expect(component.title).toEqual('Web Store');
  });

  it('should start the SignalR stock hub connection on init', () => {
    // Act: run lifecycle init directly (no detectChanges — the shell child
    // components are not declared in this TestBed).
    component.ngOnInit();

    // Assert: the new hub-startup behavior — ngOnInit opens the stock socket exactly
    // once so live stock is available for the whole session.
    expect(stockServiceStub.startConnection).toHaveBeenCalled();
    expect(stockServiceStub.startConnection).toHaveBeenCalledTimes(1);
  });

  it('should load the current user on init', () => {
    // Act
    component.ngOnInit();

    // Assert: existing bootstrap behavior is preserved alongside the new hub start.
    expect(accountServiceStub.loadCurrentUser).toHaveBeenCalled();
  });

  it('rehydrates the basket from a persisted basket_id on init (QA A1 coverage)', () => {
    // Arrange: a basket_id persisted from a prior session drives loadBasket() down
    // its rehydration branch (the beforeEach clears localStorage, so the other
    // specs only exercise the no-op branch — this closes that coverage gap).
    localStorage.setItem('basket_id', 'persisted-basket-1');

    try {
      // Act
      component.ngOnInit();

      // Assert: the persisted basket is rehydrated with its exact id, and the new
      // hub-startup behavior remains a single call alongside it.
      expect(basketServiceStub.getBasket).toHaveBeenCalledWith('persisted-basket-1');
      expect(stockServiceStub.startConnection).toHaveBeenCalledTimes(1);
    } finally {
      // Do not leak persisted state into later specs / spec files.
      localStorage.removeItem('basket_id');
    }
  });
});
