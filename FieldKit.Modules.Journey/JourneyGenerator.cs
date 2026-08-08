namespace FieldKit.Modules.Journey;

/// <summary>One outlet as generation sees it — everything it needs, and nothing to look up.</summary>
/// <remarks>
/// Flat and self-contained, the same shape <c>PriceCandidate</c> takes and for the same reason: it
/// is what lets <see cref="JourneyGenerator"/> be a pure function over data the caller has already
/// gathered.
/// <para>
/// <paramref name="IsOpen"/> and <paramref name="Frequency"/> are both allowed to say "no", and they
/// say different things. A closed shop is excluded by <c>BR-JRN-5</c>; a shop with no frequency is a
/// gap in configuration. Passing both in — rather than letting the caller filter them out — is what
/// lets the plan explain which shops it skipped and why.
/// </para>
/// </remarks>
/// <param name="Code">The outlet's code. Used only to order the plan — see <see cref="JourneyGenerator"/>.</param>
public sealed record PlannableOutlet(Guid OutletId, string Code, bool IsOpen, CallFrequency? Frequency);

/// <summary>Why an outlet is not in the plan at all.</summary>
public enum ExclusionReason
{
    /// <summary>Closed or inactive — <c>BR-JRN-5</c>, which defers to <c>BR-OUT-4</c>.</summary>
    Closed,

    /// <summary>Nobody has said how often to visit it. A gap in configuration, not a decision.</summary>
    NoFrequency,
}

/// <summary>An outlet the plan skipped, and why.</summary>
public sealed record ExcludedOutlet(Guid OutletId, ExclusionReason Reason);

/// <summary>An outlet that is in the plan, but not as often as its frequency asks.</summary>
/// <remarks>
/// Reported rather than silently accepted, because it is the sentence a supervisor needs: "this
/// territory needs 240 calls and the rep's calendar holds 180". Without it, a plan that is 25% short
/// looks exactly like a plan that is complete.
/// </remarks>
public sealed record Shortfall(Guid OutletId, int Required, int Planned);

/// <summary>
/// One call, on one day, as generation produced it.
/// </summary>
/// <remarks>
/// Distinct from <see cref="PlannedVisit"/>, which is the same call once it has been stored on a
/// plan. The names are worth keeping apart: this one is the output of a pure function and has no
/// identity, and confusing the two is how a domain entity ends up in a signature that promised to
/// be side-effect-free.
/// </remarks>
public sealed record GeneratedVisit(DateOnly Date, Guid OutletId);

/// <summary>What generation was asked for.</summary>
/// <param name="From">First day of the window, inclusive.</param>
/// <param name="To">Last day of the window, inclusive.</param>
/// <param name="WorkingDays">
/// The days the rep can actually be sent out, with each day's capacity — the rep's pattern with the
/// holidays already taken out (<c>JRN-02</c>).
/// </param>
public sealed record GenerationRequest(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<PlannableOutlet> Outlets,
    IReadOnlyList<WorkingDay> WorkingDays);

/// <summary>A plan, and everything it could not do.</summary>
public sealed record GeneratedPlan(
    IReadOnlyList<GeneratedVisit> Visits,
    IReadOnlyList<ExcludedOutlet> Excluded,
    IReadOnlyList<Shortfall> Shortfalls);

/// <summary>
/// Turns frequency × territory × calendar into planned visits (<c>JRN-03</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure and side-effect-free</b>, exactly as <c>PriceResolver</c> is: data in, one plan out. No
/// database, no clock, no tenant context. That is not tidiness — this is the most rule-dense thing
/// in the module and the one part a supervisor will argue with, so it has to be runnable against a
/// hand-written scenario in a unit test rather than only against a seeded database.
/// </para>
/// <para>
/// <b>The window is a parameter, not "now".</b> Regenerating last month's plan must produce last
/// month's plan; a function that asks what day it is cannot promise that.
/// </para>
/// <para>
/// <b>Day sequencing is not here.</b> Ordering a day's calls by proximity is <c>JRN-09</c>, a
/// *Should* at Phase 3, while everything else in this file is a Phase 2 *Must*. So the plan emits a
/// stable, arbitrary order — by outlet code within a day — and the heuristic replaces it later.
/// Worth stating because "the order looks wrong" is otherwise a bug report against a slice that
/// never claimed to order anything.
/// </para>
/// </remarks>
public static class JourneyGenerator
{
    /// <summary>
    /// Plans the window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three rules, in the order they apply:
    /// </para>
    /// <list type="number">
    /// <item><b><c>BR-JRN-5</c></b> — a closed outlet is excluded before anything else is computed.
    /// An outlet with no frequency is excluded on the same pass, for a different reason.</item>
    /// <item><b>Frequency</b> becomes a number of calls for *this window*: the window's share of a
    /// cycle, rounded half-up. See <see cref="RequiredVisits"/>.</item>
    /// <item><b><c>BR-JRN-3</c></b> — a day never holds more calls than its capacity. Visits that do
    /// not fit are not placed elsewhere silently; they come back as a <see cref="Shortfall"/>.</item>
    /// </list>
    /// </remarks>
    public static GeneratedPlan Generate(GenerationRequest request)
    {
        var excluded = new List<ExcludedOutlet>();
        var plannable = new List<(PlannableOutlet Outlet, int Required)>();

        // Ordered by code up front, so everything downstream — placement, tie-breaks, the emitted
        // plan — is deterministic without needing to sort again.
        foreach (var outlet in request.Outlets.OrderBy(o => o.Code, StringComparer.Ordinal))
        {
            if (!outlet.IsOpen)
            {
                excluded.Add(new ExcludedOutlet(outlet.OutletId, ExclusionReason.Closed));
                continue;
            }

            if (outlet.Frequency is not { } frequency)
            {
                excluded.Add(new ExcludedOutlet(outlet.OutletId, ExclusionReason.NoFrequency));
                continue;
            }

            var required = RequiredVisits(frequency, request.From, request.To);

            // Zero required is not a shortfall and not an exclusion: the window is simply too short
            // to owe this outlet a call. A monthly shop in a three-day window is not being skipped.
            if (required > 0) plannable.Add((outlet, required));
        }

        var days = request.WorkingDays.OrderBy(day => day.Date).ToArray();
        var remaining = days.Select(day => day.Capacity).ToArray();
        var placed = new Dictionary<Guid, List<DateOnly>>();

        /*
         * Round-robin by visit number, not outlet by outlet.
         *
         * Under capacity pressure something has to give, and this decides *what*: every outlet gets
         * its first call before any outlet gets its second. Planning outlet-by-outlet would instead
         * give the alphabetically-early shops everything and the late ones nothing — the same total
         * shortfall, concentrated on whoever sorts last, which is a rule nobody would defend out
         * loud.
         */
        var maxRequired = plannable.Count == 0 ? 0 : plannable.Max(entry => entry.Required);

        for (var round = 0; round < maxRequired; round++)
        {
            foreach (var (outlet, required) in plannable)
            {
                if (round >= required) continue;

                var target = TargetDay(round, required, days.Length);
                var day = NearestDayWithRoom(remaining, target);

                if (day is null) continue;

                remaining[day.Value]--;

                if (!placed.TryGetValue(outlet.OutletId, out var dates))
                {
                    placed[outlet.OutletId] = dates = [];
                }

                dates.Add(days[day.Value].Date);
            }
        }

        var shortfalls = plannable
            .Select(entry => new Shortfall(
                entry.Outlet.OutletId,
                entry.Required,
                placed.TryGetValue(entry.Outlet.OutletId, out var dates) ? dates.Count : 0))
            .Where(shortfall => shortfall.Planned < shortfall.Required)
            .ToList();

        var codes = request.Outlets.ToDictionary(outlet => outlet.OutletId, outlet => outlet.Code);

        var visits = placed
            .SelectMany(entry => entry.Value.Select(date => new GeneratedVisit(date, entry.Key)))
            .OrderBy(visit => visit.Date)
            .ThenBy(visit => codes[visit.OutletId], StringComparer.Ordinal)
            .ToList();

        return new GeneratedPlan(visits, excluded, shortfalls);
    }

    /// <summary>
    /// How many calls this window owes an outlet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window's share of a cycle, <b>rounded half-up</b>: a 28-day window at 1×/week owes four
    /// calls, and an 11-day window owes two rather than one and a half. One formula rather than
    /// special cases for whole and partial cycles, because the special cases are where a rule stops
    /// being explainable to the person arguing with the plan.
    /// </para>
    /// <para>
    /// Integer arithmetic, not floating point. The numbers are small and the answer is a count, and
    /// a plan that differs by one call depending on how a double rounded is exactly the kind of
    /// irreproducibility <c>BR-PRD-9</c> banned from money for the same reason.
    /// </para>
    /// <para>
    /// A window shorter than half a cycle owes <b>zero</b>, which is deliberate: a monthly shop in a
    /// three-day window should not be visited, and rounding it up to one would quietly make every
    /// short window over-plan.
    /// </para>
    /// </remarks>
    public static int RequiredVisits(CallFrequency frequency, DateOnly from, DateOnly to)
    {
        if (to < from) return 0;

        var windowDays = to.DayNumber - from.DayNumber + 1;

        // (visits * window / cycle), rounded half-up, without leaving the integers.
        return ((frequency.VisitsPerCycle * windowDays * 2) + frequency.CycleLengthDays)
            / (2 * frequency.CycleLengthDays);
    }

    /// <summary>
    /// Where the <paramref name="index"/>-th of <paramref name="count"/> visits ideally falls.
    /// </summary>
    /// <remarks>
    /// Evenly spaced across the working days, at the midpoint of each visit's share rather than at
    /// its start — so two visits across ten days land around days 3 and 8, not days 1 and 6. Bunching
    /// a shop's calls at the front of a cycle satisfies the count and defeats the point of a
    /// frequency, which is regular contact.
    /// </remarks>
    private static int TargetDay(int index, int count, int dayCount) =>
        dayCount == 0 ? 0 : Math.Min((((2 * index) + 1) * dayCount) / (2 * count), dayCount - 1);

    /// <summary>
    /// The day nearest <paramref name="target"/> that still has room, or null when none has.
    /// </summary>
    /// <remarks>
    /// Outward from the ideal rather than forward from it, so a full Tuesday pushes a call to Monday
    /// as readily as to Wednesday. Searching only forward would drift every displaced visit towards
    /// the end of the window and leave the last days overloaded and the first half empty.
    /// </remarks>
    private static int? NearestDayWithRoom(int[] remaining, int target)
    {
        for (var offset = 0; offset < remaining.Length; offset++)
        {
            // Earlier first on a tie, which keeps the plan front-loaded rather than arbitrary.
            if (target - offset >= 0 && remaining[target - offset] > 0) return target - offset;
            if (target + offset < remaining.Length && remaining[target + offset] > 0) return target + offset;
        }

        return null;
    }
}
