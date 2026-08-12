import { describe, expect, it } from "vitest";

import { Money } from "@/lib/pricing/money";
import {
  checkOrderMinimum,
  resolveOrderMinimum,
  type OrderMinimumCandidate,
} from "@/lib/pricing/order-minimum";

/**
 * The order-minimum rule on the device (`ORD-06`, `BR-ORD-5`) — W11 slice 8b-ii.
 *
 * **Hand-written rather than vector-driven, and that is a gap this slice names rather than hides.**
 * The three resolvers beside this one are checked by `vectors/pricing/*.json`, read by both languages,
 * because a rule implemented twice drifts. This one is implemented twice too — `OrderMinimumResolver`
 * on the server resolves the same candidates — so it wants the same corpus, which is its own slice
 * (the C# reader is a class of its own, and this file would become a second reader of it).
 *
 * Until then the cases below deliberately mirror the C# resolver's documented behaviour clause by
 * clause: outlet beats channel, the tie is broken by lower-cased id, a mismatched currency reports
 * rather than compares, and an unparseable amount is its own answer.
 */
function candidate(overrides: Partial<OrderMinimumCandidate> = {}): OrderMinimumCandidate {
  return {
    orderMinimumId: "00000000-0000-0000-0000-000000000001",
    scope: "Channel",
    currencyCode: "RON",
    amount: "150.00",
    ...overrides,
  };
}

describe("resolveOrderMinimum", () => {
  it("has no minimum when nothing is configured, rather than one of zero", () => {
    /*
     * The ordinary case, and what `BR-ORD-5`'s "if configured" is about. Most tenants will never set
     * one, so *absent* has to be a first-class answer — and it has to read as "every order passes"
     * rather than as a threshold of nothing, which is what a zero would look like to a caller.
     */
    expect(resolveOrderMinimum([])).toBeNull();
  });

  it("prefers the outlet's own minimum over its channel's", () => {
    const resolved = resolveOrderMinimum([
      candidate({ orderMinimumId: "z-channel", scope: "Channel", amount: "500.00" }),
      candidate({ orderMinimumId: "a-outlet", scope: "Outlet", amount: "50.00" }),
    ]);

    // The ids are chosen so the *tiebreak* would pick the other one: `z-channel` sorts above
    // `a-outlet`, so a resolver that ignored scope and fell through to the id comparison would
    // return the channel's 500 and this test would still look like it was about precedence.
    expect(resolved?.orderMinimumId).toBe("a-outlet");
    expect(resolved?.amount).toBe("50.00");
  });

  it("breaks a tie at the same scope by the lower-cased id", () => {
    /*
     * Not correctness — two minimums at one scope is a data problem no tiebreak fixes. What it buys
     * is that this device, every other device, and the server refuse the same order.
     *
     * Upper case on one id is the case that matters: in ASCII `'A'–'F' < 'a'–'f'`, so comparing raw
     * would make the winner depend on how somebody typed a GUID.
     */
    const resolved = resolveOrderMinimum([
      candidate({ orderMinimumId: "0195F000-AAAA", amount: "10.00" }),
      candidate({ orderMinimumId: "0195f000-bbbb", amount: "20.00" }),
    ]);

    expect(resolved?.orderMinimumId).toBe("0195f000-bbbb");
  });

  it("keeps the amount the string it arrived as", () => {
    // Resolution selects; it does not do arithmetic. Parsing here would mean re-formatting on the
    // way out, and `"12.50"` becoming `"12.5"` is a different answer to show a rep.
    expect(resolveOrderMinimum([candidate({ amount: "12.50" })])?.amount).toBe("12.50");
  });
});

describe("checkOrderMinimum", () => {
  it("passes every order when no minimum applies", () => {
    expect(checkOrderMinimum(null, Money.of("0.01", "RON"))).toBe("None");
  });

  it("meets the minimum exactly at the threshold", () => {
    // `>=`, not `>`. "Must be met" is met by meeting it, and an off-by-one here refuses an order for
    // exactly the amount an administrator typed as acceptable.
    const minimum = resolveOrderMinimum([candidate({ amount: "150.00" })]);

    expect(checkOrderMinimum(minimum, Money.of("150.00", "RON"))).toBe("Met");
    expect(checkOrderMinimum(minimum, Money.of("149.99", "RON"))).toBe("NotMet");
    expect(checkOrderMinimum(minimum, Money.of("150.01", "RON"))).toBe("Met");
  });

  it("reports a currency disagreement instead of comparing the numbers", () => {
    /*
     * The failure this verdict exists for. 50 EUR against a 200 RON threshold is comfortably over in
     * value and far under by its digits — comparing them would refuse orders a rep is entitled to
     * send, and it would look exactly like the rule working.
     */
    const minimum = resolveOrderMinimum([candidate({ amount: "200.00", currencyCode: "RON" })]);

    expect(checkOrderMinimum(minimum, Money.of("50.00", "EUR"))).toBe("CurrencyMismatch");
    expect(checkOrderMinimum(minimum, Money.of("5000.00", "EUR"))).toBe("CurrencyMismatch");
  });

  it("ignores the case of the currency code", () => {
    // `Money` upper-cases what it is given and the wire is upper-case, so this can only differ if a
    // row was written by something that did not — a refusal-to-answer on `ron` vs `RON` would be a
    // rep sent to their supervisor about nothing.
    const minimum = resolveOrderMinimum([candidate({ currencyCode: "ron", amount: "10.00" })]);

    expect(checkOrderMinimum(minimum, Money.of("20.00", "RON"))).toBe("Met");
  });

  it("cannot decide when the order has not priced", () => {
    // The device's own case, which the C# signature has no way to express: a screen whose lines have
    // no total yet. Refusing is the safe half — the order stays, and stays editable.
    const minimum = resolveOrderMinimum([candidate()]);

    expect(checkOrderMinimum(minimum, null)).toBe("Unreadable");
  });

  it("cannot decide on a stored amount that is not a decimal", () => {
    /*
     * A broken row rather than a small order, and its own answer for that reason. `"1,500"` is the
     * case worth naming: `decimal.js` throws on it and .NET's `NumberStyles` deliberately excludes
     * thousands separators, so neither language silently reads one and a half as fifteen hundred.
     */
    for (const amount of ["", "twelve", "1,500", "NaN", "Infinity"]) {
      const minimum = resolveOrderMinimum([candidate({ amount })]);

      expect(checkOrderMinimum(minimum, Money.of("9999.00", "RON"))).toBe("Unreadable");
    }
  });
});
