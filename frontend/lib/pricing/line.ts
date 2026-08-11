import { Decimal, Money, percentOf } from "@/lib/pricing/money";
import type { ResolvedPromotion } from "@/lib/pricing/promotion-resolver";

/**
 * What one order line costs, broken into the four numbers a document has to show.
 *
 * `subtotal` is unit price × quantity before any promotion; `net` is what remains after the
 * discount and is what tax is charged on; `total` is `net + tax`.
 */
export type PricedLine = {
  subtotal: Money;
  discount: Money;
  net: Money;
  tax: Money;
  total: Money;
};

/**
 * What a line costs once its promotion and tax are applied (`ORD-02`, `ORD-03`, `BR-ORD-2/3`).
 *
 * **The device half of `LinePricing.cs`**, and the reason it exists is `BR-ORD-2`: a rep prices an
 * order at a counter with no signal, and the server re-prices it on push. Those two must reach the
 * same number, and the only way to hold two implementations to that is a shared corpus both read —
 * `vectors/pricing/line.v1.json`, which lands with this file rather than before it.
 *
 * W6 and W7 mirrored the three *resolvers* — which price, which promotion, which rate. None of them
 * answered what the line costs, so this is the first arithmetic in the pair rather than the fourth
 * selection rule. Every decision it encodes is argued in the C# original; the comments here cover
 * only what is specific to doing it in JavaScript.
 *
 * @param unitPrice What one unit costs, from `resolvePrice`.
 * @param quantity
 *   How many, as a decimal **string** — never a `number`. A quantity can be a weight, and
 *   `0.1 + 0.2` is exactly the arithmetic `Money` exists to keep out of this module.
 * @param promotion The one promotion `resolvePromotion` chose, or null.
 * @param taxPercentage
 *   The rate from `resolveTaxRate`, or null when the tenant has none for this class and country.
 *   **Null is "unknown", not zero** — both yield no tax here, and the caller keeps the distinction.
 */
export function priceLine(
  unitPrice: Money,
  quantity: string,
  promotion: ResolvedPromotion | null,
  taxPercentage: string | null,
): PricedLine {
  const subtotal = unitPrice.multiply(quantity).round();

  let discount = discountOn(subtotal, unitPrice, quantity, promotion).round();

  // Clamped: a fixed amount authored larger than the line it reaches would otherwise drive the net
  // negative, and the order total would fall as the shopkeeper bought more.
  if (discount.amount.greaterThan(subtotal.amount)) discount = subtotal;

  const net = subtotal.subtract(discount).round();
  const taxed = applyRate(net, taxPercentage);

  return { subtotal, discount, net, tax: taxed.tax, total: taxed.total };
}

/** Tax at a rate that may be unknown. Kept separate so the null case reads as a case. */
function applyRate(net: Money, percentage: string | null): { tax: Money; total: Money } {
  if (percentage === null) {
    // `Money.zero` is fine here, unlike in C#: `toWire` formats to the currency's minor units
    // whatever the underlying scale, so a zero always serialises as "0.00" beside a "27.00".
    const nothing = Money.zero(net.currency);

    return { tax: nothing, total: net };
  }

  const tax = percentOf(net, percentage).round();

  // Net plus tax, never net × 1.19 — the two differ once rounding is involved, and only the first
  // shows as numbers that add up. `applyTax` in tax.ts makes the same call.
  return { tax, total: net.add(tax) };
}

function discountOn(
  subtotal: Money,
  unitPrice: Money,
  quantity: string,
  promotion: ResolvedPromotion | null,
): Money {
  if (promotion === null) return Money.zero(subtotal.currency);

  if (promotion.percentOff !== null) {
    return percentOf(subtotal, promotion.percentOff);
  }

  if (promotion.amountOff !== null) {
    // Off the line, not off each unit — "€5 off" on a line of twelve is €5.
    return Money.of(promotion.amountOff, promotion.currency ?? subtotal.currency);
  }

  const bundle = promotion.bundle;

  // A cross-product bundle discounts nothing here: the money belongs to a line this function cannot
  // see, and crediting it against this one would put it against the wrong product for every report
  // downstream. The order-level pass owns it.
  if (bundle == null || bundle.getProductId != null) return Money.zero(subtotal.currency);

  /*
   * Whole bundles only, and the remainder is charged in full.
   *
   * `Decimal.floor` rather than `Math.floor`, which is the JavaScript-specific trap: `Math.floor`
   * takes a `number`, so a quantity that arrived as an exact decimal string would be coerced to a
   * float on the way in — the one conversion this module exists to prevent, on the value that
   * decides how much stock is given away.
   */
  const group = bundle.buyQuantity + bundle.getQuantity;
  const groups = new Decimal(quantity).dividedBy(group).floor();

  if (groups.lessThanOrEqualTo(0)) return Money.zero(subtotal.currency);

  const discounted = groups.times(bundle.getQuantity);

  return percentOf(unitPrice.multiply(discounted), bundle.getPercentOff);
}
