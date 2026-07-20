import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { BreadcrumbService } from 'xng-breadcrumb';
import { SectionHeaderComponent } from './section-header.component';

// Stub for the third-party <xng-breadcrumb> element the production template renders.
// Declaring it lets the real SectionHeaderComponent template compile WITHOUT
// NO_ERRORS_SCHEMA masking, so genuine binding errors on the component's own markup
// (the *ngIf / async / titlecase usage) are still caught.
@Component({ selector: 'xng-breadcrumb', template: '' })
class XngBreadcrumbStubComponent {}

describe('SectionHeaderComponent', () => {
  let component: SectionHeaderComponent;
  let fixture: ComponentFixture<SectionHeaderComponent>;
  const breadcrumbServiceStub = { breadcrumbs$: of([]) };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      // CommonModule supplies *ngIf, the async pipe and the titlecase pipe used by
      // the template; the stub above supplies the <xng-breadcrumb> element.
      imports: [
        CommonModule
      ],
      declarations: [
        SectionHeaderComponent,
        XngBreadcrumbStubComponent
      ],
      providers: [
        { provide: BreadcrumbService, useValue: breadcrumbServiceStub }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SectionHeaderComponent);
    component = fixture.componentInstance;
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('should assign breadcrumb$ from the BreadcrumbService on init', () => {
    fixture.detectChanges();
    expect(component.breadcrumb$).toBe(breadcrumbServiceStub.breadcrumbs$);
    component.breadcrumb$.subscribe(value => expect(value).toEqual([]));
  });
});
