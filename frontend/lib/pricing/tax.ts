import { Money, percentOf } from "@/lib/pricing/money";

/**
 * One rate that could apply, and when it does.
 *
 * The country is not here: the caller has already filtered to one jurisdiction, because a rate for
 * somewhere else is not a candidate at all rather than a losing one.
 */
export type TaxRateCandidate = {
  taxRateId: string;
  /** A decimal string — `"19.00"`, not `19`. A percentage is money-shaped (`BR-PRD-8`). */
  percentage: string;
  /** `YYYY-MM-DD`, half-open against `effectiveTo`. */
  effectiveFrom: string;
  effectiveTo: string | null;
};

/** A net amount, the tax on it, and the two added up. */
export type TaxedAmount = {
  net: Money;
  tax: Money;
  gross: Money;
};

/**
 * The rate applying on `on`, or null when none does (`PRD-07`, `BR-PRD-5`) — the mirror of
 * `TaxEngine.Resolve`.
 *
 * **Null is not zero, and the distinction is the point.** No rate authored for this class in this
 * country means *unknown*; a rate of `"0.00"` means zero-rated. Collapsing them would let a missing
 * setup step invoice as tax-free and look deliberate.
 *
 * Latest `effectiveFrom` wins — that is how an announced rate change takes over from its predecessor
 * — and a tie goes to the higher id, lower-cased before comparing for the ASCII reason the other two
 * resolvers give.
 */
export function resolveTaxRate(
  candidates: readonly TaxRateCandidate[],
  on: string,
): TaxRateCandidate | null {
  let best: TaxRateCandidate | null = null;

  for (const candidate of candidates) {
    if (on < candidate.effectiveFrom) continue;
    if (candidate.effectiveTo !== null && on >= candidate.effectiveTo) continue;
    if (best !== null && !beats(candidate, best)) continue;

    best = candidate;
  }

  return best;
}

/**
 * Applies a percentage to a net line (`BR-PRD-9`) — the mirror of `TaxEngine.Apply`, and the first
 * rule in this directory that does arithmetic rather than selection.
 *
 * **On the rounded net line, and rounded again after.** Two roundings, both deliberate. The net is
 * rounded first because that is the figure printed on the order and the one a shopkeeper checks —
 * tax computed on an unrounded intermediate would not match the net anyone can see. The tax is then
 * rounded because it is money in its own right, on its own line of an invoice.
 *
 * **Gross is net plus tax, not net times 1.19.** Those differ once rounding is involved, and only
 * the first can be shown as three numbers that add up.
 *
 * Half-up away from zero at both steps — `Money.round` owns that policy and this defers to it rather
 * than restating it. That is also why this function takes and returns `Money` rather than strings:
 * the rounding rule lives in one type, in both languages.
 */
export function applyTax(net: Money, percentage: string): TaxedAmount {
  const rounded = net.round();
  const tax = percentOf(rounded, percentage).round();

  return { net: rounded, tax, gross: rounded.add(tax) };
}

/** Latest effective date, then the higher id — the same last resort the other resolvers use. */
function beats(challenger: TaxRateCandidate, holder: TaxRateCandidate): boolean {
  if (challenger.effectiveFrom !== holder.effectiveFrom) {
    return challenger.effectiveFrom > holder.effectiveFrom;
  }

  return challenger.taxRateId.toLowerCase() > holder.taxRateId.toLowerCase();
}
