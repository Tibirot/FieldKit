import { describe, expect, it } from "vitest";

import { REQUIRED_HUNDREDTHS, sumInHundredths } from "@/lib/api/score-weights";

/**
 * The one piece of arithmetic on the authoring screen (`AUD-07`, `BR-AUD-4`) — W10 slice 8.
 *
 * It exists because the server's check has **no tolerance**: exactly 100, in `decimal`. A screen
 * that disagreed with that check would either refuse a set the server accepts, or accept one it
 * refuses — and the second is worse, because the administrator finds out at Save.
 */
describe("summing a weighting", () => {
  it("agrees with the server on a set whose float sum misses 100", () => {
    /*
     * The case this function exists for, and it took a search to find — which is the point.
     *
     * `0.01 + 64.04 + 35.95` is `100.00000000000001` in float64, so `sum === 100` is false and a
     * naive screen would say "0.00 over" and disable Save on a set the server stores happily. My
     * first version of this test used thirds, on the assumption that `33.34 + 33.33 + 33.33` drifts;
     * it does not — it is exactly 100 — and the test passed against the naive implementation.
     *
     * There are thousands of such triples. An administrator will not know which one they typed.
     */
    const drifting = [
      { percentage: 0.01 },
      { percentage: 64.04 },
      { percentage: 35.95 },
    ];

    expect(sumInHundredths(drifting)).toBe(REQUIRED_HUNDREDTHS);

    // The naive sum this replaces, asserted so the reason is visible rather than claimed.
    expect(drifting.reduce((total, weight) => total + weight.percentage, 0)).not.toBe(100);
  });

  it("agrees with the server on thirds too", () => {
    // Not a float case — `33.34 + 33.33 + 33.33` is exactly 100 — but it is the set a tenant
    // expressing thirds actually types, and it is what `BR-AUD-4`'s "no tolerance" is argued about.
    expect(
      sumInHundredths([{ percentage: 33.34 }, { percentage: 33.33 }, { percentage: 33.33 }]),
    ).toBe(REQUIRED_HUNDREDTHS);
  });

  it("sums what the server will store, not what was typed", () => {
    /*
     * The half that rounding only at the end would miss. The column is `numeric(5,2)`, so `33.335`
     * lands as `33.34` — three of them are `100.02` in the row and `100.005` as typed.
     *
     * Rounding each value first makes the screen's total the row's total. Rounding once at the end
     * would answer `10001` here (100.005 → 100.01), which agrees with the typing and disagrees with
     * the database.
     */
    const rounded = [
      { percentage: 33.335 },
      { percentage: 33.335 },
      { percentage: 33.335 },
    ];

    expect(sumInHundredths(rounded)).toBe(10_002);
    expect(Math.round(33.335 * 3 * 100)).toBe(10_001);
  });

  it("still catches a set that is genuinely wrong", () => {
    // The rule is not "round until it passes". 99.99 is exactly the case `BR-AUD-4` refuses.
    expect(
      sumInHundredths([{ percentage: 33.33 }, { percentage: 33.33 }, { percentage: 33.33 }]),
    ).toBe(9_999);
  });

  it("counts a pillar weighted at nothing", () => {
    // A tenant switching share of shelf off writes 0, and the other two still have to reach 100.
    expect(
      sumInHundredths([{ percentage: 70 }, { percentage: 0 }, { percentage: 30 }]),
    ).toBe(REQUIRED_HUNDREDTHS);
  });
});
