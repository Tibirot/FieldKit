import { Decimal, Money } from "./money";

/**
 * Which scope an order minimum reached this outlet through.
 *
 * Names, not numbers — the same two `PriceScope` carries, and for the same reason: the C# enum's
 * ordinals are storage, and what crosses a boundary is the word.
 */
export type OrderMinimumScope = "Channel" | "Outlet";

/**
 * How specific each scope is. **`Outlet` beats `Channel`, always.**
 *
 * A rank table rather than declaration order, exactly as `price-resolver.ts` has it — a string union
 * has no ordinal, and inventing one from array position couples the answer to the order somebody
 * happened to write the type in.
 */
const SPECIFICITY: Readonly<Record<OrderMinimumScope, number>> = {
  Channel: 0,
  Outlet: 1,
};

/**
 * One minimum this order could have to meet, and where it came from.
 *
 * **`amount` is the string the caller was given, and stays one** — the same rule `PriceCandidate`
 * states. Resolution *selects*; it does not do arithmetic, and parsing here would mean re-formatting
 * on the way out.
 */
export type OrderMinimumCandidate = {
  orderMinimumId: string;
  scope: OrderMinimumScope;
  currencyCode: string;
  amount: string;
};

/** The minimum that applies, and which candidate won. */
export type ResolvedOrderMinimum = {
  orderMinimumId: string;
  scope: OrderMinimumScope;
  currencyCode: string;
  amount: string;
};

/**
 * What an order minimum says about one order.
 *
 * `CurrencyMismatch` and `Unreadable` are their own answers rather than folded into `NotMet`, because
 * what a rep should *do* differs: one is a misconfiguration to report, the other is a broken row or
 * an order that has not priced, and neither is fixed by adding stock.
 */
export type OrderMinimumVerdict = "None" | "Met" | "NotMet" | "CurrencyMismatch" | "Unreadable";

/**
 * Picks the minimum that applies to one outlet (`ORD-06`, `BR-ORD-5`) — the device's mirror of
 * `OrderMinimumResolver.Resolve`.
 *
 * **Pure**, like the three resolvers beside it: candidates in, one answer out. No fetch, no clock, no
 * storage — which is what lets it run at a counter with no signal, and this rule is enforced *here*
 * rather than on the server precisely because "must be met to submit" is a question asked there.
 *
 * **Null is the ordinary answer, not an error.** `BR-ORD-5` says a minimum applies *if configured*,
 * and most tenants will configure none — so "no minimum" is a first-class result, and an order with
 * no minimum is submittable at any value, which is what every order has been until now.
 *
 * **There is no date.** Worth stating because every other rule in this module has one: a price list
 * and a promotion both have windows, and nothing in `ORD-06` asks for a minimum that starts on a
 * date. The server's resolver says the same.
 */
export function resolveOrderMinimum(
  candidates: readonly OrderMinimumCandidate[],
): ResolvedOrderMinimum | null {
  let winner: OrderMinimumCandidate | null = null;

  for (const candidate of candidates) {
    if (winner !== null && !beats(candidate, winner)) continue;

    winner = candidate;
  }

  return winner === null
    ? null
    : {
        orderMinimumId: winner.orderMinimumId,
        scope: winner.scope,
        currencyCode: winner.currencyCode,
        amount: winner.amount,
      };
}

/**
 * Whether `challenger` should displace `holder`.
 *
 * **A tie is broken by id, for agreement rather than for correctness.** Two minimums at the same
 * scope is a data problem no tiebreak makes right; what it buys is that this device, every other
 * device, and the server all refuse the same order.
 *
 * Lower-cased before comparing, which is load-bearing: in ASCII `'A'–'F' < 'a'–'f'`, so an id spelled
 * in upper case would sort below every lower-case one and the winner would depend on how somebody
 * typed a GUID. `>` on strings is JavaScript's code-unit comparison, which is what
 * `string.CompareOrdinal` does — the ids are ASCII, so the two agree.
 */
function beats(challenger: OrderMinimumCandidate, holder: OrderMinimumCandidate): boolean {
  if (challenger.scope !== holder.scope) {
    return SPECIFICITY[challenger.scope] > SPECIFICITY[holder.scope];
  }

  return challenger.orderMinimumId.toLowerCase() > holder.orderMinimumId.toLowerCase();
}

/**
 * Whether an order of `total` meets `minimum` — the mirror of `OrderMinimumResolver.Check`.
 *
 * **Separate from resolution on purpose.** Picking which rule applies and deciding whether an order
 * satisfies it are two questions, and only the first has a precedence story. Keeping them apart is
 * what lets the screen show the rep the threshold before they have added a line.
 *
 * **A mismatched currency is a refusal to answer, not a refusal of the order.** An order's currency
 * comes from the list that priced it (`BR-ORD-7`) and nothing makes that agree with what somebody
 * typed into a minimum. Comparing 50 EUR against 200 RON by their numbers alone would refuse orders
 * comfortably over the intended threshold and accept ones under it — and it would look like the rule
 * working. `Money` throws across currencies, which is right in arithmetic and wrong for a rep at a
 * counter, so this reports the disagreement instead.
 *
 * **`total` may be null**, which the C# signature has no way to express and the device needs: an
 * order whose lines have not priced has no total to compare. That answers `Unreadable` alongside a
 * broken stored amount, because both mean *this device cannot decide* — and a caller that must not
 * submit on either is served by one value.
 */
export function checkOrderMinimum(
  minimum: ResolvedOrderMinimum | null,
  total: Money | null,
): OrderMinimumVerdict {
  if (minimum === null) return "None";
  if (total === null) return "Unreadable";

  if (minimum.currencyCode.toUpperCase() !== total.currency.toUpperCase()) {
    return "CurrencyMismatch";
  }

  /*
   * The shapes `decimal.js` accepts and .NET does not, refused before parsing — W11½ R7.
   *
   * <b>Found by the shared vector corpus, not by reasoning.</b> `OrderMinimumResolver.Check` parses
   * with `NumberStyles.AllowDecimalPoint | AllowLeadingSign`, which excludes exponents and
   * hexadecimal; `decimal.js` reads `"1e2"` as 100 and `"0x10"` as 16. So this device called an
   * order **Met** against a minimum the server cannot read at all — and `BR-ORD-5` has no
   * server-side gate, so nothing downstream would ever have said so.
   *
   * Unreachable through today's write path, which parses with the identical styles and refuses the
   * row. That is precisely the kind of assumption a vector file exists to stop depending on: the
   * agreement is now pinned rather than inherited from two validators that happen to match.
   *
   * <b>The device takes the stricter side deliberately.</b> `Unreadable` stops a submission; `Met`
   * lets one through. Where the two engines disagree about whether a stored row is even a number,
   * refusing to answer is the failure a rep can act on.
   */
  if (!/^[+-]?(\d+(\.\d*)?|\.\d+)$/.test(minimum.amount)) return "Unreadable";

  /*
   * The same refusal of thousands separators the C# gets, arrived at from the opposite direction:
   * .NET has to *exclude* `NumberStyles.AllowThousands`, while `decimal.js` rejects `"1,500"` by
   * throwing. Either way a minimum a tenant meant as one and a half must not silently become fifteen
   * hundred. `"NaN"` and `"Infinity"` are the pair `decimal.js` accepts and `isFinite` catches.
   *
   * Both are now also refused by the pattern above — kept because they are the *reason* the parse is
   * guarded at all, and because a pattern is a claim about syntax while these are claims about what
   * the value would mean.
   */
  let threshold: InstanceType<typeof Decimal>;

  try {
    threshold = new Decimal(minimum.amount);
  } catch {
    // Its own answer rather than borrowing the currency one: the write path validates the amount, so
    // reaching here means the stored row is broken, and telling a rep their currencies disagree would
    // send them looking for the wrong thing.
    return "Unreadable";
  }

  if (!threshold.isFinite()) return "Unreadable";

  return total.amount.greaterThanOrEqualTo(threshold) ? "Met" : "NotMet";
}
