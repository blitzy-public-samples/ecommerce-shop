import { Component, Input, OnInit } from '@angular/core';

@Component({
  selector: 'app-live-stock-indicator',
  templateUrl: './live-stock-indicator.component.html',
  styleUrls: ['./live-stock-indicator.component.scss']
})
export class LiveStockIndicatorComponent implements OnInit {
  @Input() quantityAvailable: number;
  @Input() lowStockThreshold = 5;

  constructor() { }

  ngOnInit(): void {
  }

  get isOutOfStock(): boolean {
    return this.quantityAvailable <= 0;
  }

  get isLowStock(): boolean {
    return this.quantityAvailable > 0 && this.quantityAvailable <= this.lowStockThreshold;
  }
}
