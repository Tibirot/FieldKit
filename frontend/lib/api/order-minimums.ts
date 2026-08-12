import { apiGet, apiSend } from "@/lib/api/client";

/**
 * The smallest order one channel or one shop may place (`ORD-06`, `BR-ORD-5`).
 *
 * **Exactly one of the two ids is set.** That is how the rule is ranked — an outlet's own minimum
 * beats its channel's — and the server refuses a row with both by name as well as by a constraint.
 *
 * `amount` is a decimal **string**, per `BR-PRD-8`. A JSON number would have been through IEEE-754
 * before anything here read it, and on this field a hundredth decides whether a rep may send their
 * order at all.
 */
export type OrderMinimum = {
  id: string;
  channelId: string | null;
  outletId: string | null;
  amount: string;
  currencyCode: string;
};

/** One minimum as an author states it — no id, because the set is replaced rather than patched. */
export type OrderMinimumWrite = {
  channelId: string | null;
  outletId: string | null;
  amount: string;
  currencyCode: string;
};

export function fetchOrderMinimums(
  accessToken: string,
  signal?: AbortSignal,
): Promise<OrderMinimum[]> {
  return apiGet<OrderMinimum[]>("/api/products/order-minimums", accessToken, signal);
}

/**
 * Replaces every minimum this tenant has.
 *
 * <b>A PUT of the whole set, not a patch</b>, which is the server's shape and worth knowing here:
 * an author who saves a screen showing one channel has told the server that channel is the only one
 * with a minimum. Every row the screen holds has to be sent, including the ones nobody touched.
 *
 * <b>An empty list is a real state, and the ordinary one</b> — no minimum anywhere, so every order
 * is submittable at any value. It is also how the last one is withdrawn.
 */
export function setOrderMinimums(
  accessToken: string,
  minimums: readonly OrderMinimumWrite[],
): Promise<OrderMinimum[]> {
  return apiSend<OrderMinimum[]>("PUT", "/api/products/order-minimums", accessToken, { minimums });
}

export const orderMinimumsKey = (subject: string) => ["order-minimums", subject] as const;
