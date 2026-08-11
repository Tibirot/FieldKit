using FieldKit.Modules.Audit.Contracts;
using FieldKit.Modules.Configuration.Contracts;

namespace FieldKit.Modules.Audit;

/// <summary>What one pillar is worth, as the scorer is told.</summary>
/// <remarks>
/// Taken as a parameter rather than read from Configuration, because the weights that matter are the
/// ones the audit was scored against (<c>BR-AUD-8</c>) — a version number the audit records, not
/// whatever is published today. Keeping the lookup outside the function is what makes that possible
/// and what keeps the function pure.
/// </remarks>
public sealed record PillarWeight(ScorePillar Pillar, decimal Percentage);

/// <summary>Everything the score is computed from.</summary>
/// <param name="CategoryFacings">
/// The share-of-shelf denominator, or null when the rep could not count it (<c>BR-AUD-2</c>).
/// </param>
/// <param name="PriceToleranceMinorUnits">
/// How far a shelf price may sit from the expected one and still comply (<c>BR-AUD-3</c>). The
/// spec's own assumption is tenant-configurable with a default of <b>0</b>, and there is no tenant
/// setting for it yet — so callers pass <c>0</c> and the parameter exists to stop that becoming a
/// constant buried in the arithmetic.
/// </param>
public sealed record ScoreInputs(
    IReadOnlyList<AvailabilityLine> Availability,
    IReadOnlyList<FacingsLine> Facings,
    int? CategoryFacings,
    IReadOnlyList<PriceLine> Prices,
    IReadOnlyList<PillarWeight> Weights,
    long PriceToleranceMinorUnits = 0);

/// <summary>
/// One pillar's contribution.
/// </summary>
/// <param name="Percentage">
/// <c>0</c>–<c>100</c>, or <b>null</b> when the pillar was skipped — which is not the same as zero
/// (W10 slice 0). A skipped pillar is renormalised away; a zero one drags the score down.
/// </param>
/// <param name="Weight">What the weight set said it was worth, whether or not it was measured.</param>
public sealed record PillarScore(ScorePillar Pillar, decimal? Percentage, decimal Weight);

/// <summary>
/// A perfect-store score, with the working shown.
/// </summary>
/// <param name="Score">
/// <c>0</c>–<c>100</c>, or null when nothing could be scored — see
/// <see cref="PerfectStoreScore.Compute"/> for the two ways that happens.
/// </param>
/// <param name="Pillars">
/// Every pillar the weight set named, in a fixed order, including the skipped ones. Returned rather
/// than filtered because <c>AUD-09</c>'s breakdown has to be able to say <i>why</i> a score is what
/// it is, and "share of shelf was not measured" is the most common answer.
/// </param>
public sealed record PerfectStoreResult(decimal? Score, IReadOnlyList<PillarScore> Pillars);

/// <summary>
/// The perfect-store score (<c>AUD-06</c>, <c>BR-AUD-4</c>, <c>BR-AUD-5</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure</b>, like <c>Geofencing</c>, <c>PriceResolver</c> and <c>JourneyGenerator</c>: measurements
/// and weights in, a score out. It has to run identically on a phone that is offline and on this
/// server (<c>BR-AUD-5</c>), so it cannot touch a database — and slice 5 mirrors it in TypeScript
/// against generated vectors, which only works if the whole thing is a function.
/// </para>
/// <para>
/// <b><c>decimal</c> throughout, never <c>double</c>.</b> The same discipline pricing uses
/// (<c>BR-PRD-8</c>), for a sharper reason here: a share-of-shelf ratio is a division, and
/// <c>7 / 30</c> in float64 is where the phone's answer and the server's start to differ in the
/// fourth decimal place — which survives into a weighted sum and out to a supervisor as two systems
/// disagreeing about one shelf.
/// </para>
/// <para>
/// <b>Skipped pillars are renormalised away, not scored zero</b> — the decision taken in W10 slice 0
/// and argued in [audits §5](../../docs/product/22-merchandising-and-audits.md). Scoring the gap zero
/// treats "unknown" as "bad", which is exactly the faking <c>BR-AUD-2</c> refuses, and punishes a rep
/// for a measurement they could not take.
/// </para>
/// <para>
/// <b>Nothing here reads Configuration or Products.</b> The weights arrive as a parameter, because
/// the ones that matter are the version the audit recorded (<c>BR-AUD-8</c>) rather than whatever is
/// published today.
/// </para>
/// </remarks>
public static class PerfectStoreScore
{
    /// <summary>
    /// Where percentages are rounded, and it is the same everywhere on purpose.
    /// </summary>
    /// <remarks>
    /// Two places, half-up (away from zero) — the policy <c>BR-PRD-9</c> documents and
    /// <see cref="FieldKit.SharedKernel.Money.Round"/> already applies. The TypeScript mirror applies
    /// the identical policy; a difference of one ulp here is a parity failure in slice 5.
    /// </remarks>
    private const int Decimals = 2;

    /// <summary>The order pillars are always reported in — the enum's, so it cannot drift.</summary>
    private static readonly ScorePillar[] Order = Enum.GetValues<ScorePillar>();

    /// <summary>
    /// Scores an audit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The score is the weighted mean over the pillars that <i>were</i> measured:
    /// <c>Σ(pillar × weight) ÷ Σ(weight of measured pillars)</c>.
    /// </para>
    /// <para>
    /// It is <b>null</b> in two cases, and they are different in kind. Nothing was measured at all —
    /// there is no score to give, and a <c>0</c> would be a claim about a shop nobody looked at.
    /// Or every pillar that was measured is weighted zero — the tenant has said it does not care
    /// about the things this audit happened to record, so a score would be a number with no basis.
    /// Both come back as null rather than zero for the same reason a skipped pillar does.
    /// </para>
    /// </remarks>
    public static PerfectStoreResult Compute(ScoreInputs inputs)
    {
        var measured = new Dictionary<ScorePillar, decimal?>
        {
            [ScorePillar.Availability] = AvailabilityPercentage(inputs),
            [ScorePillar.ShareOfShelf] = ShareOfShelfPercentage(inputs),
            [ScorePillar.PriceCompliance] = PriceCompliancePercentage(inputs),
        };

        var pillars = Order
            .Select(pillar => new PillarScore(pillar, measured[pillar], WeightOf(inputs, pillar)))
            .ToList();

        /*
         * The weighted mean, computed from the *rounded* pillar percentages.
         *
         * Rounding the parts before combining them loses a hair of precision, and it is deliberate:
         * `AUD-09` shows a supervisor the pillar breakdown beside the total, and a breakdown whose
         * parts do not reconcile with the whole is a support conversation every single time. Scoring
         * from the same numbers the screen displays makes the arithmetic checkable by hand.
         *
         * It also makes the parity vectors stronger — slice 5 compares the intermediate pillar
         * values as well as the total, so a mirror that rounds in a different place is caught rather
         * than absorbed.
         */
        var scored = pillars.Where(pillar => pillar.Percentage is not null).ToList();

        var weight = scored.Sum(pillar => pillar.Weight);

        if (scored.Count == 0 || weight == 0m) return new PerfectStoreResult(null, pillars);

        var total = scored.Sum(pillar => pillar.Percentage!.Value * pillar.Weight);

        return new PerfectStoreResult(Round(total / weight), pillars);
    }

    /// <summary>
    /// How much of the outlet's MSL was on the shelf (<c>AUD-01</c>, <c>BR-AUD-1</c>).
    /// </summary>
    /// <remarks>
    /// <b>Only <see cref="AvailabilityStatus.Present"/> counts.</b> Absent and out-of-stock are both
    /// misses — they mean opposite things to the business, which is why they are stored separately,
    /// but from the shelf's point of view the product was not there to sell. Splitting them into two
    /// pillars is a reporting question (<c>AUD-09</c>), not a scoring one.
    /// </remarks>
    private static decimal? AvailabilityPercentage(ScoreInputs inputs)
    {
        // No availability checks is a skipped pillar, not a score of zero: the rep did not fail to
        // find the products, they were not asked to look.
        if (inputs.Availability.Count == 0) return null;

        var present = inputs.Availability.Count(line => line.Status == AvailabilityStatus.Present);

        return Round(100m * present / inputs.Availability.Count);
    }

    /// <summary>
    /// Own facings over the total category facings (<c>AUD-02</c>, <c>BR-AUD-2</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The denominator is the captured category total, never the sum of own facings</b> — that
    /// would always be ~100%, which is the whole reason <c>BR-AUD-2</c> makes the rep count it
    /// separately.
    /// </para>
    /// <para>
    /// <b>Skipped in three ways, and all three are "the rep could not measure it".</b> No total
    /// captured; no facings captured; or a total of zero, which is a category with nothing on the
    /// shelf at all and a ratio that is undefined rather than nought.
    /// </para>
    /// </remarks>
    private static decimal? ShareOfShelfPercentage(ScoreInputs inputs)
    {
        if (inputs.CategoryFacings is not { } category || category <= 0) return null;

        if (inputs.Facings.Count == 0) return null;

        var own = inputs.Facings.Sum(line => (decimal)line.Facings);

        /*
         * Capped at 100.
         *
         * Own facings above the category total is a miscount — most often the rep counted the
         * competitor shelf and forgot to include their own products in the total. Left uncapped it
         * produces a pillar above 100, which drags the *whole score* above 100 and means nothing to
         * any consumer.
         *
         * Capping hides the miscount, so it is worth being explicit: the raw numbers are still in
         * the audit and still visible in the breakdown, and this only bounds the derived percentage.
         * The alternative — refusing the audit — was rejected at ingest for the reason every refusal
         * there is: a measurement the rep took is a fact, and a server that argues with facts teaches
         * reps to enter whatever gets accepted.
         */
        return Math.Min(100m, Round(100m * own / category));
    }

    /// <summary>
    /// How many shelf prices matched the expected one (<c>AUD-03</c>, <c>BR-AUD-3</c>).
    /// </summary>
    /// <remarks>
    /// <b>Only prices with an expectation count, and that is the load-bearing detail.</b> A product
    /// the device could resolve no price for is not a compliance failure — the gap is in somebody's
    /// price list, and scoring it against the rep would punish them for it. So an unpriced product
    /// leaves the denominator as well as the numerator, and an audit where <i>nothing</i> had an
    /// expected price skips the pillar entirely.
    /// </remarks>
    private static decimal? PriceCompliancePercentage(ScoreInputs inputs)
    {
        var comparable = inputs.Prices
            .Where(line => line.ExpectedMinorUnits is not null)
            .ToList();

        if (comparable.Count == 0) return null;

        // Absolute, so charging under the expected price is as non-compliant as charging over. That
        // is the right default for FMCG — an under-price is a margin leak and often an unauthorised
        // promotion — and if a tenant ever wants one-sided tolerance it belongs in the same setting
        // the tolerance itself will live in.
        var compliant = comparable.Count(line =>
            Math.Abs(line.ObservedMinorUnits - line.ExpectedMinorUnits!.Value)
                <= inputs.PriceToleranceMinorUnits);

        return Round(100m * compliant / comparable.Count);
    }

    /// <summary>What the weight set said a pillar is worth; zero when it named none.</summary>
    /// <remarks>
    /// Zero rather than skipping the pillar, and the difference matters: a pillar the tenant weighted
    /// at nothing was <i>measured and disregarded</i>, which is a tenant switching share-of-shelf off.
    /// A pillar nobody measured is skipped. Both contribute nothing to the total, and only the second
    /// leaves the denominator.
    /// </remarks>
    private static decimal WeightOf(ScoreInputs inputs, ScorePillar pillar) =>
        inputs.Weights.FirstOrDefault(weight => weight.Pillar == pillar)?.Percentage ?? 0m;

    /// <summary>Half-up (away from zero) to two places — <c>BR-PRD-9</c>'s policy, mirrored in TS.</summary>
    private static decimal Round(decimal value) =>
        Math.Round(value, Decimals, MidpointRounding.AwayFromZero);
}
