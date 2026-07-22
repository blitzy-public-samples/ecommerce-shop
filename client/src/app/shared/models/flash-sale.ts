export interface IFlashSale {
  id: number;
  productId: number;
  startAt: string;
  endAt: string;
  salePrice: number;
  stockAllocation: number;
  quantityAvailable: number;
}

// M9-fe: The server "FlashSaleEnded" hub event carries only the identifiers of the sale that
// ended — { productId, saleId } — NOT a full IFlashSale projection. Modelling it explicitly lets
// consumers clear ONLY the sale whose id matches (guarding against an out-of-order tick that
// sends Started(new) then Ended(old), which would otherwise wipe the freshly-started sale).
export interface IFlashSaleEnded {
  productId: number;
  saleId: number;
}
