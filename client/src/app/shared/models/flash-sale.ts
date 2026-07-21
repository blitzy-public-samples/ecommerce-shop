export interface IFlashSale {
  id: number;
  productId: number;
  startAt: string;
  endAt: string;
  salePrice: number;
  stockAllocation: number;
  quantityAvailable: number;
}
