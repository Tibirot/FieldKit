import "fake-indexeddb/auto";

import { afterEach, describe, expect, it } from "vitest";

import { priceOrder } from "@/lib/orders/pricing";
import {
  closeDatabase,
  FieldKitDatabase,
  type ReferenceOutlet,
  type ReferencePriceAssignment,
  type ReferencePriceLine,
  type ReferencePriceList,
  type ReferenceProduct,
  type ReferencePromotion,
  type ReferencePromotionAssignment,
  type ReferenceTaxRate,
} from "@/lib/sync/db";
import {
  applyOutletChanges,
  applyPriceAssignmentChanges,
  applyPriceLineChanges,
  applyPriceListChanges,
  applyProductChanges,
  applyPromotionAssignmentChanges,
  applyPromotionChanges,
  applyTaxRateChanges,
} from "@/lib/sync/reference";

/**
 * What a whole order costs on the device (`ORD-02`, `ORD-03`) — W11 slice 7d.
 *
 * The mirror of `PricingService.cs`, and what these tests are about is the **gathering**: the pure
 * rules already have vectors that check both languages against the same file, so re-asserting "60%
 * of 100 is 40" here would test `priceLine` a second time and the composition not at all.
 *
 * So: which candidates reach a line, which do not, and what happens when one of them is missing.
 */
const OUTLET = "outlet-1";
const CHANNEL = "channel-1";
const TODAY = "2026-08-12";

function freshDatabase(): FieldKitDatabase {
  return new FieldKitDatabase(`test:${crypto.randomUUID()}`);
}

function page<T>(upserts: T[], cursor = 1) {
  return { upserts, tombstones: [], cursor };
}

function shop(countryCode: string | null = "RO"): ReferenceOutlet {
  return {
    id: OUTLET,
    code: "SHOP-1",
    name: "Corner Shop",
    channelId: CHANNEL,
    segment: null,
    status: "Active",
    countryCode,
    latitude: null,
    longitude: null,
    radiusMetres: 150,
    rowVersion: 1,
  };
}

function item(id: string, overrides: Partial<ReferenceProduct> = {}): ReferenceProduct {
  return {
    id,
    sku: id.toUpperCase(),
    name: `Product ${id}`,
    brandId: null,
    categoryId: null,
    taxClassId: null,
    unitOfMeasure: "EA",
    packSize: 24,
    status: "Active",
    rowVersion: 1,
    ...overrides,
  };
}

function list(
  id: string,
  effectiveFrom = "2026-01-01",
  effectiveTo: string | null = null,
): ReferencePriceList {
  return { id, name: id, currency: "RON", effectiveFrom, effectiveTo, rowVersion: 1 };
}

function priceRow(priceListId: string, productId: string, amount: string): ReferencePriceLine {
  return { id: `${priceListId}:${productId}`, priceListId, productId, amount, rowVersion: 1 };
}

function assignment(
  id: string,
  priceListId: string,
  scope: "outlet" | "channel",
): ReferencePriceAssignment {
  return {
    id,
    priceListId,
    channelId: scope === "channel" ? CHANNEL : null,
    outletId: scope === "outlet" ? OUTLET : null,
    rowVersion: 1,
  };
}

function promotion(
  id: string,
  overrides: Partial<ReferencePromotion> = {},
): ReferencePromotion {
  return {
    id,
    name: id,
    type: "PercentOff",
    percentOff: "10.00",
    amountOff: null,
    currency: null,
    buyQuantity: null,
    getQuantity: null,
    getPercentOff: null,
    getProductId: null,
    validFrom: "2026-01-01",
    validTo: null,
    priority: 1,
    targets: [],
    tiers: [],
    rowVersion: 1,
    ...overrides,
  };
}

function promotionAssignment(
  id: string,
  promotionId: string,
  scope: "outlet" | "channel" = "channel",
): ReferencePromotionAssignment {
  return {
    id,
    promotionId,
    channelId: scope === "channel" ? CHANNEL : null,
    outletId: scope === "outlet" ? OUTLET : null,
    rowVersion: 1,
  };
}

function rate(percentage: string, taxClassId = "standard"): ReferenceTaxRate {
  return {
    id: `rate:${taxClassId}:${percentage}`,
    taxClassId,
    countryCode: "RO",
    percentage,
    effectiveFrom: "2026-01-01",
    effectiveTo: null,
    rowVersion: 1,
  };
}

/** A device with one shop, one product, one channel list pricing it at 10.00, and no tax. */
async function stocked(db: FieldKitDatabase) {
  await applyOutletChanges(db, page([shop()]));
  await applyProductChanges(db, page([item("product-1")]));
  await applyPriceListChanges(db, page([list("channel-list")]));
  await applyPriceLineChanges(db, page([priceRow("channel-list", "product-1", "10.00")]));
  await applyPriceAssignmentChanges(db, page([assignment("a1", "channel-list", "channel")]));
}

afterEach(() => {
  closeDatabase();
});

describe("pricing an order on the device", () => {
  it("prices a line from the list reaching this outlet, and totals it", async () => {
    const db = freshDatabase();
    await stocked(db);

    const priced = (await priceOrder(db, OUTLET, TODAY, [
      { productId: "product-1", quantity: "3" },
    ]))!;

    expect(priced.currency).toBe("RON");
    expect(priced.lines[0].unitPrice.toString()).toBe("10.00 RON");
    expect(priced.lines[0].priceListId).toBe("channel-list");
    expect(priced.subtotal?.toString()).toBe("30.00 RON");
    expect(priced.total?.toString()).toBe("30.00 RON");
    expect(priced.unpriced).toEqual([]);
  });

  it("prefers the outlet's own list over its channel's", async () => {
    /*
     * `BR-PRD-2`'s precedence, and this test moved here from `manager.test.ts` in slice 7d.
     *
     * The device used to answer this with `priceListFor`, which picked the first assignment the
     * index handed back — right by *ordering*, since outlet assignments were queried first. That is
     * a second implementation of a rule the shared `resolvePrice` already owns, and it disagreed
     * with it whenever two lists tied: `resolvePrice` breaks a tie by the later `effectiveFrom` and
     * then by id, so the server and the device could pick different lists and neither would look
     * wrong. This path goes through the resolver, so there is one answer.
     *
     * <b>The ids are chosen so that only the scope can produce the right answer.</b> The first
     * version of this test named them `channel-list` and `outlet-list`, with the same start date —
     * so the id tiebreak alone put `outlet-list` on top, and sabotaging the scope to a constant left
     * the test green. Named `z-…` and `a-…`, the tiebreak now points the *wrong* way and the
     * assertion has nothing to lean on but `BR-PRD-2`.
     */
    const db = freshDatabase();

    await applyOutletChanges(db, page([shop()]));
    await applyProductChanges(db, page([item("product-1")]));
    await applyPriceListChanges(db, page([list("z-channel-list"), list("a-outlet-list")]));
    await applyPriceLineChanges(
      db,
      page([
        priceRow("z-channel-list", "product-1", "10.00"),
        priceRow("a-outlet-list", "product-1", "8.00"),
      ]),
    );
    await applyPriceAssignmentChanges(
      db,
      page([
        assignment("a1", "z-channel-list", "channel"),
        assignment("a2", "a-outlet-list", "outlet"),
      ]),
    );

    const priced = (await priceOrder(db, OUTLET, TODAY, [
      { productId: "product-1", quantity: "1" },
    ]))!;

    expect(priced.lines[0].priceListId).toBe("a-outlet-list");
    expect(priced.lines[0].unitPrice.toString()).toBe("8.00 RON");
  });

  it("prices for the order's day, not the day the device synced", async () => {
    // A rep offline for a week may be pricing an order on the day a new list takes over. Half-open
    // at both ends, so the changeover day belongs to exactly one list.
    const db = freshDatabase();

    await applyOutletChanges(db, page([shop()]));
    await applyProductChanges(db, page([item("product-1")]));
    await applyPriceListChanges(
      db,
      page([list("old", "2026-01-01", "2026-06-01"), list("new", "2026-06-01")]),
    );
    await applyPriceLineChanges(
      db,
      page([priceRow("old", "product-1", "10.00"), priceRow("new", "product-1", "12.00")]),
    );
    await applyPriceAssignmentChanges(
      db,
      page([assignment("a1", "old", "channel"), assignment("a2", "new", "channel")]),
    );

    const before = await priceOrder(db, OUTLET, "2026-05-31", [
      { productId: "product-1", quantity: "1" },
    ]);
    const after = await priceOrder(db, OUTLET, "2026-06-01", [
      { productId: "product-1", quantity: "1" },
    ]);

    expect(before!.lines[0].unitPrice.toString()).toBe("10.00 RON");
    expect(after!.lines[0].unitPrice.toString()).toBe("12.00 RON");
  });

  it("reports a product no list prices rather than dropping it", async () => {
    /*
     * The line still exists on the rep's screen. Dropping it silently would hand back a total that
     * omitted something they had added — and a rep reading a plausible number has no way to notice
     * a line is missing from a sum.
     */
    const db = freshDatabase();
    await stocked(db);

    await applyProductChanges(db, page([item("product-2")], 2));

    const priced = (await priceOrder(db, OUTLET, TODAY, [
      { productId: "product-1", quantity: "1" },
      { productId: "product-2", quantity: "1" },
    ]))!;

    expect(priced.lines).toHaveLength(1);
    expect(priced.unpriced).toEqual(["product-2"]);
    expect(priced.total?.toString()).toBe("10.00 RON");
  });

  it("has no totals at all when nothing could be priced", async () => {
    /*
     * Where this differs from the C# original, deliberately. `PricedOrder` there leans on
     * `default(Money)` — a zero with an empty currency; the TypeScript `Money` refuses to be built
     * without one, and a fabricated `"RON"` on an order that priced nothing is a lie a screen would
     * render as a real total.
     */
    const db = freshDatabase();
    await applyOutletChanges(db, page([shop()]));
    await applyProductChanges(db, page([item("product-1")]));

    const priced = (await priceOrder(db, OUTLET, TODAY, [
      { productId: "product-1", quantity: "1" },
    ]))!;

    expect(priced.currency).toBe("");
    expect(priced.total).toBeNull();
    expect(priced.unpriced).toEqual(["product-1"]);
  });

  it("answers null for a shop this device has never pulled", async () => {
    // The mirror of the server's null for an outlet it cannot classify. A rep whose territory
    // changed mid-round can reach a shop the device has not seen, and "I cannot price this" is a
    // different answer from "it costs nothing".
    const db = freshDatabase();
    await stocked(db);

    expect(await priceOrder(db, "outlet-unknown", TODAY, [])).toBeNull();
  });
});

describe("what reaches a line", () => {
  it("applies a promotion that targets the product", async () => {
    const db = freshDatabase();
    await stocked(db);

    await applyPromotionChanges(
      db,
      page([promotion("promo-1", { targets: [{ productId: "product-1", categoryId: null }] })]),
    );
    await applyPromotionAssignmentChanges(db, page([promotionAssignment("pa-1", "promo-1")]));

    const priced = (await priceOrder(db, OUTLET, TODAY, [
      { productId: "product-1", quantity: "2" },
    ]))!;

    expect(priced.lines[0].promotionId).toBe("promo-1");
    expect(priced.discount?.toString()).toBe("2.00 RON");
    expect(priced.total?.toString()).toBe("18.00 RON");
  });

  it("applies one that targets the product's category", async () => {
    // The second way a promotion reaches a line, and the reason gathering returns a map: a deal
    // reaching one order through two products has to land on both.
    const db = freshDatabase();
    await stocked(db);

    await applyProductChanges(db, page([item("product-1", { categoryId: "water" })], 2));
    await applyPromotionChanges(
      db,
      page([promotion("promo-1", { targets: [{ productId: null, categoryId: "water" }] })]),
    );
    await applyPromotionAssignmentChanges(db, page([promotionAssignment("pa-1", "promo-1")]));

    const priced = (await priceOrder(db, OUTLET, TODAY, [
      { productId: "product-1", quantity: "1" },
    ]))!;

    expect(priced.lines[0].promotionId).toBe("promo-1");
  });

  it("applies nothing for a promotion with no targets at all", async () => {
    /*
     * **The comment in `db.ts` said the opposite until this slice**, and it would have been believed:
     * this is the first device code that reads `targets`. The server is unambiguous —
     * `PromotionEndpoints` calls an empty target set "a real state, not a refusal: the promotion then
     * discounts nothing", and it is how a deal is withdrawn without editing its window.
     *
     * Reading it as "everything" applies every withdrawn promotion to every line, which is the most
     * expensive possible way to be wrong and would break `BR-ORD-2` on exactly the number the rep
     * and the shopkeeper shook hands on.
     */
    const db = freshDatabase();
    await stocked(db);

    await applyPromotionChanges(db, page([promotion("promo-1", { targets: [] })]));
    await applyPromotionAssignmentChanges(db, page([promotionAssignment("pa-1", "promo-1")]));

    const priced = (await priceOrder(db, OUTLET, TODAY, [
      { productId: "product-1", quantity: "1" },
    ]))!;

    expect(priced.lines[0].promotionId).toBeNull();
    expect(priced.discount?.toString()).toBe("0.00 RON");
  });

  it("ignores a promotion assigned to neither this outlet nor its channel", async () => {
    const db = freshDatabase();
    await stocked(db);

    await applyPromotionChanges(
      db,
      page([promotion("promo-1", { targets: [{ productId: "product-1", categoryId: null }] })]),
    );

    const priced = (await priceOrder(db, OUTLET, TODAY, [
      { productId: "product-1", quantity: "1" },
    ]))!;

    expect(priced.lines[0].promotionId).toBeNull();
  });

  it("charges the tax the shop's country and the product's class agree on", async () => {
    // The join W11 slice 7c built, arriving where it was always going: the line the rep reads.
    const db = freshDatabase();
    await stocked(db);

    await applyProductChanges(db, page([item("product-1", { taxClassId: "standard" })], 2));
    await applyTaxRateChanges(db, page([rate("19.00")]));

    const priced = (await priceOrder(db, OUTLET, TODAY, [
      { productId: "product-1", quantity: "2" },
    ]))!;

    expect(priced.net?.toString()).toBe("20.00 RON");
    expect(priced.tax?.toString()).toBe("3.80 RON");
    expect(priced.total?.toString()).toBe("23.80 RON");
  });

  it("charges no tax, and does not fail, when the shop has no country", async () => {
    // Unknown, not zero — and the two produce the same total, which is why the distinction lives
    // with the caller rather than in the arithmetic (`PRD-07`, W11 slice 7c).
    const db = freshDatabase();
    await stocked(db);

    await applyOutletChanges(db, page([shop(null)], 2));
    await applyProductChanges(db, page([item("product-1", { taxClassId: "standard" })], 2));
    await applyTaxRateChanges(db, page([rate("19.00")]));

    const priced = (await priceOrder(db, OUTLET, TODAY, [
      { productId: "product-1", quantity: "2" },
    ]))!;

    expect(priced.tax?.toString()).toBe("0.00 RON");
    expect(priced.total?.toString()).toBe("20.00 RON");
  });

  it("truncates a fractional quantity before choosing a tier, never rounds it", async () => {
    /*
     * The one arithmetic decision this file makes, and it is a copy of the C# one.
     *
     * A tier reading "buy 6 or more" is a promise about whole units the shopkeeper has taken, and
     * 5.9 kg has not reached six of anything. Rounding up would hand a tier to an order that never
     * earned it — and the tier's discount then applies to the **whole** line, so the error is not
     * proportional to the rounding.
     */
    const db = freshDatabase();
    await stocked(db);

    await applyPromotionChanges(
      db,
      page([
        promotion("promo-1", {
          type: "VolumeTiered",
          percentOff: null,
          targets: [{ productId: "product-1", categoryId: null }],
          tiers: [{ minQuantity: 6, percentOff: "50.00", amountOff: null, currency: null }],
        }),
      ]),
    );
    await applyPromotionAssignmentChanges(db, page([promotionAssignment("pa-1", "promo-1")]));

    const under = (await priceOrder(db, OUTLET, TODAY, [
      { productId: "product-1", quantity: "5.9" },
    ]))!;
    const over = (await priceOrder(db, OUTLET, TODAY, [
      { productId: "product-1", quantity: "6" },
    ]))!;

    expect(under.lines[0].promotionId).toBeNull();
    expect(over.lines[0].promotionId).toBe("promo-1");
  });

  it("sums the lines' rounded amounts rather than re-deriving the order", async () => {
    /*
     * `BR-PRD-9`. Three lines that each round *up* by half a minor unit: 0.125 × 1 is 0.13 apiece,
     * so the honest total is 0.39. Re-deriving from unrounded intermediates gives 0.375 → 0.38 —
     * a total that disagrees with the column above it, which is the one arithmetic error a reader
     * always notices.
     */
    const db = freshDatabase();

    await applyOutletChanges(db, page([shop()]));
    await applyProductChanges(
      db,
      page([item("product-1"), item("product-2"), item("product-3")]),
    );
    await applyPriceListChanges(db, page([list("channel-list")]));
    await applyPriceLineChanges(
      db,
      page([
        priceRow("channel-list", "product-1", "0.125"),
        priceRow("channel-list", "product-2", "0.125"),
        priceRow("channel-list", "product-3", "0.125"),
      ]),
    );
    await applyPriceAssignmentChanges(db, page([assignment("a1", "channel-list", "channel")]));

    const priced = (await priceOrder(db, OUTLET, TODAY, [
      { productId: "product-1", quantity: "1" },
      { productId: "product-2", quantity: "1" },
      { productId: "product-3", quantity: "1" },
    ]))!;

    expect(priced.lines.map((line) => line.total.toString())).toEqual([
      "0.13 RON",
      "0.13 RON",
      "0.13 RON",
    ]);

    expect(priced.total?.toString()).toBe("0.39 RON");
  });
});
