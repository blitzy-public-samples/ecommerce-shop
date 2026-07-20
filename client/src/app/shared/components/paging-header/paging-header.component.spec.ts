import { CommonModule } from '@angular/common';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { PagingHeaderComponent } from './paging-header.component';

describe('PagingHeaderComponent', () => {
  let component: PagingHeaderComponent;
  let fixture: ComponentFixture<PagingHeaderComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      // Real template compilation (no NO_ERRORS_SCHEMA masking): CommonModule
      // supplies *ngIf and interpolation, the component's only template deps.
      imports: [CommonModule],
      declarations: [PagingHeaderComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(PagingHeaderComponent);
    component = fixture.componentInstance;
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('should render the showing-range text for a full first page', () => {
    component.pageNumber = 1;
    component.pageSize = 10;
    component.totalCount = 18;

    fixture.detectChanges();

    const text = fixture.nativeElement.textContent;
    expect(text).toContain('1 -');
    expect(text).toContain('10');
    expect(text).toContain('of');
    expect(text).toContain('18');
    expect(text).toContain('results');
  });

  it('should clamp the upper bound to totalCount on the last partial page', () => {
    component.pageNumber = 2;
    component.pageSize = 10;
    component.totalCount = 18;

    fixture.detectChanges();

    const text = fixture.nativeElement.textContent;
    expect(text).toContain('11 -');
    expect(text).toContain('18');
  });

  it('should render the empty-state message when totalCount is 0', () => {
    component.totalCount = 0;

    fixture.detectChanges();

    const text = fixture.nativeElement.textContent;
    expect(text).toContain('There are');
    expect(text).toContain('0 results for this filter');
  });
});
