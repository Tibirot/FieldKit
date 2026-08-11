import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import { priceLine } from "@/lib/pricing/line";
import { Money } from "@/lib/pricing/money";
import type { BundleCandidate, ResolvedPromotion } from "@/lib/pricing/promotion-resolver";

/**
 * The shared line vectors, run against the device mirror (`ORD-02`, `ORD-03`, `PRD-08`) — W11
 * slice 2b.
 *
 * The fourth mirror and the second that does **arithmetic** rather than selection — and unlike the
 * first three, this corpus was written *with* its mirror rather than before it. The C# side shipped
 * in 2a with unit tests only, because `scripts/check-vector-readers.mjs` refuses a file only one
 * language reads: a corpus with one reader is a rule proven once and unchecked in the language that
 * actually has to agree.
 *
 * That refusal is the whole point of the file. `BR-ORD-2` says the device and the server produce
 * identical results, and until this test existed there was no arithmetic for them to be identical
 * about — the resolvers agreed on which price, and nothing agreed on the total.
 */
type PromotionVector = {
  kind: "percentOff" | "amountOff" | "bundle";
  percentOff?: string;
  amountOff?: string;
  currency?: string;
  buyQuantity?: number;
  getQuantity?: number;
  getPercentOff?: string;
  getProductId?: string | null;
};

type LineVector = {
  name: string;
  why?: string;
  unitPrice: string;
  currency: string;
  quantity: string;
  promotion: PromotionVector | null;
  taxPercentage: string | null;
  expected: { subtotal: string; discount: string; net: string; tax: string; total: string };
};

type LineFile = { version: number; cases: LineVector[] };

function load(): LineFile {
  const path = fileURLToPath(new URL("../../../vectors/pricing/line.v1.json", import.meta.url));

  return JSON.parse(readFileSync(path, "utf8")) as LineFile;
}

const file = load();

/**
 * The vector's promotion as the resolver would have handed it over.
 *
 * A tiered promotion never appears here: `resolvePromotion` projects the winning tier's discount
 * onto `percentOff`/`amountOff` before anything downstream sees it, so the file carries the three
 * shapes this function actually meets.
 */
function promotionOf(vector: PromotionVector | null): ResolvedPromotion | null {
  if (vector === null) return null;

  const base = {
    promotionId: "00000000-0000-0000-0000-000000000000",
    priority: 0,
    percentOff: null,
    amountOff: null,
    currency: null,
    bundle: null,
  };

  if (vector.kind === "percentOff") {
    return { ...base, type: "PercentOff", percentOff: vector.percentOff! };
  }

  if (vector.kind === "amountOff") {
    return {
      ...base,
      type: "AmountOff",
      amountOff: vector.amountOff!,
      currency: vector.currency ?? null,
    };
  }

  const bundle: BundleCandidate = {
    buyQuantity: vector.buyQuantity!,
    getQuantity: vector.getQuantity!,
    getPercentOff: vector.getPercentOff!,
    getProductId: vector.getProductId ?? null,
  };

  return { ...base, type: "BuyXGetY", bundle };
}

function price(vector: LineVector) {
  return priceLine(
    Money.of(vector.unitPrice, vector.currency),
    vector.quantity,
    promotionOf(vector.promotion),
    vector.taxPercentage,
  );
}

describe("the shared line vectors", () => {
  it.each(file.cases.map((vector) => [vector.name, vector] as const))(
    "%s",
    (_name, vector) => {
      const line = price(vector);

      // Compared as **wire strings**, not as Decimals. A `Decimal` comparison passes when one engine
      // carries 0 and the other 0.00 — which is exactly the scale disagreement this corpus caught in
      // the C# original, and it is invisible to any check that normalises before comparing.
      expect(line.subtotal.toWire()).toBe(vector.expected.subtotal);
      expect(line.discount.toWire()).toBe(vector.expected.discount);
      expect(line.net.toWire()).toBe(vector.expected.net);
      expect(line.tax.toWire()).toBe(vector.expected.tax);
      expect(line.total.toWire()).toBe(vector.expected.total);

      expect(line.total.currency).toBe(vector.currency);
    },
  );

  it("keeps the four numbers adding up, on every case", () => {
    // The property a document depends on: a reader adding the printed net and tax must reach the
    // printed total. Any scheme that carries full precision between steps and rounds once at the end
    // can break this on a case nobody wrote down.
    for (const vector of file.cases) {
      const line = price(vector);

      expect(line.subtotal.subtract(line.discount).toWire()).toBe(line.net.toWire());
      expect(line.net.add(line.tax).toWire()).toBe(line.total.toWire());
    }
  });

  it("never drives a line negative, on every case", () => {
    for (const vector of file.cases) {
      const line = price(vector);

      expect(line.net.amount.greaterThanOrEqualTo(0)).toBe(true);
      expect(line.discount.amount.greaterThan(line.subtotal.amount)).toBe(false);
    }
  });

  it("reads the same file the server does", () => {
    expect(file.version).toBe(1);
    expect(file.cases.length).toBeGreaterThanOrEqual(10);
    expect(new Set(file.cases.map((vector) => vector.name)).size).toBe(file.cases.length);

    // At least one zero-decimal currency. Without it the whole corpus is satisfied by arithmetic
    // hard-coded to two places — the bug `Money.round`'s own docs record having shipped once.
    expect(file.cases.some((vector) => vector.currency === "JPY")).toBe(true);
  });
});

describe("priceLine", () => {
  it("floors the bundle count rather than rounding or ceiling it", () => {
    /*
     * 20.3 units against "buy 2 get 1" is 6.766… groups. Floor gives six free units; **round or
     * ceil would give seven**, which is stock the shopkeeper did not buy enough to earn.
     *
     * The quantity is chosen so the three disagree — at 6.5 units (2.166… groups) floor and round
     * agree, and the corpus case there would pass against either.
     *
     * <b>This test replaced a vacuous one.</b> It first claimed the `Decimal.floor` here prevents a
     * float coercion bug, and the sabotage pass disproved that: `Math.floor(Number(q) / g)` passes
     * every case in this file. Using `Decimal` is still right — nothing in this module touches a
     * float — but it is a consistency rule, not a demonstrated bug, and the test now asserts the
     * thing that is actually checkable.
     */
    const bundle: ResolvedPromotion = {
      promotionId: "00000000-0000-0000-0000-000000000000",
      type: "BuyXGetY",
      priority: 0,
      percentOff: null,
      amountOff: null,
      currency: null,
      bundle: { buyQuantity: 2, getQuantity: 1, getPercentOff: "100.00", getProductId: null },
    };

    const line = priceLine(Money.of("1.00", "EUR"), "20.3", bundle, null);

    // Six whole groups of three in 20.3, so six free units — not seven, and not 6.766…
    expect(line.discount.toWire()).toBe("6.00");
    expect(line.net.toWire()).toBe("14.30");
  });

  it("refuses a quantity that is not a decimal string", () => {
    // `Money.of` throws on NaN and Infinity; the quantity path has to be as strict, or a bad
    // quantity reaches the wire as a total nobody can explain.
    expect(() => priceLine(Money.of("1.00", "EUR"), "twelve", null, null)).toThrow();
  });
});
