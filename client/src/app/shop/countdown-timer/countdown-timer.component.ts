import { Component, EventEmitter, Input, OnDestroy, OnInit, Output } from '@angular/core';
import { Subscription, timer } from 'rxjs';

// Real-Time Inventory & Flash Sale - live countdown to a flash sale's endAt.
// Emits `expired` exactly once when the window closes. Uses rxjs timer(0, 1000)
// (rxjs 6 root import) and unsubscribes in ngOnDestroy to avoid a leaked interval.
@Component({
  selector: 'app-countdown-timer',
  templateUrl: './countdown-timer.component.html',
  styleUrls: ['./countdown-timer.component.scss']
})
export class CountdownTimerComponent implements OnInit, OnDestroy {
  @Input() endAt: string;
  @Output() expired = new EventEmitter<void>();
  remaining = '00:00:00';
  private timerSub: Subscription;
  private expiredEmitted = false;

  constructor() { }

  ngOnInit(): void {
    // timer(0, 1000): emit immediately, then every second.
    this.timerSub = timer(0, 1000).subscribe(() => this.updateRemaining());
  }

  updateRemaining(): void {
    const diff = new Date(this.endAt).getTime() - Date.now();
    if (diff <= 0) {
      this.remaining = '00:00:00';
      if (!this.expiredEmitted) {
        this.expiredEmitted = true;
        this.expired.emit();
      }
      return;
    }
    const totalSeconds = Math.floor(diff / 1000);
    const hours = Math.floor(totalSeconds / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    const seconds = totalSeconds % 60;
    this.remaining = this.pad(hours) + ':' + this.pad(minutes) + ':' + this.pad(seconds);
  }

  ngOnDestroy(): void {
    if (this.timerSub) {
      this.timerSub.unsubscribe();
    }
  }

  private pad(value: number): string {
    return (value < 10 ? '0' : '') + value;
  }
}
