import {Injectable} from '@angular/core';
import {CanActivate, Router, UrlTree} from '@angular/router';
import {BasketService} from "../../basket/basket.service";
import {StockService} from "../services/stock.service";

/**
 * Route guard for /checkout that enforces the basket stock gate for ALL
 * navigation modalities — closing the QA H-C bypass where a keyboard user (Enter
 * on the proceed control) or a direct URL / back-forward navigation could reach
 * checkout with an out-of-stock or insufficient-quantity line.
 *
 * The in-page proceed control is already rendered as a disabled <button> (inert
 * to mouse, touch, and keyboard), but a route the user can type or bookmark must
 * also be defended at the router. If any basket line's authoritative last-known
 * stock is exactly zero, or is a KNOWN value below the requested quantity, the
 * guard redirects to /basket, where the shopper can see live status and recover
 * (remove or reduce the offending line — the documented recovery flow).
 *
 * Unknown stock (never broadcast for a line) is intentionally NOT blocked here:
 * doing so would make a fresh direct navigation / reload of /checkout unreachable
 * before any stock value has arrived. Availability while the hub is disconnected
 * is instead enforced fail-closed by the basket and checkout proceed/submit
 * controls, which require the Connected state.
 */
@Injectable({
  providedIn: 'root'
})
export class StockGuard implements CanActivate {
  constructor(
    private basketService: BasketService,
    private stockService: StockService,
    private router: Router) {
  }

  canActivate(): boolean | UrlTree {
    const basket = this.basketService.getCurrentBasketValue();

    // No basket or an empty basket has nothing to gate; the checkout page handles
    // the empty-basket experience itself.
    if (!basket || !basket.items || basket.items.length === 0) {
      return true;
    }

    const blocked = basket.items.some(item => {
      const stock = this.stockService.getCurrentStock(item.id);
      // Known zero => out of stock; a known value below the requested quantity =>
      // insufficient. Either condition blocks checkout and returns the shopper to
      // /basket to recover.
      return stock === 0 || (stock !== undefined && stock < item.quantity);
    });

    return blocked ? this.router.createUrlTree(['/basket']) : true;
  }
}
