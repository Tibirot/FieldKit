using FieldKit.Modules.Audit;
using FieldKit.Modules.Audit.Contracts;
using FieldKit.Modules.Configuration.Contracts;

namespace FieldKit.Server.Tests;

/// <summary>
/// The perfect-store score (<c>AUD-06</c>, <c>BR-AUD-4</c>, <c>BR-AUD-5</c>) — W10 slice 4.
/// </summary>
/// <remarks>
/// <para>
/// A pure function, so every case here is written as numbers in and a number out — no database, no
/// fixture, no HTTP. That is the point of the shape: the same rules run on a phone that is offline,
/// and slice 5 mirrors this file's arithmetic in TypeScript against generated vectors.
/// </para>
/// <para>
/// The case that carries the slice is <see cref="The_worked_example_from_slice_0"/>, which is the
/// 58-versus-83 table from
/// [audits §5](../../docs/product/22-merchandising-and-audits.md) computed rather than argued.
/// </para>
/// </remarks>
public class PerfectStoreScoreTests
{
    private static readonly Guid Product = Guid.CreateVersion7();

    /// <summary>50 / 30 / 20 — the weighting the spec's own worked example uses.</summary>
    private static PillarWeight[] Balanced() =>
    [
        new(ScorePillar.Availability, 50m),
        new(ScorePillar.ShareOfShelf, 30m),
        new(ScorePillar.PriceCompliance, 20m),
    ];

    private static ScoreInputs Inputs(
        IReadOnlyList<AvailabilityLine>? availability = null,
        IReadOnlyList<FacingsLine>? facings = null,
        int? categoryFacings = null,
        IReadOnlyList<PriceLine>? prices = null,
        IReadOnlyList<PillarWeight>? weights = null,
        long tolerance = 0) =>
        new(availability ?? [], facings ?? [], categoryFacings, prices ?? [],
            weights ?? Balanced(), tolerance);

    /// <summary>Availability lines: <paramref name="present"/> found, <paramref name="missing"/> not.</summary>
    private static AvailabilityLine[] Availability(int present, int missing) =>
    [
        .. Enumerable.Range(0, present)
            .Select(_ => new AvailabilityLine(Guid.CreateVersion7(), AvailabilityStatus.Present)),
        .. Enumerable.Range(0, missing)
            .Select(_ => new AvailabilityLine(Guid.CreateVersion7(), AvailabilityStatus.Absent)),
    ];

    private static decimal? PillarOf(PerfectStoreResult result, ScorePillar pillar) =>
        result.Pillars.Single(score => score.Pillar == pillar).Percentage;

    [Fact]
    public void The_worked_example_from_slice_0()
    {
        /*
         * Availability 80 (weight 50), share of shelf **not captured** (weight 30), price 90
         * (weight 20). The audits spec tabulates the two candidate answers:
         *
         *   score the gap zero → 40 + 0 + 18 = 58
         *   renormalise        → (40 + 18) ÷ 0.70 = 82.857… → 82.86
         *
         * Slice 0 chose renormalise, because scoring the gap zero treats "unknown" as "bad" — the
         * faking BR-AUD-2 refuses — and punishes a rep for a measurement they could not take.
         *
         * The doc's table rounds to 83; this asserts the exact stored value. Worth noting rather
         * than smoothing over: the spec is showing a reader the shape of the answer, and the code is
         * the answer.
         */
        var result = PerfectStoreScore.Compute(Inputs(
            availability: Availability(present: 8, missing: 2),
            prices:
            [
                .. Enumerable.Range(0, 9).Select(_ => new PriceLine(Guid.CreateVersion7(), 100, 100, "RON")),
                new PriceLine(Guid.CreateVersion7(), 120, 100, "RON"),
            ]));

        Assert.Equal(80m, PillarOf(result, ScorePillar.Availability));
        Assert.Null(PillarOf(result, ScorePillar.ShareOfShelf));
        Assert.Equal(90m, PillarOf(result, ScorePillar.PriceCompliance));

        Assert.Equal(82.86m, result.Score);

        // …and emphatically not the other candidate.
        Assert.NotEqual(58m, result.Score);
    }

    [Fact]
    public void Every_pillar_measured_is_the_plain_weighted_mean()
    {
        // 100 × 0.5 + 50 × 0.3 + 0 × 0.2 = 65. Nothing renormalises, because nothing was skipped.
        var result = PerfectStoreScore.Compute(Inputs(
            availability: Availability(present: 4, missing: 0),
            facings: [new FacingsLine(Product, 15)],
            categoryFacings: 30,
            prices: [new PriceLine(Product, 120, 100, "RON")]));

        Assert.Equal(100m, PillarOf(result, ScorePillar.Availability));
        Assert.Equal(50m, PillarOf(result, ScorePillar.ShareOfShelf));
        Assert.Equal(0m, PillarOf(result, ScorePillar.PriceCompliance));
        Assert.Equal(65m, result.Score);
    }

    [Fact]
    public void A_skipped_pillar_and_a_zero_one_are_not_the_same_thing()
    {
        /*
         * The distinction the whole renormalisation rule rests on, asserted side by side because it
         * is the one a reader is most likely to collapse.
         *
         * Skipped: the rep could not measure it, so it leaves the denominator — 80 stays 80.
         * Zero:    the rep measured it and found nothing, so it drags — 80 × 0.5 ÷ 0.8 = 50.
         */
        var skipped = PerfectStoreScore.Compute(Inputs(
            availability: Availability(present: 8, missing: 2),
            weights: [new PillarWeight(ScorePillar.Availability, 50m), new PillarWeight(ScorePillar.ShareOfShelf, 30m)]));

        var zero = PerfectStoreScore.Compute(Inputs(
            availability: Availability(present: 8, missing: 2),
            facings: [new FacingsLine(Product, 0)],
            categoryFacings: 30,
            weights: [new PillarWeight(ScorePillar.Availability, 50m), new PillarWeight(ScorePillar.ShareOfShelf, 30m)]));

        Assert.Equal(80m, skipped.Score);
        Assert.Equal(50m, zero.Score);
    }

    [Fact]
    public void Absent_and_out_of_stock_are_both_misses()
    {
        // They mean opposite things to the business — a listing gap versus a replenishment gap —
        // which is why they are stored separately. From the shelf's point of view the product was
        // not there to sell, and splitting them is AUD-09's job, not the score's.
        var result = PerfectStoreScore.Compute(Inputs(availability:
        [
            new AvailabilityLine(Guid.CreateVersion7(), AvailabilityStatus.Present),
            new AvailabilityLine(Guid.CreateVersion7(), AvailabilityStatus.Absent),
            new AvailabilityLine(Guid.CreateVersion7(), AvailabilityStatus.OutOfStock),
            new AvailabilityLine(Guid.CreateVersion7(), AvailabilityStatus.Present),
        ]));

        Assert.Equal(50m, PillarOf(result, ScorePillar.Availability));
    }

    [Fact]
    public void Share_of_shelf_divides_by_the_captured_total_and_not_by_own_facings()
    {
        // BR-AUD-2's whole reason for making the rep count the category separately: summing own
        // facings as the denominator would answer ~100% every time.
        var result = PerfectStoreScore.Compute(Inputs(
            facings: [new FacingsLine(Product, 6), new FacingsLine(Guid.CreateVersion7(), 4)],
            categoryFacings: 40));

        Assert.Equal(25m, PillarOf(result, ScorePillar.ShareOfShelf));
    }

    [Theory]
    [InlineData(null)]  // the rep could not count the category
    [InlineData(0)]     // a category with nothing on the shelf — undefined, not nought
    public void Share_of_shelf_is_skipped_without_a_usable_denominator(int? categoryFacings)
    {
        var result = PerfectStoreScore.Compute(Inputs(
            availability: Availability(present: 1, missing: 0),
            facings: [new FacingsLine(Product, 5)],
            categoryFacings: categoryFacings));

        Assert.Null(PillarOf(result, ScorePillar.ShareOfShelf));

        // …and the score is the availability pillar alone, renormalised.
        Assert.Equal(100m, result.Score);
    }

    [Fact]
    public void Share_of_shelf_is_capped_at_a_hundred()
    {
        /*
         * Own facings above the category total is a miscount — usually the rep counted the
         * competitor shelf and forgot to include their own products in the total. Uncapped it
         * produces a pillar above 100, which drags the whole score above 100 and means nothing to
         * any consumer.
         *
         * The raw numbers are still in the audit, so the miscount stays visible; only the derived
         * percentage is bounded.
         */
        var result = PerfectStoreScore.Compute(Inputs(
            facings: [new FacingsLine(Product, 50)],
            categoryFacings: 30));

        Assert.Equal(100m, PillarOf(result, ScorePillar.ShareOfShelf));
        Assert.Equal(100m, result.Score);
    }

    [Fact]
    public void A_price_with_nothing_expected_leaves_the_denominator_as_well_as_the_numerator()
    {
        /*
         * The load-bearing detail of the price pillar. An unpriced product is a gap in somebody's
         * price list, not a rep's failure — counting it as non-compliant would punish them for it,
         * and counting it as compliant would inflate the pillar.
         *
         * Two of three priced, one compliant → 50%, not 33.33 and not 66.67.
         */
        var result = PerfectStoreScore.Compute(Inputs(prices:
        [
            new PriceLine(Guid.CreateVersion7(), 100, 100, "RON"),
            new PriceLine(Guid.CreateVersion7(), 120, 100, "RON"),
            new PriceLine(Guid.CreateVersion7(), 100, null, "RON"),
        ]));

        Assert.Equal(50m, PillarOf(result, ScorePillar.PriceCompliance));
    }

    [Fact]
    public void An_audit_where_nothing_had_an_expected_price_skips_the_pillar()
    {
        var result = PerfectStoreScore.Compute(Inputs(
            availability: Availability(present: 1, missing: 1),
            prices: [new PriceLine(Product, 100, null, "RON")]));

        Assert.Null(PillarOf(result, ScorePillar.PriceCompliance));
        Assert.Equal(50m, result.Score);
    }

    [Fact]
    public void The_price_tolerance_is_absolute_and_inclusive()
    {
        /*
         * Charging under the expected price is as non-compliant as charging over — an under-price is
         * a margin leak and often an unauthorised promotion, so `Math.Abs` rather than a signed
         * comparison.
         *
         * The boundary is inclusive: a delta *equal* to the tolerance complies. "Within 5 bani"
         * plainly includes 5, and an exclusive bound would make a tenant's stated tolerance one unit
         * tighter than they typed.
         */
        var result = PerfectStoreScore.Compute(Inputs(
            prices:
            [
                new PriceLine(Guid.CreateVersion7(), 105, 100, "RON"),  // +5, exactly the tolerance
                new PriceLine(Guid.CreateVersion7(), 95, 100, "RON"),   // −5, the same the other way
                new PriceLine(Guid.CreateVersion7(), 106, 100, "RON"),  // +6, one past it

                // −10, well past it the other way. This line is what makes the test about
                // `Math.Abs` rather than about a threshold: a signed comparison waves every
                // under-price through, and the first three cases pass either way.
                new PriceLine(Guid.CreateVersion7(), 90, 100, "RON"),
            ],
            tolerance: 5));

        Assert.Equal(50m, PillarOf(result, ScorePillar.PriceCompliance));
    }

    [Fact]
    public void The_default_tolerance_is_exact()
    {
        // The spec's own assumption: tenant-configurable, defaulting to 0. Nothing configures it
        // yet, and the parameter exists so that default is visible rather than buried.
        var result = PerfectStoreScore.Compute(Inputs(prices:
        [
            new PriceLine(Guid.CreateVersion7(), 100, 100, "RON"),
            new PriceLine(Guid.CreateVersion7(), 101, 100, "RON"),
        ]));

        Assert.Equal(50m, PillarOf(result, ScorePillar.PriceCompliance));
    }

    [Fact]
    public void An_audit_that_measured_nothing_scores_null_rather_than_zero()
    {
        // A zero would be a claim about a shop nobody looked at. The ingest path refuses an empty
        // audit, so this is unreachable through it today — asserted anyway, because the scorer is
        // also the device's and slice 5 will drive it with whatever a vector says.
        var result = PerfectStoreScore.Compute(Inputs());

        Assert.Null(result.Score);
        Assert.All(result.Pillars, pillar => Assert.Null(pillar.Percentage));
    }

    [Fact]
    public void Pillars_measured_but_all_weighted_zero_score_null_too()
    {
        /*
         * The other way a score has no basis, and the subtler one. The tenant weighted availability
         * at nothing; the rep measured only availability. Dividing by a total weight of zero would
         * throw, and answering 0 would say "this shop scored nothing" when what happened is that the
         * tenant does not care about the one thing that was measured.
         */
        var result = PerfectStoreScore.Compute(Inputs(
            availability: Availability(present: 5, missing: 0),
            weights:
            [
                new PillarWeight(ScorePillar.Availability, 0m),
                new PillarWeight(ScorePillar.ShareOfShelf, 100m),
            ]));

        Assert.Equal(100m, PillarOf(result, ScorePillar.Availability));
        Assert.Null(result.Score);
    }

    [Fact]
    public void A_pillar_the_weight_set_never_named_is_worth_nothing()
    {
        // Not skipped — measured and disregarded. It contributes zero to the total and stays in the
        // denominator, which is what "the tenant weighted this at nothing" has to mean.
        var result = PerfectStoreScore.Compute(Inputs(
            availability: Availability(present: 10, missing: 0),
            facings: [new FacingsLine(Product, 10)],
            categoryFacings: 100,
            weights: [new PillarWeight(ScorePillar.Availability, 100m)]));

        Assert.Equal(0m, result.Pillars.Single(p => p.Pillar == ScorePillar.ShareOfShelf).Weight);

        // 100 × 1.0 + 10 × 0 ÷ 1.0 = 100. The measured-but-unweighted pillar changes nothing.
        Assert.Equal(100m, result.Score);
    }

    [Fact]
    public void Every_pillar_comes_back_including_the_skipped_ones()
    {
        // AUD-09's breakdown has to be able to say *why* a score is what it is, and "share of shelf
        // was not measured" is the most common answer. Filtering skipped pillars out here would make
        // that impossible to render without recomputing.
        var result = PerfectStoreScore.Compute(Inputs(availability: Availability(present: 1, missing: 0)));

        Assert.Equal(Enum.GetValues<ScorePillar>(), result.Pillars.Select(pillar => pillar.Pillar));
        Assert.Equal(30m, result.Pillars.Single(p => p.Pillar == ScorePillar.ShareOfShelf).Weight);
    }

    [Fact]
    public void Percentages_round_half_up_at_two_places()
    {
        /*
         * BR-PRD-9's policy, which the TypeScript mirror applies identically — a difference of one
         * ulp here is a parity failure in slice 5.
         *
         * 1 of 3 present is 33.333…, and 2 of 3 is 66.666…: one rounds down and one rounds up, which
         * is what makes this a test of the policy rather than of truncation.
         */
        var down = PerfectStoreScore.Compute(Inputs(availability: Availability(present: 1, missing: 2)));
        var up = PerfectStoreScore.Compute(Inputs(availability: Availability(present: 2, missing: 1)));

        Assert.Equal(33.33m, PillarOf(down, ScorePillar.Availability));
        Assert.Equal(66.67m, PillarOf(up, ScorePillar.Availability));
    }

    [Fact]
    public void Half_rounds_away_from_zero_rather_than_to_even()
    {
        /*
         * The case that separates the documented policy from .NET's default, and it needs a real
         * midpoint to do it: 1 of 32 present is exactly 3.125, so half-up answers 3.13 and
         * `MidpointRounding.ToEven` — which is what `Math.Round` does if nobody says otherwise —
         * answers 3.12.
         *
         * The first version of this test used 1 of 8, which is 12.5 and rounds to itself at two
         * places. It asserted the right number for the wrong reason and passed under banker's
         * rounding; the sabotage pass is what found that.
         */
        var result = PerfectStoreScore.Compute(Inputs(
            availability: Availability(present: 1, missing: 31)));

        Assert.Equal(3.13m, PillarOf(result, ScorePillar.Availability));
        Assert.Equal(3.13m, result.Score);
    }

    [Fact]
    public void The_arithmetic_is_exact_in_decimal()
    {
        // A third of a hundred is not representable in binary, and `100.0 / 3` in float64 leaves
        // 33.333333333333336 — which rounds the same way here but does not stay the same through a
        // weighted sum. Asserted directly so the *type* is pinned, not only its effect: this is
        // BR-AUD-5's parity requirement at its narrowest point.
        var result = PerfectStoreScore.Compute(Inputs(
            availability: Availability(present: 1, missing: 2),
            facings: [new FacingsLine(Product, 1)],
            categoryFacings: 3,
            weights:
            [
                new PillarWeight(ScorePillar.Availability, 50m),
                new PillarWeight(ScorePillar.ShareOfShelf, 50m),
            ]));

        // Both pillars are 33.33 after rounding, so the mean is exactly 33.33 with no residue.
        Assert.Equal(33.33m, result.Score);
    }

    [Fact]
    public void The_score_is_computed_from_the_rounded_pillars_so_the_breakdown_reconciles()
    {
        /*
         * A deliberate double-rounding. Availability is 1 of 3 = 33.333…, stored and displayed as
         * 33.33; the score is computed from 33.33, not from 33.333….
         *
         * Rounding the parts before combining them loses a hair of precision. In exchange, the
         * breakdown a supervisor sees beside the total actually adds up to it — and a breakdown whose
         * parts do not reconcile with the whole is a support conversation every single time.
         *
         * 33.33 × 0.5 + 100 × 0.5 = 66.665 → 66.67. From the unrounded 33.333… it would be 66.6666…
         * → 66.67 as well; the assertion that separates them is on the pillar, which is why this test
         * checks both numbers rather than only the score.
         */
        var result = PerfectStoreScore.Compute(Inputs(
            availability: Availability(present: 1, missing: 2),
            facings: [new FacingsLine(Product, 10)],
            categoryFacings: 10,
            weights:
            [
                new PillarWeight(ScorePillar.Availability, 50m),
                new PillarWeight(ScorePillar.ShareOfShelf, 50m),
            ]));

        Assert.Equal(33.33m, PillarOf(result, ScorePillar.Availability));
        Assert.Equal(100m, PillarOf(result, ScorePillar.ShareOfShelf));

        // Exactly what the two displayed numbers give: (33.33 + 100) ÷ 2 = 66.665 → 66.67.
        Assert.Equal(66.67m, result.Score);
    }

    [Fact]
    public void Weights_that_do_not_sum_to_a_hundred_still_renormalise_correctly()
    {
        // BR-AUD-4 has Configuration refuse a set that does not sum to 100, so this should never
        // arrive — but the scorer divides by the measured weight rather than assuming 100, and this
        // is what makes that true rather than incidental. Ten and thirty, availability skipped:
        // 50 × 30 ÷ 30 = 50.
        var result = PerfectStoreScore.Compute(Inputs(
            facings: [new FacingsLine(Product, 5)],
            categoryFacings: 10,
            weights:
            [
                new PillarWeight(ScorePillar.Availability, 10m),
                new PillarWeight(ScorePillar.ShareOfShelf, 30m),
            ]));

        Assert.Equal(50m, result.Score);
    }
}
