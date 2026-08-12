import "fake-indexeddb/auto";

import { afterEach, describe, expect, it } from "vitest";

import { Decimal, Money } from "@/lib/pricing/money";
import { applyTax, resolveTaxRate, type TaxRateCandidate } from "@/lib/pricing/tax";

import { closeDatabase, FieldKitDatabase, type ReferenceTaxRate } from "./db";
import { applyTaxRateChanges, TAX_RATES, taxRatesFor, watermark } from "./reference";

/**
 * Tax rates on the device (`OFF-03`, `PRD-07`) — W11 slice 7b.
 *
 * The engine has been here since W7 slice 14 and the rates have been on the server since W6 slice 13;
 * nothing carried one to the other. What these tests cover is the join: that a rate survives the trip
 * as something `decimal.js` can read exactly, that the lookup narrows to the jurisdiction the rep is
 * standing in, and that the window arrives intact so `resolveTaxRate` can still answer a question
 * about last Tuesday.
 */
function freshDatabase(): FieldKitDatabase {
  return new FieldKitDatabase(`test:${crypto.randomUUID()}`);
}

function rate(
  id: string,
  overrides: Partial<ReferenceTaxRate> = {},
): ReferenceTaxRate {
  return {
    id,
    taxClassId: "standard",
    countryCode: "RO",
    percentage: "19.00",
    effectiveFrom: "2026-01-01",
    effectiveTo: null,
    rowVersion: 1,
    ...overrides,
  };
}

function page<T>(upserts: T[], cursor: number) {
  return { upserts, tombstones: [], cursor };
}

/** What `resolveTaxRate` takes, from what the store holds. The shape the device actually uses. */
function candidates(rates: readonly ReferenceTaxRate[]): TaxRateCandidate[] {
  return rates.map((each) => ({
    taxRateId: each.id,
    percentage: each.percentage,
    effectiveFrom: each.effectiveFrom,
    effectiveTo: each.effectiveTo,
  }));
}

afterEach(() => {
  closeDatabase();
});

describe("tax rates on the device", () => {
  it("stores a rate and advances its own watermark", async () => {
    const db = freshDatabase();

    await applyTaxRateChanges(db, page([rate("r1", { rowVersion: 12 })], 12));

    expect((await db.taxRates.get("r1"))?.percentage).toBe("19.00");
    expect(await watermark(db, TAX_RATES)).toBe(12);
  });

  it("carries the percentage as a string a decimal can read exactly", async () => {
    /*
     * The load-bearing property of the whole slice, and the one W11 slice 7a was opened for after the
     * price feed was found sending money as JSON numbers.
     *
     * Asserted on the *type* as well as the value: a round-tripped `19.75` compares equal by `==` and
     * is still wrong, because `decimal.js` handed a float has already lost whatever the float lost.
     */
    const db = freshDatabase();

    await applyTaxRateChanges(db, page([rate("r1", { percentage: "19.75" })], 1));

    const stored = (await db.taxRates.get("r1"))!;

    expect(typeof stored.percentage).toBe("string");
    expect(new Decimal(stored.percentage).equals("19.75")).toBe(true);

    // And through the engine, because a string that never reaches `applyTax` proves nothing. 8.10 at
    // 19.75% is exactly 1.59975, which is a half-up rounding decided in the fifth decimal place —
    // and gross is net *plus* tax rather than net × 1.1975, so the three numbers add up on screen.
    const taxed = applyTax(Money.of("8.10", "RON"), stored.percentage);

    expect(taxed.tax.toString()).toBe("1.60 RON");
    expect(taxed.gross.toString()).toBe("9.70 RON");
  });

  it("narrows to the country and class, and is not fussy about the country's case", async () => {
    /*
     * The compound index is what keeps a tenant selling in four countries from resolving against all
     * four. Upper-casing on the way in mirrors the server, which stores `RO` whatever an author
     * typed — a lookup keyed on an outlet's `ro` would otherwise silently find nothing, which reads
     * as "no rate authored" rather than as a bug.
     */
    const db = freshDatabase();

    await applyTaxRateChanges(
      db,
      page(
        [
          rate("ro-standard"),
          rate("ro-reduced", { taxClassId: "reduced", percentage: "9.00" }),
          rate("bg-standard", { countryCode: "BG", percentage: "20.00" }),
        ],
        3,
      ),
    );

    expect((await taxRatesFor(db, "RO", "standard")).map((each) => each.id)).toEqual([
      "ro-standard",
    ]);

    expect((await taxRatesFor(db, "ro", "standard")).map((each) => each.id)).toEqual([
      "ro-standard",
    ]);

    expect(await taxRatesFor(db, "RO", "zero-rated")).toEqual([]);
  });

  it("keeps an expired rate, so an order dated before a VAT change still resolves", async () => {
    /*
     * Why the window is not filtered in the query. `BR-PRD-6` resolves against the *order's* date,
     * and a device syncs a week's work at a time — a rate that ended on Monday is exactly what
     * Sunday's order needs. Filtering to "in force today" here would make the device disagree with
     * the server about an order neither of them thinks is unusual.
     */
    const db = freshDatabase();

    await applyTaxRateChanges(
      db,
      page(
        [
          rate("old", { percentage: "19.00", effectiveFrom: "2026-01-01", effectiveTo: "2026-07-01" }),
          rate("new", { percentage: "21.00", effectiveFrom: "2026-07-01" }),
        ],
        2,
      ),
    );

    const held = candidates(await taxRatesFor(db, "RO", "standard"));

    expect(held).toHaveLength(2);

    // Half-open at both ends: the changeover day belongs to the new rate and to nothing else.
    expect(resolveTaxRate(held, "2026-06-30")?.percentage).toBe("19.00");
    expect(resolveTaxRate(held, "2026-07-01")?.percentage).toBe("21.00");
  });

  it("drops a rate the server tombstoned", async () => {
    /*
     * Not an edge case here. The server's PUT replaces a class's rates wholesale, because a rate's
     * identity is its country and start date together — so an author correcting a date deletes and
     * recreates, and a device that only ever upserted would keep resolving against a rate its tenant
     * abolished. `resolveTaxRate` picks the latest `effectiveFrom` that applies, so a stale row with
     * a later start date wins outright.
     */
    const db = freshDatabase();

    await applyTaxRateChanges(db, page([rate("r1", { percentage: "19.00" })], 1));

    await applyTaxRateChanges(db, {
      upserts: [rate("r2", { percentage: "21.00", rowVersion: 2 })],
      tombstones: [{ id: "r1", rowVersion: 2 }],
      cursor: 2,
    });

    const held = await taxRatesFor(db, "RO", "standard");

    expect(held.map((each) => each.id)).toEqual(["r2"]);
    expect(resolveTaxRate(candidates(held), "2026-08-12")?.percentage).toBe("21.00");
  });
});
