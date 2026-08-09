/**
 * Which kind of assignment a resolved price came from.
 *
 * Names, not numbers — the same values the API and the vector files carry. The C# enum's ordinals
 * are storage; what crosses a boundary is the word.
 */
export type PriceScope = "Channel" | "Outlet";

/**
 * How specific each scope is. **`Outlet` beats `Channel`, always.**
 *
 * A rank table rather than a relied-upon declaration order, because TypeScript string unions have no
 * ordinal to compare and inventing one from array position would be the same silent coupling the C#
 * enum's comment warns about. A scope added here has to be given a rank deliberately.
 */
const SPECIFICITY: Readonly<Record<PriceScope, number>> = {
  Channel: 0,
  Outlet: 1,
};

/**
 * One price this product could have on this date, and where it came from.
 *
 * **`amount` is the string the caller was given, and stays one.** Resolution is a *selection* rule:
 * it picks a candidate, it does not do arithmetic. Parsing the amount here would mean re-formatting
 * it on the way out, and `"12.50"` becoming `"12.5"` is a different answer to give a rep even though
 * it is the same number. The arithmetic engines (tax, promotions) take it from here as a `Money`.
 */
export type PriceCandidate = {
  priceListId: string;
  scope: PriceScope;
  currency: string;
  /** `YYYY-MM-DD`. A business day, not an instant — see `resolvePrice`. */
  effectiveFrom: string;
  effectiveTo: string | null;
  amount: string;
};

/** The price that applies, and which candidate won. */
export type ResolvedPrice = {
  priceListId: string;
  scope: PriceScope;
  currency: string;
  amount: string;
};

/**
 * Picks the price that applies to one product at one outlet on one date (`PRD-04`, `BR-PRD-2`) —
 * the device's mirror of `PriceResolver.Resolve`.
 *
 * **Pure** (`BR-PRD-7`): candidates in, one answer out. No fetch, no clock, no storage. That is what
 * lets it run on a phone with no signal, and what lets the same vector file check both languages
 * (`PRD-08`).
 *
 * **The date is a parameter, and a string.** Reproducibility is the reason for the first: an order
 * placed at a price must still resolve to that price when it syncs three days later, and a function
 * that asks what day it is cannot promise that. A `YYYY-MM-DD` string rather than a `Date` is the
 * reason for the second — a business day is not an instant (`BR-PRD-6`), and `new Date("2026-03-15")`
 * is midnight *UTC*, which is the previous day in Bucharest for anyone west of it. Comparing the
 * strings is exact, timezone-free, and gives the same answer as comparing the days.
 *
 * **Null is a real answer**, not an error: a product with no list covering the date is one this
 * outlet cannot be sold, which is the caller's decision to make.
 */
export function resolvePrice(
  candidates: readonly PriceCandidate[],
  on: string,
): ResolvedPrice | null {
  let winner: PriceCandidate | null = null;

  for (const candidate of candidates) {
    if (!covers(candidate, on)) continue;
    if (winner !== null && !beats(candidate, winner)) continue;

    winner = candidate;
  }

  return winner === null
    ? null
    : {
        priceListId: winner.priceListId,
        scope: winner.scope,
        currency: winner.currency,
        amount: winner.amount,
      };
}

/** Half-open: `[effectiveFrom, effectiveTo)` — a successor starts the day its predecessor stops. */
function covers(candidate: PriceCandidate, on: string): boolean {
  return on >= candidate.effectiveFrom
    && (candidate.effectiveTo === null || on < candidate.effectiveTo);
}

/**
 * Whether `challenger` should displace `holder` (`BR-PRD-2`).
 *
 * In order: a more specific scope wins; then the later `effectiveFrom` — the most recent decision;
 * then the higher id.
 *
 * **The id tiebreak is not a rule about correctness, it is a rule about agreement.** Two lists at the
 * same scope with the same start date is a data problem, and no tiebreak makes it right. What it buys
 * is determinism: the server and every device pick the same one, so a rep and a supervisor see the
 * same number and an order re-priced during sync does not move.
 *
 * **Compared as lowercase canonical strings**, which `vectors/README.md` states is equivalent to
 * comparing the 16 bytes big-endian — the form the canonical spelling prints. Lowercasing is
 * load-bearing rather than cosmetic: in ASCII `'0'–'9' < 'A'–'F' < 'a'–'f'`, so an id spelled in
 * upper case would sort below every lower-case one and this comparison would depend on how somebody
 * typed a GUID.
 */
function beats(challenger: PriceCandidate, holder: PriceCandidate): boolean {
  if (challenger.scope !== holder.scope) {
    return SPECIFICITY[challenger.scope] > SPECIFICITY[holder.scope];
  }

  if (challenger.effectiveFrom !== holder.effectiveFrom) {
    return challenger.effectiveFrom > holder.effectiveFrom;
  }

  return challenger.priceListId.toLowerCase() > holder.priceListId.toLowerCase();
}
