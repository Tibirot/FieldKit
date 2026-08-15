import { apiGet } from "@/lib/api/client";

/**
 * Where an order stands (`ORD-01`).
 *
 * `Draft` never reaches this server — an order is sealed on the device before it is pushed — and
 * nothing today sets `Accepted` or `Cancelled`: rejection is the only transition the back office
 * has. Both are in the union because the server may send them once that changes, and a screen that
 * cannot name a state it receives is worse than one that renders it plainly.
 */
export type OrderStatus = "Draft" | "Submitted" | "Accepted" | "Rejected" | "Cancelled";

/** Why an order was refused whole (`ORD-12`, `F4`). */
export type OrderRejectionReason =
  | "OffAssortment"
  | "BelowMinimum"
  | "OutletClosed"
  | "CreditHold"
  | "Other";

/**
 * Whether the server's re-pricing agreed with the device's (`BR-ORD-2`).
 *
 * `NotRepriced` is not a failure: an outlet whose price list would not resolve is a different
 * problem from a price the server disputes, and the two are deliberately not merged.
 */
export type PriceAgreement = "NotRepriced" | "Agrees" | "Differs";

/**
 * The refusal, as stored.
 *
 * `offendingProductId` is often null and that is ordinary — half of `F4`'s own examples point at no
 * line at all. An outlet that closed during the offline window is not something the rep can edit.
 */
export type OrderRejection = {
  reason: OrderRejectionReason;
  offendingProductId: string | null;
  note: string | null;
};

export type OrderLine = {
  productId: string;
  quantity: number;
  unitOfMeasure: string;
  packSize: number | null;
  unitPrice: number;
  lineTotal: number;
};

/**
 * A stored order (`ORD-01`).
 *
 * **`total` is the device's** — what the rep and the shopkeeper settled at the counter. The server
 * re-prices and *flags*; it never applies (`BR-ORD-2`), so `serverTotal` sits beside rather than
 * instead. `agreement` is sent rather than derived here: comparing two decimals in a browser is
 * exactly the sort of second implementation that disagrees subtly with the first.
 */
export type Order = {
  id: string;
  visitId: string;
  outletId: string;
  userId: string;
  status: OrderStatus;
  currencyCode: string;
  total: number;
  taxTotal: number;
  serverTotal: number | null;
  serverTaxTotal: number | null;
  agreement: PriceAgreement;
  capturedAtUtc: string;
  lines: OrderLine[];
  rejection: OrderRejection | null;
};

export const ordersKey = (subject: string, status?: OrderStatus) =>
  ["orders", subject, status ?? "all"] as const;

/**
 * The queue, newest first.
 *
 * **Bounded by the server** — a ceiling rather than a page size, the call the visit list and the
 * outlet audits both made. There is no cursor and no date window yet.
 */
export function fetchOrders(
  accessToken: string,
  status?: OrderStatus,
  signal?: AbortSignal,
): Promise<Order[]> {
  const suffix = status ? `?status=${status}` : "";

  return apiGet<Order[]>(`/api/orders${suffix}`, accessToken, signal);
}
