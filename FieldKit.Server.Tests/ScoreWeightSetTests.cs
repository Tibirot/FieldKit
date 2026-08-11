using FieldKit.Modules.Configuration;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.SharedKernel;

namespace FieldKit.Server.Tests;

/// <summary>
/// A tenant's perfect-store weighting, as a rule rather than as an endpoint (<c>AUD-06</c>,
/// <c>BR-AUD-4/8</c>) — W10 slice 1.
/// </summary>
/// <remarks>
/// <para>
/// Two rules carry the slice, and only one of them is arithmetic. The weights must sum to exactly
/// 100 (<c>BR-AUD-4</c>), and a <b>published</b> set must be unchangeable (<c>BR-AUD-8</c>, decided
/// in W10 slice 0) — because the server recomputes a sealed audit with the weights that audit was
/// scored against, and that is a sentence about a fixed set of numbers.
/// </para>
/// <para>
/// Here rather than in the integration tests because neither rule needs a database to be wrong.
/// <see cref="ScoreWeightTests"/> covers what a caller sees over HTTP.
/// </para>
/// </remarks>
public class ScoreWeightSetTests
{
    /// <summary>A clock that does not move. Time is incidental here — every rule under test is about numbers.</summary>
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private static readonly IClock Clock = new FixedClock(new DateTimeOffset(2026, 4, 6, 9, 0, 0, TimeSpan.Zero));

    private static (ScorePillar, decimal)[] Balanced() =>
    [
        (ScorePillar.Availability, 50m),
        (ScorePillar.ShareOfShelf, 30m),
        (ScorePillar.PriceCompliance, 20m),
    ];

    [Fact]
    public void A_balanced_set_drafts()
    {
        var (set, refusal) = ScoreWeightSet.Draft(1, Balanced());

        Assert.Equal(WeightSetRefusal.None, refusal);
        Assert.NotNull(set);
        Assert.Equal(1, set.Version);
        Assert.False(set.IsPublished);
        Assert.Equal(3, set.Weights.Count);
    }

    [Theory]
    [InlineData(50, 30, 19)]   // 99
    [InlineData(50, 30, 21)]   // 101
    [InlineData(0, 0, 0)]      // nothing at all
    public void Weights_that_do_not_add_up_to_a_hundred_are_refused(int availability, int shelf, int price)
    {
        var (set, refusal) = ScoreWeightSet.Draft(1, [
            (ScorePillar.Availability, availability),
            (ScorePillar.ShareOfShelf, shelf),
            (ScorePillar.PriceCompliance, price),
        ]);

        Assert.Equal(WeightSetRefusal.DoesNotSumToOneHundred, refusal);
        Assert.Null(set);
    }

    [Fact]
    public void A_hundred_means_a_hundred_and_not_nearly()
    {
        /*
         * The case a tolerance would wave through, and the reason there is none.
         *
         * `33.33 × 3` is exactly 99.99 in `decimal` — no floating-point ambiguity to forgive. A set
         * that summed to 99.99 would be renormalised by the score against a total that is not 100,
         * silently rescaling every audit stored under it, and an administrator would never see why
         * their numbers were 0.01% out.
         */
        var (_, refusal) = ScoreWeightSet.Draft(1, [
            (ScorePillar.Availability, 33.33m),
            (ScorePillar.ShareOfShelf, 33.33m),
            (ScorePillar.PriceCompliance, 33.33m),
        ]);

        Assert.Equal(WeightSetRefusal.DoesNotSumToOneHundred, refusal);
    }

    [Fact]
    public void Decimal_weights_that_do_add_up_are_accepted()
    {
        // The other half of the test above: refusing 99.99 must not mean refusing decimals. A tenant
        // weighting 33.34/33.33/33.33 has expressed thirds as exactly as this type allows.
        var (set, refusal) = ScoreWeightSet.Draft(1, [
            (ScorePillar.Availability, 33.34m),
            (ScorePillar.ShareOfShelf, 33.33m),
            (ScorePillar.PriceCompliance, 33.33m),
        ]);

        Assert.Equal(WeightSetRefusal.None, refusal);
        Assert.NotNull(set);
    }

    [Fact]
    public void A_pillar_cannot_be_weighted_twice()
    {
        // Two rows for one pillar is a set whose sum is right and whose meaning is not: the score
        // would have to pick one, and "whichever came back first" is not a weighting.
        var (_, refusal) = ScoreWeightSet.Draft(1, [
            (ScorePillar.Availability, 50m),
            (ScorePillar.Availability, 50m),
        ]);

        Assert.Equal(WeightSetRefusal.DuplicatePillar, refusal);
    }

    [Fact]
    public void A_weighting_of_nothing_is_refused()
    {
        var (_, refusal) = ScoreWeightSet.Draft(1, []);

        Assert.Equal(WeightSetRefusal.Empty, refusal);
    }

    [Fact]
    public void A_pillar_may_be_worth_nothing_as_long_as_the_rest_add_up()
    {
        // A tenant that does not measure share of shelf turns it off by weighting it zero, and the
        // score then has two pillars rather than a missing one. Distinct from the *skipped* pillar
        // of BR-AUD-2, which is a measurement the rep could not take.
        var (set, refusal) = ScoreWeightSet.Draft(1, [
            (ScorePillar.Availability, 70m),
            (ScorePillar.ShareOfShelf, 0m),
            (ScorePillar.PriceCompliance, 30m),
        ]);

        Assert.Equal(WeightSetRefusal.None, refusal);
        Assert.NotNull(set);
    }

    [Fact]
    public void A_draft_can_be_edited()
    {
        var (set, _) = ScoreWeightSet.Draft(1, Balanced());

        var refusal = set!.Set([
            (ScorePillar.Availability, 60m),
            (ScorePillar.PriceCompliance, 40m),
        ], Clock);

        Assert.Equal(WeightSetRefusal.None, refusal);
        Assert.Equal(2, set.Weights.Count);
        Assert.Equal(60m, set.Weights.Single(weight => weight.Pillar == ScorePillar.Availability).Percentage);
    }

    [Fact]
    public void An_edit_is_checked_too_and_leaves_the_set_alone_when_refused()
    {
        /*
         * `BR-AUD-4` is a property of a weight set, not of a published one — so it is enforced on
         * every write rather than at publish. The second half matters as much as the first: a
         * refused edit that had already cleared the weights would leave a set nobody asked for.
         */
        var (set, _) = ScoreWeightSet.Draft(1, Balanced());

        var refusal = set!.Set([(ScorePillar.Availability, 90m)], Clock);

        Assert.Equal(WeightSetRefusal.DoesNotSumToOneHundred, refusal);
        Assert.Equal(3, set.Weights.Count);
        Assert.Equal(50m, set.Weights.Single(weight => weight.Pillar == ScorePillar.Availability).Percentage);
    }

    [Fact]
    public void Publishing_freezes_the_weights_for_good()
    {
        // The rule W10 slice 0 exists for. Everything BR-AUD-8 promises rests on this being true.
        var (set, _) = ScoreWeightSet.Draft(1, Balanced());

        Assert.Equal(WeightSetRefusal.None, set!.Publish(Clock));
        Assert.True(set.IsPublished);
        Assert.Equal(Clock.UtcNow, set.PublishedAtUtc);

        var edit = set.Set([(ScorePillar.Availability, 100m)], Clock);

        Assert.Equal(WeightSetRefusal.AlreadyPublished, edit);
        Assert.Equal(3, set.Weights.Count);
    }

    [Fact]
    public void Publishing_twice_is_refused_rather_than_silently_fine()
    {
        // An administrator who thinks they are publishing an edit needs to be told the edit was
        // never in this version; a second 200 would hide that.
        var (set, _) = ScoreWeightSet.Draft(1, Balanced());
        set!.Publish(Clock);

        Assert.Equal(WeightSetRefusal.AlreadyPublished, set.Publish(Clock));
    }
}
