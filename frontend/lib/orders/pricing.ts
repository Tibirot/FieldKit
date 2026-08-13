import { priceLine } from "@/lib/pricing/line";
import { Decimal, Money } from "@/lib/pricing/money";
import {
  resolvePrice,
  type PriceCandidate,
  type ResolvedPrice,
} from "@/lib/pricing/price-resolver";
import {
  resolvePromotion,
  type PromotionCandidate,
  type PromotionType,
} from "@/lib/pricing/promotion-resolver";
import { resolveTaxRate } from "@/lib/pricing/tax";
import type { FieldKitDatabase, ReferencePromotion } from "@/lib/sync/db";
import { promotionsFor, taxRatesFor } from "@/lib/sync/reference";

/**
 * What a whole order costs, from what this device holds (`ORD-02`, `ORD-03`) — W11 slice 7d.
 *
 * <b>The device half of `PricingService.cs`</b>, and the last piece `BR-ORD-2` needs: a rep prices an
 * order at a counter with no signal and the server re-prices it on push, and the two have to reach
 * the same number. W6 and W7 mirrored the three *resolvers* and W11 slice 2b mirrored the line
 * arithmetic; nothing gathered the candidates and ran them, so every rule was in place and nothing
 * composed them.
 *
 * <b>Thin, and deliberately boring — the same instruction the C# original carries.</b> Everything
 * that decides anything is a pure function with vectors: `resolvePrice`, `resolvePromotion`,
 * `resolveTaxRate`, `priceLine`. This gathers and hands over. Anything here that reads like a pricing
 * decision is a bug, because the parity vectors cannot see it.
 *
 * <b>Batched, for the reason the server's is.</b> An order is tens of lines and every gather here is
 * an IndexedDB round trip on a path the rep watches change as they type. Prices, promotions and rates
 * are fetched once for the whole set, not once per line.
 *
 * <b>There is still no order-level promotion.</b> `BR-ORD-3` allows one, `B1` calls them "separate
 * and additive", and the model has no such thing — a promotion targets products or categories and
 * `PRD-05` lists four line-level types and no fifth. Slice 2c recorded that on the server; the mirror
 * repeats it rather than inventing the concept on a phone.
 */

/** One line as the screen has it: a product and how many. */
export type LineToPrice = {
  productId: string;
  /** A decimal **string** — a quantity can be a weight, and `0.1 + 0.2` is why. */
  quantity: string;
};

/** One line, priced. Every amount is `Money`, already rounded to the currency's minor units. */
export type PricedOrderLine = {
  productId: string;
  quantity: string;
  unitPrice: Money;
  priceListId: string;
  promotionId: string | null;
  subtotal: Money;
  discount: Money;
  net: Money;
  tax: Money;
  total: Money;
};

/**
 * The whole order, priced.
 *
 * `unpriced` is not an error: a product no list covers on this date is one this outlet cannot be sold
 * today, which is the *screen's* decision to report rather than this function's to refuse. Dropping
 * such a line silently would give a rep a total that quietly omitted something they had added.
 */
export type PricedOrder = {
  /** Empty when nothing priced — there is genuinely no currency to report. */
  currency: string;
  lines: PricedOrderLine[];
  /**
   * Null when `lines` is empty, which is where this shape differs from the C# original.
   *
   * `PricedOrder` in C# leans on `default(Money)` — a zero with an empty currency code. The
   * TypeScript `Money` **refuses** an empty currency, and that is the better rule rather than an
   * inconvenience: a fabricated `"EUR"` on an order that priced nothing is a lie a screen would
   * render as a real total. Null is the same fact stated where a caller has to look at it.
   */
  subtotal: Money | null;
  discount: Money | null;
  net: Money | null;
  tax: Money | null;
  total: Money | null;
  unpriced: string[];
};

/**
 * Prices an order against this device's copy of the tenant's data.
 *
 * Returns **null** when the outlet is not on the device — the mirror of the server's null for an
 * outlet it cannot classify. A rep whose territory changed mid-round can open an order for a shop
 * this device has never pulled, and "I cannot price this" is a different answer from "it costs
 * nothing".
 *
 * @param on The **order's** date as `YYYY-MM-DD`, in the outlet's day (`BR-PRD-6`) — never a clock
 *   this module reads. An order priced today must still resolve to the same numbers when it syncs
 *   on Thursday.
 */
export async function priceOrder(
  db: FieldKitDatabase,
  outletId: string,
  on: string,
  lines: readonly LineToPrice[],
): Promise<PricedOrder | null> {
  const shop = await db.outlets.get(outletId);
  if (!shop) return null;

  const productIds = [...new Set(lines.map((line) => line.productId))];

  const prices = await pricesFor(db, outletId, shop.channelId, on, productIds);
  const promotions = await promotionsByProduct(db, outletId, shop.channelId, on, productIds);
  const rates = await ratesByProduct(db, shop.countryCode, on, productIds);

  const priced: PricedOrderLine[] = [];
  const unpriced: string[] = [];

  for (const line of lines) {
    const price = resolvePrice(prices.get(line.productId) ?? [], on);

    if (price === null) {
      unpriced.push(line.productId);
      continue;
    }

    const unitPrice = Money.of(price.amount, price.currency);

    /*
     * The promotion resolver takes a whole-number quantity and a line carries a decimal.
     *
     * **Truncated, never rounded** — the same call the C# makes. A tier reading "buy 6 or more" is a
     * promise about whole units the shopkeeper has taken, and 5.9 kg has not reached six of
     * anything. Rounding up would hand a tier to an order that never earned it, and the tier's
     * discount then applies to the *whole* line, so the error is not proportional to the rounding.
     *
     * `Decimal.floor` and not `Math.floor`: the latter takes a `number`, which would coerce an exact
     * decimal string to a float on the way in — on the value that decides how much stock is given
     * away.
     */
    const whole = new Decimal(line.quantity).floor().toNumber();

    const promotion = resolvePromotion(promotions.get(line.productId) ?? [], whole, on);
    const rate = rates.get(line.productId) ?? null;

    const computed = priceLine(unitPrice, line.quantity, promotion, rate);

    priced.push({
      productId: line.productId,
      quantity: line.quantity,
      unitPrice,
      priceListId: price.priceListId,
      promotionId: promotion?.promotionId ?? null,
      ...computed,
    });
  }

  return total(priced, unpriced);
}

/**
 * Adds the lines up.
 *
 * <b>Sums of the lines' rounded amounts, never a re-derivation.</b> Each line was already rounded to
 * the currency's minor units so it reads correctly on its own row; recomputing the order from
 * unrounded intermediates gives a total that disagrees with the column above it by a cent or two,
 * which is the one arithmetic error a reader always notices. Same call `save` makes in
 * `local-order.ts`, and the same one `PricingService.Total` makes.
 *
 * <b>The currency falls out of the lines rather than being asserted.</b> There is no cross-currency
 * order (`BR-ORD-7`) — every price here resolved from lists reaching one outlet — and `Money` refuses
 * arithmetic across currencies, so a tenant who had somehow assigned two would get a thrown error
 * naming both rather than a total quietly computed in whichever was seen first.
 */
function total(lines: PricedOrderLine[], unpriced: string[]): PricedOrder {
  // An order whose every line was unpriced has no currency to report, so it has no totals either:
  // `Money` will not be built without one. The ids say which lines caused it.
  if (lines.length === 0) {
    return {
      currency: "",
      lines: [],
      subtotal: null,
      discount: null,
      net: null,
      tax: null,
      total: null,
      unpriced,
    };
  }

  const currency = lines[0].total.currency;
  const sum = (pick: (line: PricedOrderLine) => Money) =>
    lines.reduce((running, line) => running.add(pick(line)), Money.zero(currency));

  return {
    currency,
    lines,
    subtotal: sum((line) => line.subtotal),
    discount: sum((line) => line.discount),
    net: sum((line) => line.net),
    tax: sum((line) => line.tax),
    total: sum((line) => line.total),
    unpriced,
  };
}

/**
 * Every price these products could carry at this outlet, by product.
 *
 * <b>The window is filtered here *and* in the resolver, and neither is redundant.</b> A tenant
 * accumulates price lists for years, and handing all of them to a pure function on a phone would
 * grow the work without bound; the resolver re-checks because it cannot assume its caller filtered,
 * and the vectors hold it to that.
 */
async function pricesFor(
  db: FieldKitDatabase,
  outletId: string,
  channelId: string,
  on: string,
  productIds: readonly string[],
): Promise<Map<string, PriceCandidate[]>> {
  const assignments = [
    ...(await db.priceAssignments.where("outletId").equals(outletId).toArray()),
    ...(await db.priceAssignments.where("channelId").equals(channelId).toArray()),
  ];

  const byProduct = new Map<string, PriceCandidate[]>();

  for (const assignment of assignments) {
    const list = await db.priceLists.get(assignment.priceListId);

    // Half-open, matching `resolvePrice.covers`: a successor list starts the day its predecessor
    // stops, and the changeover day belongs to exactly one of them.
    if (!list || list.effectiveFrom > on) continue;
    if (list.effectiveTo !== null && on >= list.effectiveTo) continue;

    for (const productId of productIds) {
      const priced = await db.priceLines
        .where("[priceListId+productId]")
        .equals([list.id, productId])
        .first();

      if (!priced) continue;

      const candidates = byProduct.get(productId) ?? [];

      candidates.push({
        priceListId: list.id,
        // Which of the two ids is set is the whole of the scope, exactly as it is server-side.
        scope: assignment.outletId === null ? "Channel" : "Outlet",
        currency: list.currency,
        effectiveFrom: list.effectiveFrom,
        effectiveTo: list.effectiveTo,
        amount: priced.amount,
      });

      byProduct.set(productId, candidates);
    }
  }

  return byProduct;
}

/**
 * What each product is *meant* to cost at this shop on this date (`PRD-04`, `BR-AUD-3`) — W11 9b.
 *
 * <b>Exported so the audit's price check does not gather candidates a second time.</b> `AUD-03`
 * compares a shelf price against "the expected price resolved for that outlet/date", which is the
 * same question `priceOrder` asks one line at a time — and a second gatherer would be a second set
 * of half-open window comparisons to keep in step with `resolvePrice.covers`.
 *
 * <b>Absent, not null, for a product no list covers.</b> The caller has to tell "the device says
 * 4.50" from "the device has no opinion": an unpriced product is not a compliance failure, and
 * scoring it as one would punish a rep for a gap in the price list.
 *
 * @param on The **audit's** date as `YYYY-MM-DD`, not today's — a device syncs a week of work.
 */
export async function expectedPrices(
  db: FieldKitDatabase,
  outletId: string,
  on: string,
  productIds: readonly string[],
): Promise<Map<string, ResolvedPrice>> {
  const shop = await db.outlets.get(outletId);
  if (!shop) return new Map();

  const candidates = await pricesFor(db, outletId, shop.channelId, on, [...new Set(productIds)]);
  const resolved = new Map<string, ResolvedPrice>();

  for (const [productId, forProduct] of candidates) {
    const price = resolvePrice(forProduct, on);

    if (price !== null) resolved.set(productId, price);
  }

  return resolved;
}

/**
 * Every promotion these products could carry at this outlet, by product.
 *
 * <b>A promotion reaches a product through its *targets*, and an empty target set reaches
 * nothing.</b> That is the server's rule — `PromotionEndpoints` calls an empty set "a real state, not
 * a refusal: the promotion then discounts nothing", and it is how a deal is taken out of play without
 * editing its window or deleting a record other things point at. Reading it as "everything" would
 * apply every withdrawn promotion to every line, which is the most expensive possible way to be
 * wrong.
 *
 * Targets are matched by product *or* by the product's category, and a promotion reaching one order
 * through two products has to land on both — which is why this returns a map rather than a set of
 * ids.
 */
async function promotionsByProduct(
  db: FieldKitDatabase,
  outletId: string,
  channelId: string,
  on: string,
  productIds: readonly string[],
): Promise<Map<string, PromotionCandidate[]>> {
  const reaching = await promotionsFor(db, outletId, channelId, on);
  if (reaching.length === 0) return new Map();

  const byProduct = new Map<string, PromotionCandidate[]>();

  for (const productId of productIds) {
    const product = await db.products.get(productId);
    if (!product) continue;

    const applicable = reaching.filter((promotion) =>
      promotion.targets.some(
        (target) =>
          target.productId === productId
          || (target.categoryId !== null && target.categoryId === product.categoryId),
      ),
    );

    if (applicable.length > 0) byProduct.set(productId, applicable.map(candidate));
  }

  return byProduct;
}

/** A stored promotion as the resolver wants it. Shape only — no rule is applied here. */
function candidate(promotion: ReferencePromotion): PromotionCandidate {
  return {
    promotionId: promotion.id,
    type: promotion.type as PromotionType,
    priority: promotion.priority,
    validFrom: promotion.validFrom,
    validTo: promotion.validTo,
    percentOff: promotion.percentOff,
    amountOff: promotion.amountOff,
    currency: promotion.currency,
    tiers: promotion.tiers.map((tier) => ({
      minQuantity: tier.minQuantity,
      percentOff: tier.percentOff,
      amountOff: tier.amountOff,
      currency: tier.currency,
    })),
    bundle:
      promotion.buyQuantity !== null
      && promotion.getQuantity !== null
      && promotion.getPercentOff !== null
        ? {
            buyQuantity: promotion.buyQuantity,
            getQuantity: promotion.getQuantity,
            getPercentOff: promotion.getPercentOff,
            getProductId: promotion.getProductId,
          }
        : null,
  };
}

/**
 * The tax percentage each product carries at this outlet, by product.
 *
 * Absent rather than null-valued when unknown, so the caller's `?? null` is the only place the three
 * unknowns collapse — no country on the shop, no tax class on the product, no rate for the pair. All
 * three mean *unknown*, which `priceLine` charges nothing for, and which is not the same fact as a
 * `"0.00"` rate a tenant authored on purpose (W11 slice 7c).
 */
async function ratesByProduct(
  db: FieldKitDatabase,
  countryCode: string | null,
  on: string,
  productIds: readonly string[],
): Promise<Map<string, string>> {
  const byProduct = new Map<string, string>();
  if (!countryCode) return byProduct;

  for (const productId of productIds) {
    const product = await db.products.get(productId);
    if (!product?.taxClassId) continue;

    const rates = await taxRatesFor(db, countryCode, product.taxClassId);

    const resolved = resolveTaxRate(
      rates.map((rate) => ({
        taxRateId: rate.id,
        percentage: rate.percentage,
        effectiveFrom: rate.effectiveFrom,
        effectiveTo: rate.effectiveTo,
      })),
      on,
    );

    if (resolved) byProduct.set(productId, resolved.percentage);
  }

  return byProduct;
}
