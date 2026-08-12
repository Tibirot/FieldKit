import { apiGet, apiSend } from "@/lib/api/client";

/**
 * A money amount as this API carries it (`BR-PRD-8`).
 *
 * **`amount` is a string, and stays one.** `JSON.parse` turns a bare `12.50` into an IEEE-754
 * double the moment it touches JavaScript, and the device pricing engine would then be doing float
 * arithmetic before `decimal.js` ever saw the value. The server never emits a number here; nothing
 * on this side may create one either — not with `Number()`, not with `parseFloat`, not by handing it
 * to a locale number formatter on the way out.
 */
export type Money = { amount: string; currency: string };

/** A price list, without its prices (`PRD-03`). */
export type PriceList = {
  id: string;
  name: string;
  currency: string;
  effectiveFrom: string;
  effectiveTo: string | null;
};

/**
 * Create a price list.
 *
 * The currency is here and absent from the update below, matching the API: changing it would
 * reinterpret every price in the list, and 12.50 EUR becoming 12.50 RON is not a conversion.
 */
export type CreatePriceList = {
  name: string;
  currency: string;
  effectiveFrom: string;
  effectiveTo: string | null;
};

export type UpdatePriceList = {
  name: string;
  effectiveFrom: string;
  effectiveTo: string | null;
};

/** One product's price in a list. The currency comes from the list, not the line. */
export type PriceLine = { productId: string; sku: string; name: string; price: Money };

/** One line as an author sets it — the amount as typed, unparsed. */
export type PriceLineWrite = { productId: string; amount: string };

export function fetchPriceLists(accessToken: string, signal?: AbortSignal): Promise<PriceList[]> {
  return apiGet<PriceList[]>("/api/products/price-lists", accessToken, signal);
}

export function createPriceList(
  accessToken: string,
  body: CreatePriceList,
): Promise<PriceList> {
  return apiSend<PriceList>("POST", "/api/products/price-lists", accessToken, body);
}

export function updatePriceList(
  accessToken: string,
  id: string,
  body: UpdatePriceList,
): Promise<PriceList> {
  return apiSend<PriceList>("PUT", `/api/products/price-lists/${id}`, accessToken, body);
}

export function fetchPrices(
  accessToken: string,
  id: string,
  signal?: AbortSignal,
): Promise<PriceLine[]> {
  return apiGet<PriceLine[]>(`/api/products/price-lists/${id}/prices`, accessToken, signal);
}

/**
 * Replaces a list's prices wholesale.
 *
 * A product left out is unpriced in this list, not left alone — the same replace-not-merge the
 * assortment endpoints use, and the reason the editor shows the whole catalogue.
 */
export function setPrices(
  accessToken: string,
  id: string,
  prices: readonly PriceLineWrite[],
): Promise<PriceLine[]> {
  return apiSend<PriceLine[]>(
    "PUT",
    `/api/products/price-lists/${id}/prices`,
    accessToken,
    { prices },
  );
}

// ── Where a list applies ───────────────────────────────────────────────────────────────────────

/** One place a price list reaches. Exactly one of the two ids is set (`PRD-03`). */
export type PriceListAssignment = { channelId: string | null; outletId: string | null };

/** The whole scope. A PUT replaces it, and an empty scope withdraws the list. */
export type SetPriceListScope = { channelIds: string[]; outletIds: string[] };

export function fetchAssignments(
  accessToken: string,
  id: string,
  signal?: AbortSignal,
): Promise<PriceListAssignment[]> {
  return apiGet<PriceListAssignment[]>(
    `/api/products/price-lists/${id}/assignments`,
    accessToken,
    signal,
  );
}

/**
 * Replaces where a list applies.
 *
 * **Also announces it.** The server raises `PriceListPublished` into the outbox in the same
 * transaction, which is what Sync turns into a reference delta — including when the scope is emptied,
 * because "this list now reaches nobody" is how a list is withdrawn and a device that never hears it
 * keeps pricing against one that no longer applies.
 */
export function setAssignments(
  accessToken: string,
  id: string,
  scope: SetPriceListScope,
): Promise<PriceListAssignment[]> {
  return apiSend<PriceListAssignment[]>(
    "PUT",
    `/api/products/price-lists/${id}/assignments`,
    accessToken,
    scope,
  );
}

export const assignmentsKey = (subject: string, id: string) =>
  ["price-lists", subject, id, "assignments"] as const;

export const priceListsKey = (subject: string) => ["price-lists", subject] as const;

export const pricesKey = (subject: string, id: string) =>
  ["price-lists", subject, id, "prices"] as const;

/**
 * Whether this could be a decimal amount the API will accept.
 *
 * Deliberately the same shape the server enforces, and deliberately **not** a locale-aware parse:
 * `"12,50"` is refused rather than read as 12.50, because under invariant culture it would parse to
 * **1250** if thousands separators were allowed — a hundredfold error that reads as a plausible
 * price. Checking it here means the message appears under the field instead of arriving as a refusal
 * about a list; the server checks it again regardless.
 *
 * Not `Number.isFinite(Number(value))`: that accepts `"1e3"` and `"Infinity"`, and turns the string
 * into a float to find out.
 *
 * <b>Surrounding space is accepted, not refused</b> — the `.trim()` above is deliberate, and this
 * comment used to list `" 12 "` among the things a naive `Number()` would wrongly accept. It does
 * not: both read it as twelve. Corrected in W11 slice 8b-iii by a test that asserted the sentence
 * and found it false. Callers send the trimmed value.
 */
export function looksLikeAnAmount(value: string): boolean {
  return /^-?\d+(\.\d+)?$/.test(value.trim());
}
