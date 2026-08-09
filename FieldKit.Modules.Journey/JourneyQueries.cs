using FieldKit.Modules.Journey.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Journey;

/// <summary>
/// Answers <see cref="IJourneyQuery"/> from the published plans (<c>JRN-04</c>).
/// </summary>
/// <remarks>
/// One query, and every condition in it is part of the answer rather than a filter the caller could
/// have applied: the rep and the outlet are what "this visit's call" means, and the published state
/// is what makes the call a real one. Doing it in the database rather than by loading a plan also
/// keeps the plan's own entities inside this module (<c>AT-3</c>) — what leaves is a record with an
/// id in it.
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
}
