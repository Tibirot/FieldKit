import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import { Money } from "@/lib/pricing/money";
import {
  checkOrderMinimum,
  resolveOrderMinimum,
  type OrderMinimumCandidate,
  type OrderMinimumVerdict,
  type ResolvedOrderMinimum,
} from "@/lib/pricing/order-minimum";

/**
 * The order-minimum rule on the device (`ORD-06`, `BR-ORD-5`) — W11 slice 8b-ii.
 *
 * **The gap this file used to name is closed** (W11½ R7, regression F3): `vectors/pricing/order-minimum.v1.json`
 * is read at the bottom of this file and by `OrderMinimumVectorTests` on the server. It found a real
 * divergence on its first run — see the note there.
 *
 * The hand-written cases below stay, and are not redundant with it. They mirror the C# resolver's
 * documented behaviour clause by clause and they exercise shapes the file cannot carry — a `null`
 * total, which the C# signature has no way to express and the device needs for an order whose lines
 * have not priced.
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

/**
 * The shared corpus, read from the same file the C# engine reads (`PRD-08`) — W11½ R7.
 *
 * The gap named at the top of this file, closed. `BR-ORD-5` is the only rule in the module with **no
 * server-side gate** — the device refuses the submission, because that is where a rep can still add
 * a line — so nothing downstream would ever notice the two engines disagreeing.
 *
 * **Writing the file found a disagreement**, which is the only evidence a vector file is worth
 * anything. `.NET` parses the stored amount with `AllowDecimalPoint | AllowLeadingSign`, which
 * excludes exponents and hexadecimal; `decimal.js` reads `"1e2"` as 100 and `"0x10"` as 16. So a
 * phone would have called an order **Met** against a minimum the server cannot read at all.
 */
type ResolutionVector = {
  name: string;
  candidates: OrderMinimumCandidate[];
  expected: ResolvedOrderMinimum | null;
};

type CheckVector = {
  name: string;
  minimum: { currencyCode: string; amount: string } | null;
  total: { amount: string; currency: string };
  expected: OrderMinimumVerdict;
};

type OrderMinimumFile = {
  version: number;
  resolution: ResolutionVector[];
  check: CheckVector[];
};

const vectors = JSON.parse(
  readFileSync(
    fileURLToPath(new URL("../../../vectors/pricing/order-minimum.v1.json", import.meta.url)),
    "utf8",
  ),
) as OrderMinimumFile;

describe("the shared order-minimum vectors", () => {
  it("loads the file the C# engine reads", () => {
    // Guards the wiring, not the engine. If the path breaks or the file empties, the `it.each`
    // blocks below silently become zero tests — a green suite that checked nothing.
    expect(vectors.version).toBe(1);
    expect(vectors.resolution.length).toBeGreaterThanOrEqual(5);
    expect(vectors.check.length).toBeGreaterThanOrEqual(10);
  });

  it("carries every amount as a string, never a JSON number", () => {
    /*
     * The format rule `vectors/README.md` states, enforced here because this is the language it
     * exists to protect: `JSON.parse` turns a bare `500.00` into an IEEE-754 double before the
     * engine under test sees it.
     *
     * It matters twice over in this file. Several cases carry amounts that are *deliberately* not
     * numbers — `"1e2"`, `"0x10"`, `"1,500"` — and a JSON number would be the one thing that could
     * not express them.
     */
    const amounts = [
      ...vectors.resolution.flatMap((vector) => [
        ...vector.candidates.map((candidate) => candidate.amount as unknown),
        ...(vector.expected ? [vector.expected.amount as unknown] : []),
      ]),
      ...vectors.check.flatMap((vector) => [
        ...(vector.minimum ? [vector.minimum.amount as unknown] : []),
        vector.total.amount as unknown,
      ]),
    ];

    for (const amount of amounts) expect(typeof amount).toBe("string");
  });

  it.each(vectors.resolution.map((vector) => [vector.name, vector] as const))(
    "resolution: %s",
    (_name, vector) => {
      const resolved = resolveOrderMinimum(vector.candidates);

      // "No minimum" is an answer rather than an absence — a file of positive cases only would let
      // a resolver that always returns something pass.
      if (vector.expected === null) {
        expect(resolved).toBeNull();
        return;
      }

      expect(resolved).toEqual(vector.expected);
    },
  );

  it.each(vectors.check.map((vector) => [vector.name, vector] as const))(
    "check: %s",
    (_name, vector) => {
      const minimum: ResolvedOrderMinimum | null =
        vector.minimum === null
          ? null
          : {
              // The id and scope play no part in the check, so the file does not carry them here —
              // a case that named them would imply they mattered.
              orderMinimumId: "00000000-0000-0000-0000-000000000000",
              scope: "Outlet",
              currencyCode: vector.minimum.currencyCode,
              amount: vector.minimum.amount,
            };

      const total = Money.of(vector.total.amount, vector.total.currency);

      expect(checkOrderMinimum(minimum, total)).toBe(vector.expected);
    },
  );
});
