using FieldKit.Modules.Org.Contracts;
using FieldKit.Modules.Outlets.Contracts;

namespace FieldKit.Modules.Journey;

/// <summary>
/// Gathers what generation needs and runs it (<c>JRN-03</c>, <c>JRN-04</c>).
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="JourneyGenerator"/>: that one holds the rules and touches nothing,
/// this one does the touching and holds no rules. Every decision about *what to plan* lives in the
/// pure function; every decision about *where the facts come from* lives here. Keeping them apart is
/// what lets the rules be argued with in a unit test.
/// </para>
/// <para>
/// Three modules answer between them, and none of them is asked for more than it owns: Organization
/// for what the rep covers (<c>IRepScope</c>), Outlets for whether those shops are open
/// (<c>IOutletCatalog</c>), and this module for their frequencies and the rep's calendar.
/// </para>
/// </remarks>
internal sealed class JourneyPlanner(
    IRepScope repScope,
    IOutletCatalog outlets,
    FrequencyResolver frequencies,
    CalendarReader calendar)
{
    public async Task<GeneratedPlan> GenerateAsync(
        string userId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        /*
         * Coverage is read once, on the window's first day.
         *
         * `IRepScope` answers per day precisely because coverage is a per-day fact — an assignment
         * ending mid-window covers the first half and not the second — and this asks once anyway.
         * That is a known limitation rather than an oversight: the generator takes a flat list of
         * outlets and has no way to express "plannable only until the 14th", so asking per day would
         * produce a union that over-plans or an intersection that under-plans, both silently.
         *
         * The operational answer today is that a mid-window handover means regenerating for the new
         * rep, which a supervisor does anyway. Modelling it properly means per-day scope inside the
         * generator, and that belongs with JRN-08's rescheduling rather than smuggled in here.
         */
        var coverage = await repScope.ForRepAsync(userId, from, cancellationToken);

        if (coverage.OutletIds.Count == 0) return Empty;

        // Outlets first, because it answers the question that can exclude a shop outright (BR-JRN-5)
        // and because it is the only source for the code the plan is ordered by.
        var summaries = await outlets.FindManyAsync(coverage.OutletIds, cancellationToken);

        if (summaries.Count == 0) return Empty;

        var resolved = (await frequencies.ForOutletsAsync(coverage.OutletIds, cancellationToken))
            .ToDictionary(row => row.OutletId, row => row.Frequency);

        var workingDays = await calendar.ForRepAsync(userId, from, to, cancellationToken);

        // A null frequency is passed through rather than filtered out: the generator reports it as
        // an unconfigured outlet, which is the only way anybody finds out the gap exists.
        var plannable = summaries
            .Select(outlet => new PlannableOutlet(
                outlet.OutletId,
                outlet.Code,
                outlet.IsOpen,
                resolved.TryGetValue(outlet.OutletId, out var frequency) ? frequency : null))
            .ToList();

        return JourneyGenerator.Generate(new GenerationRequest(from, to, plannable, workingDays));
    }

    private static GeneratedPlan Empty => new([], [], []);
}
