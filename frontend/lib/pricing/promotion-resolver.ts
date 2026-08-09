/**
 * What kind of discount a promotion gives (`PRD-05`).
 *
 * Names, like every other enum crossing a boundary. The C# ordinals are storage.
 */
export type PromotionType = "PercentOff" | "AmountOff" | "VolumeTiered" | "BuyXGetY";

/** One threshold of a tiered candidate. `minQuantity` is "this many **or more**". */
export type PromotionTierCandidate = {
  minQuantity: number;
  percentOff?: string | null;
  amountOff?: string | null;
  currency?: string | null;
};

/** What a `BuyXGetY` candidate gives away. */
export type BundleCandidate = {
  buyQuantity: number;
  getQuantity: number;
  getPercentOff: string;
  getProductId?: string | null;
};

/**
 * One promotion that could apply to a line, and everything needed to decide whether it does.
 *
 * **Scope is not here, and its absence is the point.** A price candidate carries a scope because
 * `BR-PRD-2` ranks outlet above channel. `BR-PRD-3` ranks nothing of the sort — it selects by
 * *priority* — so how a promotion reached this outlet changes nothing about which one wins. Scope is
 * a filter the caller applies while gathering, and carrying it here would invite a precedence rule
 * the spec does not have.
 *
 * Amounts and percentages are **strings**, as everywhere: `JSON.parse` on a bare number is a float
 * before any engine sees it (`BR-PRD-8`).
 */
export type PromotionCandidate = {
  promotionId: string;
  type: PromotionType;
  priority: number;
  /** `YYYY-MM-DD`. Evaluated in the *outlet's* day, which is why it is handed in — `BR-PRD-6`. */
  validFrom: string;
  validTo: string | null;
  percentOff?: string | null;
  amountOff?: string | null;
  currency?: string | null;
  tiers?: PromotionTierCandidate[] | null;
  bundle?: BundleCandidate | null;
};

/**
 * The promotion that applies, with its tier already chosen.
 *
 * A tiered promotion resolves to the concrete discount its quantity reached — the caller gets a
 * discount, not a table to search a second time. That is what keeps tier selection in one place
 * rather than in every consumer.
 */
export type ResolvedPromotion = {
  promotionId: string;
  type: PromotionType;
  priority: number;
  percentOff: string | null;
  amountOff: string | null;
  currency: string | null;
  bundle: BundleCandidate | null;
};

/**
 * Picks the one promotion that applies to an order line (`PRD-06`, `BR-PRD-3`) — the device's mirror
 * of `PromotionResolver.Resolve`.
 *
 * **Pure** (`BR-PRD-7`): candidates, a quantity and a date in; one answer out. The date is a
 * parameter and a `YYYY-MM-DD` string for the two reasons `resolvePrice` gives — reproducibility, and
 * the fact that a business day is not an instant. `BR-PRD-6` makes that sharper here: a promotion's
 * window is evaluated in the *outlet's* timezone, and only a caller holding the outlet knows what day
 * it is there.
 *
 * **Line-level only.** `BR-PRD-3` allows at most one line-level promotion per line, which is what
 * this returns. Order-level promotions are separate and additive (`B1`), and arrive with Order.
 *
 * Order of preference:
 *
 * 1. the window covers the date — half-open, as everywhere;
 * 2. the candidate actually *does something* at this quantity — a tiered promotion whose lowest
 *    threshold is 6 does not apply to a line of 3, and a buy-two-get-one does not apply to a line of
 *    one. Filtered out rather than allowed to win and then take nothing off;
 * 3. highest `priority`;
 * 4. still tied, the higher `promotionId`.
 *
 * **Never the size of the discount.** The biggest saving does not win; the one the tenant ranked
 * highest does. That is what makes priority worth authoring — a supplier-funded deal can be made to
 * beat a bigger generic one — and it is why nothing here looks at a value.
 */
export function resolvePromotion(
  candidates: readonly PromotionCandidate[],
  quantity: number,
  on: string,
): ResolvedPromotion | null {
  let winner: PromotionCandidate | null = null;
  let winningTier: PromotionTierCandidate | null = null;

  for (const candidate of candidates) {
    if (!covers(candidate, on)) continue;

    // Chosen before the comparison, not after: for a tiered candidate this is also the test of
    // whether it applies at all, so a promotion with no reachable tier never enters the priority
    // contest it would otherwise win and then do nothing with.
    const tier = bestTier(candidate, quantity);
    if (!applies(candidate, quantity, tier)) continue;

    if (winner !== null && !beats(candidate, winner)) continue;

    winner = candidate;
    winningTier = tier;
  }

  if (winner === null) return null;

  return {
    promotionId: winner.promotionId,
    type: winner.type,
    priority: winner.priority,
    // A tiered candidate resolves to **the tier's** discount, falling back to its own — the order
    // matters, because the tier is what the quantity earned.
    //
    // `??` rather than `||` for what it means rather than for what it currently does: every amount
    // here is a non-empty string, so the two behave identically today. `??` is the one that stays
    // correct if an amount is ever allowed to be empty.
    percentOff: (winningTier?.percentOff ?? winner.percentOff) ?? null,
    amountOff: (winningTier?.amountOff ?? winner.amountOff) ?? null,
    currency: (winningTier?.currency ?? winner.currency) ?? null,
    bundle: winner.bundle ?? null,
  };
}

/** Half-open: `[validFrom, validTo)`. */
function covers(candidate: PromotionCandidate, on: string): boolean {
  return on >= candidate.validFrom && (candidate.validTo === null || on < candidate.validTo);
}

/**
 * The tier this quantity reaches — the **highest threshold at or below it** — or null.
 *
 * Highest-reached rather than lowest-matching, because tiers are "N or more" and the author wrote
 * them expecting the better deal to win as the order grows: a line of 30 against tiers at 6, 12 and
 * 24 gets the 24 tier. The vectors deliberately list them out of order to prove the rule is about
 * the numbers rather than about the array.
 */
function bestTier(
  candidate: PromotionCandidate,
  quantity: number,
): PromotionTierCandidate | null {
  if (candidate.type !== "VolumeTiered") return null;

  let best: PromotionTierCandidate | null = null;

  for (const tier of candidate.tiers ?? []) {
    if (tier.minQuantity > quantity) continue;
    if (best !== null && tier.minQuantity <= best.minQuantity) continue;

    best = tier;
  }

  return best;
}

/** Whether this candidate does anything at all at this quantity. */
function applies(
  candidate: PromotionCandidate,
  quantity: number,
  tier: PromotionTierCandidate | null,
): boolean {
  switch (candidate.type) {
    // A tiered promotion with no reachable threshold, or none authored, is inert.
    case "VolumeTiered":
      return tier !== null;

    // Fewer bought than the offer requires. "Buy two get one" on a line of one is not a discount of
    // zero; it is an offer that has not been earned.
    case "BuyXGetY":
      return candidate.bundle != null && quantity >= candidate.bundle.buyQuantity;

    // Flat: applies to any line that got this far.
    default:
      return true;
  }
}

/**
 * Whether `challenger` should displace `holder`: priority, then the id.
 *
 * The id comparison is `resolvePrice`'s, for the same reason and with the same lower-casing — in
 * ASCII `'A'–'F' < 'a'–'f'`, so an un-normalised comparison would depend on how somebody spelled a
 * GUID rather than on what it is.
 */
function beats(challenger: PromotionCandidate, holder: PromotionCandidate): boolean {
  if (challenger.priority !== holder.priority) return challenger.priority > holder.priority;

  return challenger.promotionId.toLowerCase() > holder.promotionId.toLowerCase();
}
