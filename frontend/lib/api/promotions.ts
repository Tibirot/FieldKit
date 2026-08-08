import { apiGet, apiSend } from "@/lib/api/client";

/**
 * The four kinds of deal a tenant can author (`B1`, `PRD-05`).
 *
 * Spelled exactly as the API's enum, because that is what goes on the wire — and closed, so a fifth
 * kind has to be added here before any screen can pretend to render one.
 */
export type PromotionType = "PercentOff" | "FixedAmountOff" | "VolumeTiered" | "BuyXGetY";

/** What a `BuyXGetY` promotion gives away. */
export type Bundle = {
  buyQuantity: number;
  getQuantity: number;
  /** A percentage, as a string. `"100.00"` is free, which is the whole point of the type. */
  getPercentOff: string;
  /** Null means *the same product that was bought*. */
  getProductId: string | null;
};

/**
 * A promotion, without its targets, tiers or scope.
 *
 * **`value` is a string, and stays one** — the same rule money obeys (`BR-PRD-8`). A JSON number is
 * an IEEE-754 double the moment a browser parses it, and "12.5% off" losing its last digit is the
 * same class of bug as a price doing so.
 *
 * **`value` is null for the two types whose discount lives elsewhere**: `VolumeTiered` keeps it on
 * its tiers, `BuyXGetY` in its bundle. Null rather than `"0.00"`, because a zero would read as "no
 * discount" instead of "look somewhere else". `currency` is set only for `FixedAmountOff`.
 */
export type Promotion = {
  id: string;
  name: string;
  type: PromotionType;
  value: string | null;
  currency: string | null;
  validFrom: string;
  validTo: string | null;
  priority: number;
  bundle: Bundle | null;
};

/**
 * Author a promotion.
 *
 * The type is here and absent from the update below, matching the API: re-typing would reinterpret
 * the value — 15 meaning "15% off" becoming 15 meaning "€15 off" — and every order already priced
 * against it would then be explained by a rule that no longer exists. The currency is fixed for the
 * same reason.
 */
export type CreatePromotion = {
  name: string;
  type: PromotionType;
  validFrom: string;
  validTo: string | null;
  priority: number;
  value?: string;
  currency?: string;
  bundle?: Bundle;
};

export type UpdatePromotion = {
  name: string;
  validFrom: string;
  validTo: string | null;
  priority: number;
  value?: string;
  bundle?: Bundle;
};

export function fetchPromotions(accessToken: string, signal?: AbortSignal): Promise<Promotion[]> {
  return apiGet<Promotion[]>("/api/products/promotions", accessToken, signal);
}

export function createPromotion(
  accessToken: string,
  body: CreatePromotion,
): Promise<Promotion> {
  return apiSend<Promotion>("POST", "/api/products/promotions", accessToken, body);
}

export function updatePromotion(
  accessToken: string,
  id: string,
  body: UpdatePromotion,
): Promise<Promotion> {
  return apiSend<Promotion>("PUT", `/api/products/promotions/${id}`, accessToken, body);
}

export const promotionsKey = (subject: string) => ["promotions", subject] as const;

// ── What a promotion discounts ─────────────────────────────────────────────────────────────────

/** One thing a promotion discounts. Exactly one of the two ids is set (`PRD-05`). */
export type PromotionTarget = { productId: string | null; categoryId: string | null };

/**
 * The whole target set. A PUT replaces it.
 *
 * **An empty set is a real state**, not a refusal: the promotion then discounts nothing. That
 * mirrors emptying a price list's assignments, which is how a list is withdrawn — and it is how a
 * promotion is taken out of play without editing its window or deleting a record other things point
 * at.
 */
export type SetPromotionTargets = { productIds: string[]; categoryIds: string[] };

export function fetchTargets(
  accessToken: string,
  id: string,
  signal?: AbortSignal,
): Promise<PromotionTarget[]> {
  return apiGet<PromotionTarget[]>(
    `/api/products/promotions/${id}/targets`,
    accessToken,
    signal,
  );
}

export function setTargets(
  accessToken: string,
  id: string,
  targets: SetPromotionTargets,
): Promise<PromotionTarget[]> {
  return apiSend<PromotionTarget[]>(
    "PUT",
    `/api/products/promotions/${id}/targets`,
    accessToken,
    targets,
  );
}

export const targetsKey = (subject: string, id: string) =>
  ["promotions", subject, id, "targets"] as const;

/**
 * Whether a type carries a discount of its own.
 *
 * Mirrors `Promotion.CarriesItsOwnValue` server-side, and is the one branch the form is built
 * around: sending a value for a type that has none is refused rather than ignored, because a caller
 * that sent one has misunderstood what the type does.
 */
export function carriesItsOwnValue(type: PromotionType): boolean {
  return type === "PercentOff" || type === "FixedAmountOff";
}

// ── The thresholds a tiered promotion discounts by ─────────────────────────────────────────────

/**
 * One threshold: buy this many, get this off (`PRD-05`).
 *
 * `currency` is set on an amount tier and null on a percentage one — the same pairing a flat
 * promotion's value obeys. A promotion's tiers are all one or all the other; see `SetPromotionTiers`.
 */
export type PromotionTier = { minQuantity: number; value: string; currency: string | null };

/**
 * Every threshold of a tiered promotion. A PUT replaces the set.
 *
 * **An empty set is a real state**, not a refusal: the promotion then discounts nothing, exactly as
 * an untargeted promotion or an unassigned price list does.
 */
export type SetPromotionTiers = { tiers: PromotionTier[] };

export function fetchTiers(
  accessToken: string,
  id: string,
  signal?: AbortSignal,
): Promise<PromotionTier[]> {
  return apiGet<PromotionTier[]>(`/api/products/promotions/${id}/tiers`, accessToken, signal);
}

export function setTiers(
  accessToken: string,
  id: string,
  tiers: readonly PromotionTier[],
): Promise<PromotionTier[]> {
  return apiSend<PromotionTier[]>(
    "PUT",
    `/api/products/promotions/${id}/tiers`,
    accessToken,
    { tiers },
  );
}

export const tiersKey = (subject: string, id: string) =>
  ["promotions", subject, id, "tiers"] as const;

/**
 * The smallest threshold a tier may start at.
 *
 * A tier at 1 is "buy one or more", which is every line that matched at all — a flat discount
 * wearing a tier's clothes, and one that would silently shadow the `PercentOff` type it duplicates.
 * The API refuses it; stating the number here lets the form say so before the round trip.
 */
export const SMALLEST_TIER = 2;
