using FieldKit.Modules.Visit.Contracts;

namespace FieldKit.Server.Tests;

/// <summary>
/// The arithmetic on <see cref="VisitOutcomeCounts"/> (<c>VIS-10</c>) — W12 slice 1.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="VisitQueryTests"/> proves the counts are the right counts; this proves what the record
/// makes of them, and it needs no database to do it. The one that matters is <b>nothing yet versus
/// nothing worked</b>: a strike rate of <c>null</c> and a strike rate of <c>0</c> are different
/// claims about a territory, and a dashboard that shows the second when it means the first tells a
/// supervisor their team failed every call it has not made.
/// </para>
/// </remarks>
public class VisitOutcomeCountsTests
{
    [Fact]
    public void Nothing_finished_has_no_strike_rate_at_all()
    {
        // A fresh tenant, and a morning where three reps are mid-visit. Neither has a rate.
        Assert.Null(new VisitOutcomeCounts(0, 0, 0).StrikeRate);
        Assert.Null(new VisitOutcomeCounts(0, 0, 3).StrikeRate);
    }

    [Fact]
    public void Everything_finished_and_nothing_sold_is_zero_rather_than_nothing()
    {
        // The other half of the same distinction, and the reason `null` is not simply "no visits":
        // four calls came back empty, which is a real 0% and must not read as "no data".
        var counts = new VisitOutcomeCounts(Productive: 0, NonProductive: 4, Open: 0);

        Assert.Equal(0m, counts.StrikeRate);
    }

    [Fact]
    public void Open_visits_count_towards_the_total_and_not_towards_the_rate()
    {
        var counts = new VisitOutcomeCounts(Productive: 3, NonProductive: 1, Open: 6);

        Assert.Equal(10, counts.Total);
        Assert.Equal(4, counts.Finished);

        // 3 ÷ 4, not 3 ÷ 10 — the six the rep has not finished cannot be counted against them.
        Assert.Equal(0.75m, counts.StrikeRate);
    }

    [Fact]
    public void The_rate_is_decimal_rather_than_a_ratio_of_integers()
    {
        // 1 ÷ 3 is 0 in integer arithmetic, which would report a third of calls productive as zero.
        // Cheap to assert and the exact shape of a plausible slip in a record made of `int`s.
        var counts = new VisitOutcomeCounts(Productive: 1, NonProductive: 2, Open: 0);

        Assert.True(counts.StrikeRate > 0.33m);
        Assert.True(counts.StrikeRate < 0.34m);
    }
}
