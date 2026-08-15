using FieldKit.Modules.Journey.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Journey;

/// <summary>
/// Answers <see cref="IJourneyQuery"/> from the published plans (<c>JRN-04</c>).
/// </summary>
/// <remarks>
/// <para>
/// Every condition in these is part of the answer rather than a filter the caller could have
/// applied: the rep and the outlet are what "this visit's call" means, and the published state is
/// what makes the call a real one. Doing it in the database rather than by loading a plan also keeps
/// the plan's own entities inside this module (<c>AT-3</c>) — what leaves is a record with an id in
/// it, or a pair of counts.
/// </para>
/// <para>
/// <c>CountPlannedAsync</c> counts in Postgres for the reason its visit-side twin does: one grouped
/// aggregate returns at most two rows whatever the round holds, and a caller reducing a list of
/// calls would have paid the cost of shipping them.
/// </para>
/// </remarks>
internal sealed class JourneyQueries(JourneyDbContext db) : IJourneyQuery
{
    public async Task<PlannedCall?> ForVisitAsync(
        Guid plannedVisitId,
        string userId,
        Guid outletId,
        CancellationToken cancellationToken = default)
    {
        // A not-visited call is deliberately still a match. A rep who reported a shop shut and then
        // got in is exactly the sequence BR-JRN-2 keeps on the plan rather than deleting, and
        // refusing the visit here would make the earlier honesty cost them the record of the work.
        return await db.JourneyPlans
            .Where(plan => plan.UserId == userId && plan.Status == JourneyPlanStatus.Published)
            .SelectMany(plan => plan.Visits)
            .Where(visit => visit.Id == plannedVisitId && visit.OutletId == outletId)
            .Select(visit => new PlannedCall(visit.Id))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<PlannedCallCounts> CountPlannedAsync(
        IReadOnlyCollection<Guid> outletIds,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        // Nothing in scope is not the same as no filter — the decision `VisitQueryService` makes for
        // the same reason, said here rather than left to how a provider translates an empty
        // `Contains`.
        if (outletIds.Count == 0) return new PlannedCallCounts(0, 0);

        // `Date` is a `DateOnly` on the call, so the window needs no instants and no timezone: a
        // planned call is a promise about a day, not a moment. Both ends inclusive, which is a plain
        // `>=`/`<=` here rather than the half-open range the visit side needs.
        var counted = await db.JourneyPlans
            .Where(plan => plan.Status == JourneyPlanStatus.Published)
            .SelectMany(plan => plan.Visits)
            .Where(visit => outletIds.Contains(visit.OutletId))
            .Where(visit => visit.Date >= from && visit.Date <= to)
            .GroupBy(visit => visit.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        int Total(PlannedVisitStatus status) =>
            counted.SingleOrDefault(row => row.Status == status)?.Count ?? 0;

        return new PlannedCallCounts(
            Planned: Total(PlannedVisitStatus.Planned),
            NotVisited: Total(PlannedVisitStatus.NotVisited));
    }
}
