import { Component, EventEmitter, Input, OnChanges, OnDestroy, OnInit, Output, SimpleChanges } from '@angular/core';
import { Subscription, timer } from 'rxjs';

/**
 * Live countdown to a flash sale's end time.
 *
 * Real-Time Inventory & Flash Sale feature widget. Given an ISO `endAt` timestamp it renders the
 * remaining time as HH:MM:SS and emits `expired` exactly once when the deadline passes. The
 * component is designed to be safely REUSED for successive sales (its host may rebind `endAt`
 * without destroying/recreating it), so all one-shot and display state is (re)initialised for each
 * distinct deadline.
 */
@Component({
  selector: 'app-countdown-timer',
  templateUrl: './countdown-timer.component.html',
  styleUrls: ['./countdown-timer.component.scss']
})
export class CountdownTimerComponent implements OnInit, OnChanges, OnDestroy {
  /** ISO-8601 timestamp of the flash sale's end (`end_at`). */
  @Input() endAt: string;

  /** Emitted exactly once per distinct valid deadline, when that deadline is reached. */
  @Output() expired = new EventEmitter<void>();

  /** Human-readable remaining time. Also used as the defined fallback string for invalid input (m01). */
  remaining = '00:00:00';

  /**
   * True when `endAt` is missing or unparsable. The template renders a defined fallback state for
   * this case instead of a NaN-filled string (review finding m01).
   */
  invalid = false;

  private timerSub: Subscription;
  private expiredEmitted = false;
  /** Parsed deadline in epoch milliseconds; `NaN` when `endAt` is invalid (m01). */
  private deadlineMs = NaN;
  /** Guards against a redundant second start when both ngOnChanges and ngOnInit fire on first bind. */
  private hasStarted = false;

  ngOnInit(): void {
    // If the initial input binding already triggered ngOnChanges, the timer is running; only start
    // here for the (unbound-input) case where ngOnChanges never fired.
    if (!this.hasStarted) {
      this.startForCurrentDeadline();
      this.hasStarted = true;
    }
  }

  /**
   * React to `endAt` changes for a reused component (review finding M01).
   *
   * A component reused for a LATER sale must reset its one-shot `expired` state and restart (or
   * cancel) the ticking timer for the new deadline; otherwise a stale `expiredEmitted` flag would
   * suppress the `expired` event for the new sale, breaking the one-shot contract.
   */
  ngOnChanges(changes: SimpleChanges): void {
    if (changes.endAt) {
      this.startForCurrentDeadline();
      this.hasStarted = true;
    }
  }

  ngOnDestroy(): void {
    this.stopTimer();
  }

  /**
   * (Re)initialise all state for the current `endAt` and start ticking, or render a defined
   * fallback when the deadline is invalid. Safe to call repeatedly (M01).
   */
  private startForCurrentDeadline(): void {
    // Reset one-shot + display state for the new deadline (M01).
    this.stopTimer();
    this.expiredEmitted = false;

    // m01: validate the parsed deadline. Guard null/undefined/empty and unparsable strings so an
    // invalid input yields a defined fallback rather than NaN:NaN:NaN.
    const parsed = this.endAt != null ? new Date(this.endAt).getTime() : NaN;
    this.deadlineMs = parsed;

    if (isNaN(parsed)) {
      this.invalid = true;
      this.remaining = '--:--:--';
      return;
    }

    this.invalid = false;
    // timer(0, 1000): emit immediately then every second so the display is correct on first paint.
    this.timerSub = timer(0, 1000).subscribe(() => this.updateRemaining());
  }

  private updateRemaining(): void {
    const diff = this.deadlineMs - Date.now();
    if (diff <= 0) {
      this.remaining = '00:00:00';
      // m02: stop ticking immediately at expiry (before emitting) so no further intervals run until
      // the component is destroyed.
      this.stopTimer();
      if (!this.expiredEmitted) {
        this.expiredEmitted = true;
        this.expired.emit();
      }
      return;
    }
    // m03: ceiling semantics for remaining positive time. Flooring can render 00:00:00 while time
    // still remains (and before `expired` is emitted); ceiling guarantees the displayed 00:00:00
    // coincides exactly with the expiry transition handled above.
    const totalSeconds = Math.ceil(diff / 1000);
    const hours = Math.floor(totalSeconds / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    const seconds = totalSeconds % 60;
    this.remaining = this.pad(hours) + ':' + this.pad(minutes) + ':' + this.pad(seconds);
  }

  private stopTimer(): void {
    if (this.timerSub) {
      this.timerSub.unsubscribe();
      this.timerSub = undefined;
    }
  }

  private pad(value: number): string {
    return (value < 10 ? '0' : '') + value;
  }
}
