using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products;

/// <summary>
/// What one order line costs, broken into the four numbers an invoice has to show.
/// </summary>
/// <param name="Subtotal">Unit price × quantity, before any promotion.</param>
/// <param name="Discount">
/// What the promotion took off. Zero at the currency's scale when none applied, never negative.
/// </param>
/// <param name="Net">
/// <see cref="Subtotal"/> − <see cref="Discount"/> — the taxable amount, and the one a promotion is
/// judged by.
/// </param>
/// <param name="Tax">Tax on <see cref="Net"/>, or zero when no rate is known.</param>
/// <param name="Total">
/// <see cref="Net"/> + <see cref="Tax"/>. Addition rather than a second multiplication, for the
/// reason <see cref="TaxEngine.Apply"/> gives: the two differ once rounding is involved, and only the
/// first shows as three consistent numbers on a document.
/// </param>
public sealed record PricedLine(Money Subtotal, Money Discount, Money Net, Money Tax, Money Total);

/// <summary>
/// What a line costs once its promotion and tax are applied (<c>ORD-02</c>, <c>ORD-03</c>,
/// <c>BR-ORD-2/3</c>) — W11 slice 2a.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the half of the pricing engine that did not exist.</b> W6 and W7 built three resolvers
/// and mirrored them in TypeScript — they answer <i>which</i> price, <i>which</i> promotion and
/// <i>which</i> tax rate apply. None of them answers <i>so what does this line cost</i>, and neither
/// language had a function that did. The W11 plan called slice 2 "line pricing through the existing
/// engine"; the pricing part was there, the arithmetic was not.
/// </para>
/// <para>
/// <b>Pure and side-effect-free</b> (<c>BR-PRD-7</c>), like the resolvers: no database, no clock, no
/// tenant. That is what lets a device compute a total at a counter with no signal and the server
/// reach the same number on push — and it is why the TypeScript mirror (slice 2b) can be held to this
/// by shared vectors rather than by review.
/// </para>
/// <para>
/// <b>Discount before tax, and rounding at each step.</b> A promotion reduces what is being sold, so
/// it lands before the state takes its share; taxing the undiscounted subtotal would charge a
/// shopkeeper for money nobody paid. Each intermediate is rounded to the currency's minor units by
/// <see cref="Money.Round"/> (<c>BR-PRD-9</c>, half-up) rather than carried at full precision and
/// rounded once at the end: a line is a number a human reads on a document, and the four here must
/// add up exactly as printed.
/// </para>
/// </remarks>
public static class LinePricing
{
    /// <summary>
    /// Prices one line.
    /// </summary>
    /// <param name="unitPrice">What one unit costs, from <see cref="PriceResolver"/>.</param>
    /// <param name="quantity">
    /// How many, in the line's own unit of measure. <b>Decimal</b>, because a unit of measure can be
    /// a weight — half a kilo of loose produce is an order line rather than a rounding error — and
    /// percentage and fixed-amount promotions scale over it without trouble. The two that need whole
    /// units say so where they are applied.
    /// </param>
    /// <param name="promotion">
    /// The one promotion <see cref="PromotionResolver"/> chose, or null. At most one per line
    /// (<c>BR-ORD-3</c>) — the resolver has already settled the contest, and a tiered promotion
    /// arrives with its winning tier's discount projected onto
    /// <see cref="ResolvedPromotion.PercentOff"/>/<see cref="ResolvedPromotion.AmountOff"/>, so this
    /// sees one uniform shape rather than a tier table.
    /// </param>
    /// <param name="taxPercentage">
    /// The rate from <see cref="TaxEngine.Resolve"/>, or null when this tenant has none for the
    /// product's class and country. <b>Null is "unknown", not zero</b> — the same distinction
    /// <c>TaxEndpoints</c> makes — and an unknown rate yields no tax rather than a confident zero.
    /// </param>
    public static PricedLine Price(
        Money unitPrice, decimal quantity, ResolvedPromotion? promotion, decimal? taxPercentage)
    {
        var subtotal = (unitPrice * quantity).Round();

        /*
         * Rounded here rather than trusting each branch of `DiscountOn` to do it.
         *
         * The no-promotion path returns `Money.Zero`, which is `0m` at scale zero — every other
         * amount on the line carries the currency's minor units, so the four numbers came back with
         * inconsistent scale and a zero discount printed as "0" beside a subtotal of "27.00". The
         * shared vectors caught it: they compare the decimal *and* its string form precisely so a
         * scale difference cannot pass as equal, which is the difference between a mirror that
         * agrees and one that merely rounds to the same place.
         */
        var discount = DiscountOn(subtotal, unitPrice, quantity, promotion).Round();

        /*
         * Clamped, and this is the one guard worth having rather than trusting authoring.
         *
         * A fixed-amount promotion is authored as a flat sum — "€5 off" — and nothing stops a rep
         * ordering one unit of a €3 product it targets. Without the clamp the line lands at −€2, the
         * order total goes down when a shopkeeper buys more, and the number reaches an invoice.
         * Refusing instead would strand an order over a promotion the rep did not choose and cannot
         * edit, which BR-ORD-9 exists to avoid; giving the line away for nothing is the answer that
         * loses no work.
         */
        if (discount.Amount > subtotal.Amount) discount = subtotal;

        var net = (subtotal - discount).Round();
        var taxed = TaxEngine.Apply(net, taxPercentage ?? 0m);

        return new PricedLine(subtotal, discount, taxed.Net, taxed.Tax, taxed.Gross);
    }

    /// <summary>
    /// What the promotion takes off, before clamping.
    /// </summary>
    /// <remarks>
    /// <b>A cross-product bundle returns nothing here, deliberately.</b> "Buy six of these, get one
    /// of <i>those</i> free" puts the discount on a line this function cannot see — a different
    /// product, possibly not even on the order. `BR-ORD-3` calls the line-level promotion
    /// "line-level", and a discount that belongs to another line is by definition not. It is the
    /// order-level pass's to apply (slice 2c), and returning zero here is what keeps this function
    /// honest rather than quietly crediting the wrong line.
    /// </remarks>
    private static Money DiscountOn(
        Money subtotal, Money unitPrice, decimal quantity, ResolvedPromotion? promotion)
    {
        if (promotion is null) return Nothing(subtotal);

        if (promotion.PercentOff is { } percent)
        {
            return new Money(subtotal.Amount * percent / 100m, subtotal.Currency).Round();
        }

        if (promotion.AmountOff is { } amount)
        {
            /*
             * Off the *line*, not off each unit.
             *
             * Both readings are defensible and they differ by a factor of the quantity, so it is
             * worth being explicit: "€5 off" on a line of twelve is €5, not €60. That matches how a
             * shopkeeper hears it, and it is the reading that makes a fixed-amount promotion
             * bounded — per-unit would make the discount grow without limit as the order grows,
             * which is what a *percentage* is for.
             */
            return new Money(amount, promotion.Currency ?? subtotal.Currency).Round();
        }

        if (promotion.Bundle is { } bundle && bundle.GetProductId is null)
        {
            /*
             * Buy X get Y at Z% off, on the same product.
             *
             * Whole bundles only, and the fractional remainder is charged in full: a line of 14
             * against "buy 2 get 1" is four complete groups of three, so four units are discounted
             * and the two left over are not. Rounding the groups up would give away stock the
             * shopkeeper did not buy enough to earn.
             *
             * `Math.Floor` on the decimal quantity is where a weight-based unit meets a
             * count-based promotion: 6.5 kg against "buy 2 get 1" earns the same as 6 kg, because
             * half a free kilo is not a thing an author of this promotion meant.
             */
            var group = bundle.BuyQuantity + bundle.GetQuantity;
            var groups = Math.Floor(quantity / group);

            if (groups <= 0) return Nothing(subtotal);

            var discounted = groups * bundle.GetQuantity;

            return new Money(
                unitPrice.Amount * discounted * bundle.GetPercentOff / 100m,
                subtotal.Currency).Round();
        }

        return Nothing(subtotal);
    }

    /// <summary>
    /// Zero, carrying the same scale as the line it belongs to.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Money.Zero"/>, which is <c>0m</c> at scale zero. Every other amount on the
    /// line arrives at the currency&#39;s minor units, so an unscaled zero printed as <c>0</c> beside a
    /// subtotal of <c>27.00</c> — and <see cref="Money.Round"/> cannot fix it, because rounding a
    /// decimal never <i>adds</i> trailing places.
    /// <para>
    /// The shared vectors are what caught it: they compare the decimal <b>and</b> its string form,
    /// precisely so a scale difference cannot pass as equality. A TypeScript mirror formatting to the
    /// currency&#39;s minor units would have produced <c>0.00</c> and disagreed with this engine over a
    /// number that is arithmetically identical.
    /// </para>
    /// </remarks>
    private static Money Nothing(Money like) =>
        new(new decimal(0, 0, 0, isNegative: false, (byte)like.MinorUnits), like.Currency);
}
