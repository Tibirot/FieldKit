using FieldKit.Modules.Org.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Org;

/// <summary>
/// Answers <see cref="IRepScope"/> from assignments and territory membership (<c>ORG-04</c>).
/// </summary>
/// <remarks>
/// <para>
/// One query. The assignment index is <c>(tenant, userId)</c> — put there for exactly this question
/// — and the join to membership is the same one <see cref="TerritoryDirectory"/> walks in the other
/// direction.
/// </para>
/// <para>
/// <b>It reads only Organization's own schema</b>, for the reason spelled out on
/// <see cref="TerritoryDirectory"/>: a contract implementation that reached into another module
/// could be called back through it, and <c>AT-10</c> exists because <c>AT-1</c> cannot see that
/// cycle. Outlet ids are ids here and nothing more — whether an outlet is *closed* is Outlets'
/// answer, and <c>BR-JRN-1</c>'s exclusion of closed outlets is the generator's to apply with
/// <c>IOutletCatalog</c>, not a filter this could silently apply on data it does not own.
/// </para>
/// </remarks>
internal sealed class RepScope(OrgDbContext db) : IRepScope
{
    public async Task<RepCoverage> ForRepAsync(
        string userId, DateOnly day, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return new RepCoverage([], []);

        // The period test, written out rather than through `DateRange.Contains` because this has to
        // run in the database: a null end is "until further notice" and covers every day after the
        // start. Half-open elsewhere in the system, inclusive here — an assignment's `to` is the last
        // day covered, which is what an admin means by "until the 30th" (see RepAssignment).
        var territoryIds = await db.RepAssignments
            .Where(assignment =>
                assignment.UserId == userId
                && assignment.FromDate <= day
                && (assignment.ToDate == null || assignment.ToDate >= day))
            .Select(assignment => assignment.TerritoryId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (territoryIds.Count == 0) return new RepCoverage([], []);

        var outletIds = await db.TerritoryOutlets
            .Where(membership => territoryIds.Contains(membership.TerritoryId))
            .Select(membership => membership.OutletId)
            .Distinct()
            .ToListAsync(cancellationToken);

        // The tenant filter applies to both queries, so a user id from another tenant covers nothing
        // rather than reporting someone else's territories — the same shape TerritoryDirectory
        // relies on, and the reason neither needs a hand-written tenant check.
        return new RepCoverage(territoryIds, outletIds);
    }
}
