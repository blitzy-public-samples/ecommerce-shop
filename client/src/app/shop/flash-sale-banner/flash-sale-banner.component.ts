import { Component, Input, OnInit } from '@angular/core';
import { IFlashSale } from '../../shared/models/flash-sale';

@Component({
  selector: 'app-flash-sale-banner',
  templateUrl: './flash-sale-banner.component.html',
  styleUrls: ['./flash-sale-banner.component.scss']
})
export class FlashSaleBannerComponent implements OnInit {
  @Input() flashSale: IFlashSale;
  @Input() basePrice: number;

  constructor() { }

  ngOnInit(): void {
  }
}
