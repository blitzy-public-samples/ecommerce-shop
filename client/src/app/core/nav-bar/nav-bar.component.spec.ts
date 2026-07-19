import { TestBed, ComponentFixture } from '@angular/core/testing';
import { NO_ERRORS_SCHEMA } from '@angular/core';
import { RouterTestingModule } from '@angular/router/testing';
import { of } from 'rxjs';

import { NavBarComponent } from './nav-bar.component';
import { BasketService } from '../../basket/basket.service';
import { AccountService } from '../../account/account.service';

describe('NavBarComponent', () => {
  let component: NavBarComponent;
  let fixture: ComponentFixture<NavBarComponent>;
  let basketServiceStub: { basket$: any };
  let accountServiceStub: { currentUser$: any; logout: jasmine.Spy };

  beforeEach(async () => {
    // Arrange: stub both collaborators via useValue so their real HttpClient /
    // Router dependencies are never constructed. Both observables emit null so
    // the template's null-guarded blocks are skipped and detectChanges() cannot
    // dereference basket.items or currentUser.displayName.
    basketServiceStub = { basket$: of(null) };
    accountServiceStub = { currentUser$: of(null), logout: jasmine.createSpy('logout') };

    await TestBed.configureTestingModule({
      declarations: [
        NavBarComponent
      ],
      imports: [
        // Satisfies the template's routerLink / routerLinkActive directives.
        RouterTestingModule
      ],
      schemas: [
        // Neutralizes the unknown ngx-bootstrap dropdown directive trio
        // (dropdown / dropdownToggle / *dropdownMenu) used by the template.
        NO_ERRORS_SCHEMA
      ],
      providers: [
        { provide: BasketService, useValue: basketServiceStub },
        { provide: AccountService, useValue: accountServiceStub }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(NavBarComponent);
    component = fixture.componentInstance;
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('should wire basket$ and currentUser$ from the services on init', () => {
    // Act: detectChanges() triggers ngOnInit, which copies the service
    // observables onto the component.
    fixture.detectChanges();

    // Assert: reference equality proves the exact wiring performed in ngOnInit.
    expect(component.basket$).toBe(basketServiceStub.basket$);
    expect(component.currentUser$).toBe(accountServiceStub.currentUser$);
  });

  it('should delegate logout to AccountService.logout', () => {
    // Act
    component.logout();

    // Assert: logout() forwards to the injected AccountService.
    expect(accountServiceStub.logout).toHaveBeenCalled();
  });
});
