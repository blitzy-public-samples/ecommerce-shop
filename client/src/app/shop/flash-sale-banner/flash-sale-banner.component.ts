import { Component, Input } from '@angular/core';
import { IFlashSale } from '../../shared/models/flash-sale';

// N5: this is a purely presentational widget - it renders its @Input bindings and has no
// initialisation logic, so the empty constructor and empty ngOnInit/OnInit boilerplate were removed.
@Component({
  selector: 'app-flash-sale-banner',
  templateUrl: './flash-sale-banner.component.html',
  styleUrls: ['./flash-sale-banner.component.scss']
})
export class FlashSaleBannerComponent {
  @Input() flashSale: IFlashSale;
  @Input() basePrice: number;
}
