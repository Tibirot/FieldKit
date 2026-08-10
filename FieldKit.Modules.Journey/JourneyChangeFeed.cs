using FieldKit.Modules.Journey.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Journey;

/// <summary>
/// Journey's side of the pull protocol (<c>OFF-03</c>, W8 slice 8a).
/// </summary>
/// <remarks>
/// <para>
/// The tenant filter is the DbContext's, as everywhere. This class never writes a tenant predicate,
/// which is what stops a sync path becoming the one place isolation was hand-rolled.
/// </para>
/// </remarks>
internal sealed class JourneyChangeFeed(JourneyDbContext db) : IJourneyChangeFeed
{
    public async Task<JourneyChangePage> GetChangesAsync(
        long cursor, string userId, int limit, CancellationToken cancellationToken = default)
    {
        /*
         * The plan decides who may see the call, so the filter is a join and not a column.
         *
         * Denormalising `UserId` onto the call would make this a single-table read and would put a
         * second copy of "whose round is this" one bug away from disagreeing with the first. The
         * plan is the aggregate root; a call is only ever reached through it.
         */
        var mine = db.JourneyPlans
            .Where(plan => plan.UserId == userId && plan.Status == JourneyPlanStatus.Published)
            .Select(plan => plan.Id);

        // `Set<PlannedVisit>()` rather than a `DbSet` property, because there isn't one: a call is
        // reached through its plan everywhere else in this module, and adding a root-level set for
        // one reader would invite the next one to skip the aggregate too.
        var upserts = await db.Set<PlannedVisit>()
            .Where(visit => visit.RowVersion > cursor && mine.Contains(visit.JourneyPlanId))
            .OrderBy(visit => visit.RowVersion)
            .Take(limit)
            .Select(visit => new PlannedVisitSnapshot(
                visit.Id,
                visit.OutletId,
                visit.Date,
                visit.Status.ToString(),
                visit.Source.ToString(),
                visit.NotVisitedReason,
                visit.RowVersion))
            .ToListAsync(cancellationToken);

        // The highest version *in this page*, never the table's maximum — a truncated page must
        // resume rather than skip everything between the last row sent and the high-water mark.
        var highest = upserts.Count > 0 ? Math.Max(cursor, upserts[^1].RowVersion) : cursor;

        return new JourneyChangePage(upserts, Tombstones, highest);
    }

    /*
     * Always empty, and this is a statement about the domain rather than a gap.
     *
     * Nothing deletes a planned call. `BR-JRN-2` is explicit that a rep cannot remove one — a shop
     * that was skipped is a fact about the round — so a missed call becomes `NotVisited`, which is
     * an update carrying a new row version and travels as an ordinary upsert. Generation creates a
     * *new* plan rather than replacing an old one, and no endpoint deletes either. The interceptor
     * would write a tombstone if anything did; nothing does.
     *
     * Reading the table anyway would be worse than useless, because a journey tombstone cannot be
     * attributed. A tombstone records that a row is gone, so there is no longer a call to join to a
     * plan and ask whose round it was — and sending every deleted call in the tenant would tell one
     * rep how much churn there is on everybody else's. Narrowing would need a record of ownership
     * that outlives the row, which is a table this domain has no other use for.
     *
     * If a delete path ever lands, the remedy that already exists is a rebind: it clears the
     * device's scope and re-snapshots from zero. That is the right answer for a *plan* disappearing,
     * which is how calls would go — a plan is deleted as a unit, and a device holding forty calls
     * from a round that no longer exists needs a new round, not forty tombstones.
     */
    private static readonly IReadOnlyList<SharedKernel.ReferenceTombstone> Tombstones = [];
}
