import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FormsModule } from '@angular/forms';
import { PaginationModule } from 'ngx-bootstrap/pagination';

import { PagerComponent } from './pager.component';

describe('PagerComponent', () => {
  let component: PagerComponent;
  let fixture: ComponentFixture<PagerComponent>;

  beforeEach(async () => {
    // Real template compilation (no NO_ERRORS_SCHEMA masking) using the exact
    // dependencies the production template renders: the ngx-bootstrap <pagination>
    // component (same module SharedModule declares via forRoot()) and FormsModule
    // for its [ngModel] two-way binding.
    await TestBed.configureTestingModule({
      imports: [FormsModule, PaginationModule.forRoot()],
      declarations: [PagerComponent],
    }).compileComponents();
  });

  beforeEach(() => {
    fixture = TestBed.createComponent(PagerComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('should emit event.page through pageChanged when onPagerChanged is called', () => {
    let emitted: number | undefined;
    component.pageChanged.subscribe((value: number) => (emitted = value));

    component.onPagerChanged({ page: 3 });

    expect(emitted).toBe(3);
  });

  it('should call pageChanged.emit with the selected page number', () => {
    const emitSpy = spyOn(component.pageChanged, 'emit');

    component.onPagerChanged({ page: 5 });

    expect(emitSpy).toHaveBeenCalledWith(5);
  });

  it('should render the pagination widget when inputs are set', () => {
    component.totalCount = 50;
    component.pageSize = 10;
    component.pageNumber = 2;

    fixture.detectChanges();

    expect(component.totalCount).toBe(50);
    expect(component.pageSize).toBe(10);
    expect(component.pageNumber).toBe(2);
  });
});
