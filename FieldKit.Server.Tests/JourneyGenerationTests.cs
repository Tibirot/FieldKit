using FieldKit.Modules.Journey;

namespace FieldKit.Server.Tests;

/// <summary>
/// Generation, as rules rather than as an endpoint (<c>JRN-03</c>, <c>BR-JRN-1/3/5</c>) — W7 slice 3.
/// </summary>
/// <remarks>
/// <para>
/// <b>No fixture, no collection, no database</b>, for the reason <c>PriceResolutionVectorTests</c>
/// gives: <see cref="JourneyGenerator"/> is a pure function, and this is the part of the module a
/// supervisor argues with — so the tests have to be hand-written scenarios that state a rule and
/// check it, not seeded data that happens to produce a plan.
/// </para>
/// <para>
/// Dates are chosen so the arithmetic is checkable by eye: the window opens on Monday 2 March 2026.
/// </para>
/// </remarks>
public class JourneyGenerationTests
{
    private static readonly DateOnly Monday = new(2026, 3, 2);

    private static CallFrequency Weekly(int visits = 1) => Frequency(visits, 7);

    private static CallFrequency Frequency(int visits, int cycleDays)
    {
        Assert.True(CallFrequency.TryCreate(visits, cycleDays, out var frequency));
        return frequency;
    }

    /// <summary>An open outlet with a frequency — the ordinary case, named by its code.</summary>
    private static PlannableOutlet Outlet(string code, CallFrequency? frequency, bool isOpen = true) =>
        new(Guid.CreateVersion7(), code, isOpen, frequency);

    /// <summary>Every day from <paramref name="start"/>, each holding <paramref name="capacity"/>.</summary>
    private static List<WorkingDay> EveryDay(DateOnly start, int count, int capacity = 10) =>
        [.. Enumerable.Range(0, count).Select(offset => new WorkingDay(start.AddDays(offset), capacity))];

    private static GeneratedPlan Generate(
        IEnumerable<PlannableOutlet> outlets, IReadOnlyList<WorkingDay> days, int windowDays) =>
        JourneyGenerator.Generate(
            new GenerationRequest(Monday, Monday.AddDays(windowDays - 1), [.. outlets], days));

    [Theory]
    // A whole number of cycles is the ordinary case.
    [InlineData(1, 7, 28, 4)]
    [InlineData(2, 7, 28, 8)]
    [InlineData(4, 28, 28, 4)]
    // Partial cycles round half-up: 11/7 is 1.57 calls, which is two.
    [InlineData(1, 7, 11, 2)]
    [InlineData(1, 7, 10, 1)]
    // Exactly half rounds up, the same direction money does.
    [InlineData(1, 14, 7, 1)]
    // A window shorter than half a cycle owes nothing at all.
    [InlineData(1, 28, 3, 0)]
    public void A_window_owes_an_outlet_its_share_of_a_cycle(
        int visits, int cycleDays, int windowDays, int expected)
    {
        // One formula rather than special cases for whole and partial cycles, because the special
        // cases are where a rule stops being explainable to whoever is arguing with the plan.
        var frequency = Frequency(visits, cycleDays);

        Assert.Equal(
            expected,
            JourneyGenerator.RequiredVisits(frequency, Monday, Monday.AddDays(windowDays - 1)));
    }

    [Fact]
    public void An_outlet_gets_the_calls_its_frequency_asks_for()
    {
        var outlet = Outlet("OUT-1", Weekly());

        var plan = Generate([outlet], EveryDay(Monday, 28), windowDays: 28);

        Assert.Equal(4, plan.Visits.Count);
        Assert.All(plan.Visits, visit => Assert.Equal(outlet.OutletId, visit.OutletId));
        Assert.Empty(plan.Shortfalls);
        Assert.Empty(plan.Excluded);
    }

    [Fact]
    public void Calls_are_spread_across_the_window_rather_than_bunched_at_its_start()
    {
        // The point of a frequency is regular contact. Four calls in the first four days satisfies
        // the count and defeats the requirement, so the spacing is the assertion — not the total.
        var plan = Generate([Outlet("OUT-1", Weekly())], EveryDay(Monday, 28), windowDays: 28);

        var days = plan.Visits.Select(visit => visit.Date.DayNumber - Monday.DayNumber).ToList();

        Assert.Equal(4, days.Count);

        // Each call sits in its own quarter of the window, near the middle of it.
        Assert.Equal([3, 10, 17, 24], days);
    }

    [Fact]
    public void A_closed_outlet_is_excluded_rather_than_planned()
    {
        // BR-JRN-5, which defers to BR-OUT-4: a closed shop keeps its history and gets no new plan.
        var closed = Outlet("OUT-1", Weekly(), isOpen: false);
        var open = Outlet("OUT-2", Weekly());

        var plan = Generate([closed, open], EveryDay(Monday, 7), windowDays: 7);

        Assert.All(plan.Visits, visit => Assert.Equal(open.OutletId, visit.OutletId));

        var excluded = Assert.Single(plan.Excluded);

        Assert.Equal(closed.OutletId, excluded.OutletId);
        Assert.Equal(ExclusionReason.Closed, excluded.Reason);
    }

    [Fact]
    public void An_outlet_nobody_has_given_a_frequency_is_named_rather_than_dropped()
    {
        // A gap in configuration, and the whole reason it is carried this far rather than filtered
        // out by the caller: a plan that silently omits a shop cannot tell anyone which shops it
        // omitted, and "why is this outlet never visited?" is then unanswerable from the plan.
        var unconfigured = Outlet("OUT-1", frequency: null);

        var plan = Generate([unconfigured], EveryDay(Monday, 7), windowDays: 7);

        Assert.Empty(plan.Visits);

        var excluded = Assert.Single(plan.Excluded);

        Assert.Equal(unconfigured.OutletId, excluded.OutletId);
        Assert.Equal(ExclusionReason.NoFrequency, excluded.Reason);
    }

    [Fact]
    public void A_day_never_holds_more_calls_than_its_capacity()
    {
        // BR-JRN-3. Three outlets due once each, one working day that holds two.
        var days = new List<WorkingDay> { new(Monday, 2) };

        var plan = Generate(
            [Outlet("OUT-1", Weekly()), Outlet("OUT-2", Weekly()), Outlet("OUT-3", Weekly())],
            days,
            windowDays: 7);

        Assert.Equal(2, plan.Visits.Count);
        Assert.All(plan.Visits, visit => Assert.Equal(Monday, visit.Date));
    }

    [Fact]
    public void What_will_not_fit_comes_back_as_a_shortfall_rather_than_disappearing()
    {
        // The sentence a supervisor needs. Without it a plan that is short looks exactly like a plan
        // that is complete, and the rep is the one who finds out.
        var outlet = Outlet("OUT-1", Weekly(4));
        var days = new List<WorkingDay> { new(Monday, 1) };

        var plan = Generate([outlet], days, windowDays: 7);

        Assert.Single(plan.Visits);

        var shortfall = Assert.Single(plan.Shortfalls);

        Assert.Equal(outlet.OutletId, shortfall.OutletId);
        Assert.Equal(4, shortfall.Required);
        Assert.Equal(1, shortfall.Planned);
    }

    [Fact]
    public void Scarce_capacity_costs_every_outlet_its_last_call_rather_than_some_outlets_everything()
    {
        // The decision that makes a short plan defensible. Planning outlet-by-outlet would give the
        // alphabetically-early shops all of their calls and the late ones none — the same total
        // shortfall, concentrated on whoever sorts last, which is a rule nobody would defend aloud.
        var first = Outlet("OUT-1", Weekly(2));
        var second = Outlet("OUT-2", Weekly(2));
        var third = Outlet("OUT-3", Weekly(2));

        // Six calls are due; the week holds three.
        var days = new List<WorkingDay> { new(Monday, 1), new(Monday.AddDays(2), 1), new(Monday.AddDays(4), 1) };

        var plan = Generate([first, second, third], days, windowDays: 7);

        Assert.Equal(3, plan.Visits.Count);

        // One each, rather than two for OUT-1 and one for OUT-2. Asserted as a set: the ids are v7
        // GUIDs and two created in the same millisecond do not reliably sort in creation order,
        // which is a fact about the fixture rather than anything the plan promises.
        Assert.Equal(
            new HashSet<Guid> { first.OutletId, second.OutletId, third.OutletId },
            plan.Visits.Select(visit => visit.OutletId).ToHashSet());

        Assert.Equal(3, plan.Shortfalls.Count);
        Assert.All(plan.Shortfalls, shortfall => Assert.Equal(1, shortfall.Planned));
    }

    [Fact]
    public void A_full_day_pushes_a_call_to_the_nearest_day_either_side()
    {
        // Outward from the ideal rather than forward from it. Searching only forward would drift
        // every displaced visit towards the end of the window, leaving the last days overloaded and
        // the first half empty.
        var days = new List<WorkingDay>
        {
            new(Monday, 1),
            new(Monday.AddDays(1), 0),
            new(Monday.AddDays(2), 1),
        };

        // A seven-day window, so each outlet is owed exactly one call — three *working* days, of
        // which the middle one is full. (A three-day window would owe a weekly outlet nothing at
        // all, which is how the first version of this test came to assert on an empty plan.)
        var plan = Generate(
            [Outlet("OUT-1", Weekly()), Outlet("OUT-2", Weekly())], days, windowDays: 7);

        Assert.Equal(2, plan.Visits.Count);
        Assert.DoesNotContain(plan.Visits, visit => visit.Date == Monday.AddDays(1));
    }

    [Fact]
    public void A_rep_with_no_working_days_plans_nothing_and_says_so()
    {
        // An unconfigured calendar, or a fortnight that is entirely holidays. The outlet was in
        // scope and owed calls, so it is a shortfall rather than an exclusion — nothing about the
        // outlet disqualified it.
        var outlet = Outlet("OUT-1", Weekly());

        var plan = Generate([outlet], [], windowDays: 7);

        Assert.Empty(plan.Visits);
        Assert.Empty(plan.Excluded);

        var shortfall = Assert.Single(plan.Shortfalls);

        Assert.Equal(1, shortfall.Required);
        Assert.Equal(0, shortfall.Planned);
    }

    [Fact]
    public void An_outlet_the_window_is_too_short_for_is_neither_planned_nor_reported()
    {
        // Zero required is not a shortfall and not an exclusion. A monthly shop in a three-day window
        // is not being skipped — the window simply does not owe it a call, and reporting that as a
        // failure would fill every short plan with noise.
        var plan = Generate([Outlet("OUT-1", Frequency(1, 28))], EveryDay(Monday, 3), windowDays: 3);

        Assert.Empty(plan.Visits);
        Assert.Empty(plan.Shortfalls);
        Assert.Empty(plan.Excluded);
    }

    [Fact]
    public void The_plan_is_ordered_by_day_and_then_by_outlet_code()
    {
        // Stable and arbitrary, on purpose. Sequencing a day's calls by proximity is JRN-09, a
        // Should at Phase 3 — so the order here is one nobody should read meaning into, and it is
        // deterministic so that regenerating an unchanged plan produces an unchanged plan.
        var zulu = Outlet("OUT-Z", Weekly());
        var alpha = Outlet("OUT-A", Weekly());

        var plan = Generate([zulu, alpha], EveryDay(Monday, 7), windowDays: 7);

        Assert.Equal(2, plan.Visits.Count);
        Assert.Equal(plan.Visits[0].Date, plan.Visits[1].Date);
        Assert.Equal([alpha.OutletId, zulu.OutletId], plan.Visits.Select(visit => visit.OutletId));
    }

    [Fact]
    public void The_same_inputs_produce_the_same_plan()
    {
        // Regeneration is a normal act — a supervisor changes one frequency and runs it again — and
        // a plan that reshuffled every time would make the diff unreadable and the change invisible.
        var outlets = new[]
        {
            Outlet("OUT-1", Weekly(2)),
            Outlet("OUT-2", Frequency(1, 14)),
            Outlet("OUT-3", Weekly()),
        };

        var days = EveryDay(Monday, 28, capacity: 2);

        var first = Generate(outlets, days, windowDays: 28);
        var second = Generate(outlets, days, windowDays: 28);

        Assert.Equal(
            first.Visits.Select(visit => (visit.Date, visit.OutletId)),
            second.Visits.Select(visit => (visit.Date, visit.OutletId)));
    }

    [Fact]
    public void A_backwards_window_owes_nothing_rather_than_throwing()
    {
        // Reachable from a query string once slice 4 puts an endpoint in front of this. A pure
        // function answering "nothing" is better than one that throws from inside a plan.
        var plan = JourneyGenerator.Generate(new GenerationRequest(
            Monday.AddDays(7), Monday, [Outlet("OUT-1", Weekly())], EveryDay(Monday, 7)));

        Assert.Empty(plan.Visits);
        Assert.Empty(plan.Shortfalls);
    }
}
