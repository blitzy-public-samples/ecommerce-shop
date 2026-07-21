import { ComponentFixture, TestBed, fakeAsync, tick, discardPeriodicTasks } from '@angular/core/testing';
import { CommonModule } from '@angular/common';
import { SimpleChange } from '@angular/core';

import { CountdownTimerComponent } from './countdown-timer.component';

describe('CountdownTimerComponent', () => {
  let component: CountdownTimerComponent;
  let fixture: ComponentFixture<CountdownTimerComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [CommonModule],
      declarations: [CountdownTimerComponent]
    }).compileComponents();
  });

  beforeEach(() => {
    fixture = TestBed.createComponent(CountdownTimerComponent);
    component = fixture.componentInstance;
  });

  /**
   * Rebind `endAt` and drive ngOnChanges the way Angular would for a bound input change. Used to
   * exercise reuse of the component for successive sales (M01).
   */
  function setEndAt(value: string): void {
    const previous = component.endAt;
    component.endAt = value;
    component.ngOnChanges({ endAt: new SimpleChange(previous, value, previous === undefined) });
  }

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('renders a defined fallback for undefined endAt (m01)', fakeAsync(() => {
    setEndAt(undefined as unknown as string);
    expect(component.invalid).toBe(true);
    expect(component.remaining).toBe('--:--:--');
    // No periodic timer is scheduled for invalid input, so nothing to discard.
  }));

  it('renders a defined fallback for an unparsable endAt (m01)', fakeAsync(() => {
    setEndAt('not-a-real-date');
    expect(component.invalid).toBe(true);
    expect(component.remaining).toBe('--:--:--');
  }));

  it('uses ceiling seconds so it never shows 00:00:00 while time remains (m03)', fakeAsync(() => {
    // ~1500 ms in the future: floor would yield 00:00:01, ceiling yields 00:00:02.
    setEndAt(new Date(Date.now() + 1500).toISOString());
    tick(0); // timer emits immediately
    expect(component.invalid).toBe(false);
    expect(component.remaining).toBe('00:00:02');
    discardPeriodicTasks();
  }));

  it('emits expired exactly once and stops ticking at expiry (m02)', fakeAsync(() => {
    let count = 0;
    component.expired.subscribe(() => count++);

    setEndAt(new Date(Date.now() - 1000).toISOString()); // already past
    tick(0);
    expect(component.remaining).toBe('00:00:00');
    expect(count).toBe(1);

    // m02: the interval was unsubscribed at expiry, so advancing time produces no further ticks.
    tick(5000);
    expect(count).toBe(1);
    // No pending periodic task remains (timer stopped) -> fakeAsync zone is clean.
  }));

  it('resets one-shot state so a reused component emits expired again for a later sale (M01)', fakeAsync(() => {
    let count = 0;
    component.expired.subscribe(() => count++);

    // First sale: already expired -> emits once.
    setEndAt(new Date(Date.now() - 2000).toISOString());
    tick(0);
    expect(count).toBe(1);

    // Reuse for a later, still-active sale: must reset and start counting (not immediately expired).
    setEndAt(new Date(Date.now() + 1500).toISOString());
    tick(0);
    expect(component.remaining).toBe('00:00:02');
    expect(count).toBe(1);
    discardPeriodicTasks();

    // Reuse again for an already-past sale: one-shot state was reset, so it emits a SECOND time.
    setEndAt(new Date(Date.now() - 1000).toISOString());
    tick(0);
    expect(component.remaining).toBe('00:00:00');
    expect(count).toBe(2);
  }));

  afterEach(() => {
    fixture.destroy();
  });
});
