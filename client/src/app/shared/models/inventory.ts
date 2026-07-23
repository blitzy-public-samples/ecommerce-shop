export interface IInventoryReservation {
  id: number;
  productId: number;
  quantity: number;
  sessionId: string;
  expiresAt: string;
}

export interface IInventoryUpdate {
  productId: number;
  quantityAvailable: number;
}
