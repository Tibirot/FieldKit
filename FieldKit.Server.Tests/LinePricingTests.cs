using FieldKit.Modules.Products;
using FieldKit.SharedKernel;

namespace FieldKit.Server.Tests;

/// <summary>
/// What one order line costs (<c>ORD-02</c>, <c>ORD-03</c>, <c>BR-ORD-2/3</c>) — W11 slice 2a.
/// </summary>
/// <remarks>
/// Pure, so none of this needs a database. The cross-language corpus lives in
/// <c>vectors/pricing/line.v1.json</c> and lands with the TypeScript mirror in slice 2b — the
/// vector-reader gate refuses a corpus only one language reads, and it is right to. These are the
/// cases worth arguing in prose either way.
/// </remarks>
public class LinePricingTests
{
    private static Money Eur(decimal amount) => new(amount, "EUR");

    private static ResolvedPromotion Percent(decimal off) =>
        new(Guid.CreateVersion7(), PromotionType.PercentOff, 10, off, null, null, null);

    private static ResolvedPromotion Amount(decimal off, string currency = "EUR") =>
        new(Guid.CreateVersion7(), PromotionType.FixedAmountOff, 10, null, off, currency, null);

    private static ResolvedPromotion Bundle(int buy, int get, decimal percentOff, Guid? getProduct = null) =>
        new(
            Guid.CreateVersion7(),
            PromotionType.BuyXGetY,
            10,
            null,
            null,
            null,
            new BundleCandidate(buy, get, percentOff, getProduct));

    [Fact]
    public void A_line_with_no_promotion_and_no_tax_is_just_the_multiplication()
    {
        var line = LinePricing.Price(Eur(4.50m), 6m, promotion: null, taxPercentage: null);

        Assert.Equal(Eur(27.00m), line.Subtotal);
        Assert.Equal(Eur(0m), line.Discount);
        Assert.Equal(Eur(27.00m), line.Net);
        Assert.Equal(Eur(0m), line.Tax);
        Assert.Equal(Eur(27.00m), line.Total);
    }

    [Fact]
    public void An_unknown_tax_rate_yields_no_tax_rather_than_a_confident_zero()
    {
        // Null is "this tenant has no rate for that class and country", which is a different fact
        // from "the rate is 0%". Both give no tax here; the distinction matters to the caller,
        // which is why the parameter is nullable rather than defaulted.
        var unknown = LinePricing.Price(Eur(10m), 1m, null, taxPercentage: null);
        var zeroRated = LinePricing.Price(Eur(10m), 1m, null, taxPercentage: 0m);

        Assert.Equal(Eur(0m), unknown.Tax);
        Assert.Equal(Eur(0m), zeroRated.Tax);
    }

    [Fact]
    public void Tax_is_charged_on_the_discounted_net_not_the_subtotal()
    {
        /*
         * The rule the whole ordering exists for. 10 × €10 = €100, less 20% = €80, VAT at 19% on
         * €80 = €15.20. Taxing the subtotal would give €19.00 — charging the shopkeeper for €20
         * nobody paid.
         */
        var line = LinePricing.Price(Eur(10m), 10m, Percent(20m), 19m);

        Assert.Equal(Eur(100.00m), line.Subtotal);
        Assert.Equal(Eur(20.00m), line.Discount);
        Assert.Equal(Eur(80.00m), line.Net);
        Assert.Equal(Eur(15.20m), line.Tax);
        Assert.Equal(Eur(95.20m), line.Total);
    }

    [Fact]
    public void A_fixed_amount_comes_off_the_line_not_off_each_unit()
    {
        // Both readings are defensible and they differ by the quantity, so this pins the one a
        // shopkeeper hears: "€5 off" on a line of twelve is €5, not €60.
        var line = LinePricing.Price(Eur(2.00m), 12m, Amount(5m), null);

        Assert.Equal(Eur(24.00m), line.Subtotal);
        Assert.Equal(Eur(5.00m), line.Discount);
        Assert.Equal(Eur(19.00m), line.Net);
    }

    [Fact]
    public void A_fixed_amount_larger_than_the_line_gives_the_line_away_rather_than_paying_the_shop()
    {
        /*
         * "€5 off" against one unit of a €3 product. Unclamped the line lands at −€2 and the order
         * total *falls* as the shopkeeper buys more.
         *
         * Refusing would be the other option and is worse: the rep did not choose this promotion and
         * cannot edit it, so a refusal strands the order — exactly what BR-ORD-9 exists to avoid.
         */
        var line = LinePricing.Price(Eur(3.00m), 1m, Amount(5m), 19m);

        Assert.Equal(Eur(3.00m), line.Discount);
        Assert.Equal(Eur(0m), line.Net);
        Assert.Equal(Eur(0m), line.Tax);
        Assert.Equal(Eur(0m), line.Total);
    }

    [Fact]
    public void A_bundle_discounts_whole_groups_and_charges_the_remainder_in_full()
    {
        /*
         * Buy 2 get 1 free, on a line of 14: four complete groups of three, so four units free and
         * the two left over are paid for. Rounding the groups up would give away stock the
         * shopkeeper did not buy enough to earn.
         *
         * 14 × €3 = €42 subtotal; 4 free units at 100% off = €12.
         */
        var line = LinePricing.Price(Eur(3.00m), 14m, Bundle(buy: 2, get: 1, percentOff: 100m), null);

        Assert.Equal(Eur(42.00m), line.Subtotal);
        Assert.Equal(Eur(12.00m), line.Discount);
        Assert.Equal(Eur(30.00m), line.Net);
    }

    [Fact]
    public void A_bundle_the_quantity_does_not_reach_discounts_nothing()
    {
        // Two units against "buy 2 get 1" is not one group — the group is three.
        var line = LinePricing.Price(Eur(3.00m), 2m, Bundle(2, 1, 100m), null);

        Assert.Equal(Eur(0m), line.Discount);
        Assert.Equal(Eur(6.00m), line.Net);
    }

    [Fact]
    public void A_fractional_quantity_earns_only_the_whole_bundles_it_reaches()
    {
        // Where a weight-based unit meets a count-based promotion: 6.5 kg against "buy 2 get 1"
        // earns the same as 6 kg, because half a free kilo is not what the author meant.
        var whole = LinePricing.Price(Eur(4.00m), 6m, Bundle(2, 1, 100m), null);
        var fractional = LinePricing.Price(Eur(4.00m), 6.5m, Bundle(2, 1, 100m), null);

        Assert.Equal(Eur(8.00m), whole.Discount);
        Assert.Equal(Eur(8.00m), fractional.Discount);

        // …but the fractional line still costs more, because the extra half is charged.
        Assert.Equal(Eur(26.00m), fractional.Subtotal);
        Assert.Equal(Eur(18.00m), fractional.Net);
    }

    [Fact]
    public void A_cross_product_bundle_discounts_nothing_on_this_line()
    {
        /*
         * "Buy six of these, get one of *those* free." The discount belongs to a line this function
         * cannot see — a different product, possibly not even on the order — and BR-ORD-3 calls the
         * line-level promotion line-level.
         *
         * Zero here is the honest answer; the order-level pass (slice 2c) is what applies it. The
         * failure this prevents is quietly crediting the wrong line, which would balance the order
         * total while putting the money against the wrong product for every report downstream.
         */
        var line = LinePricing.Price(
            Eur(5.00m), 12m, Bundle(6, 1, 100m, getProduct: Guid.CreateVersion7()), null);

        Assert.Equal(Eur(0m), line.Discount);
        Assert.Equal(Eur(60.00m), line.Net);
    }

    [Fact]
    public void Every_step_rounds_half_up_rather_than_to_even()
    {
        /*
         * BR-PRD-9. 3 × €3.335 = €10.005, which is a half-cent — banker's rounding gives €10.00 and
         * half-up gives €10.01. .NET's default is banker's, so this is the case that catches a
         * `Math.Round` written without thinking.
         *
         * Chosen deliberately as a value where the two disagree: at €10.015 they would agree, and
         * the test would pass against either policy.
         */
        var line = LinePricing.Price(Eur(3.335m), 3m, null, null);

        Assert.Equal(Eur(10.01m), line.Subtotal);
    }

    [Fact]
    public void The_four_numbers_add_up_exactly_as_printed()
    {
        /*
         * The property a document depends on, and the reason each step rounds rather than the total
         * carrying full precision to the end. A reader adding the printed net and the printed tax
         * must reach the printed total — a residue of a tenth of a cent is a support call.
         */
        var line = LinePricing.Price(Eur(1.99m), 7m, Percent(12.5m), 19m);

        Assert.Equal(line.Subtotal - line.Discount, line.Net);
        Assert.Equal(line.Net + line.Tax, line.Total);
    }

    [Fact]
    public void A_discount_never_makes_the_line_negative_whatever_the_promotion()
    {
        // The clamp, asserted as a property over the shapes rather than only on the one case above.
        ResolvedPromotion[] generous =
        [
            Percent(150m),
            Amount(999m),
            Bundle(1, 5, 100m),
        ];

        foreach (var promotion in generous)
        {
            var line = LinePricing.Price(Eur(2.00m), 3m, promotion, 19m);

            Assert.True(line.Net.Amount >= 0m, $"{promotion.Type} drove the net negative.");
            Assert.True(line.Discount.Amount <= line.Subtotal.Amount);
        }
    }
}
