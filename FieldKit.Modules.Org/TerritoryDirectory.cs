using FieldKit.Modules.Org.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Org;

/// <summary>
/// Answers <see cref="ITerritoryDirectory"/> from the membership table (<c>ORG-05</c>).
/// </summary>
/// <remarks>
/// <para>
/// One join, one query, whatever the size of the ask — the contract is bulk so that this can be.
/// </para>
/// <para>
/// <b>It reads only Organization's own schema, and that is a rule rather than a coincidence.</b>
/// The moment this reached back into Outlets, a call from Outlets into here could return through
/// Outlets again: mutual recursion between two modules that each look correct on their own. AT-1
/// cannot see that — both assembly references would be legal — so <b>AT-10</b> checks it instead, by
/// asserting the graph of contract implementations depending on other modules' contracts stays
/// acyclic.
/// </para>
/// </remarks>
internal sealed class TerritoryDirectory(OrgDbContext db) : ITerritoryDirectory
{
    public async Task<IReadOnlyDictionary<Guid, TerritoryDescriptor>> ForOutletsAsync(
        IReadOnlyCollection<Guid> outletIds, CancellationToken cancellationToken = default)
    {
        if (outletIds.Count == 0) return new Dictionary<Guid, TerritoryDescriptor>();

        // Distinct because a caller assembling ids from a page of rows should not have to, and a
        // duplicate would otherwise throw on ToDictionary rather than simply being the same answer.
        var wanted = outletIds.Distinct().ToArray();

        var memberships = await db.TerritoryOutlets
            .Where(membership => wanted.Contains(membership.OutletId))
            .Join(
                db.Territories,
                membership => membership.TerritoryId,
                territory => territory.Id,
                (membership, territory) => new { membership.OutletId, territory.Id, territory.Name })
            .ToListAsync(cancellationToken);

        // The tenant filter applies to both sides, so an outlet id belonging to another tenant simply
        // finds nothing — the same answer as an outlet nobody has assigned yet, which is the right
        // one: this must not become a way to confirm that an id exists somewhere else.
        return memberships.ToDictionary(
            row => row.OutletId,
            row => new TerritoryDescriptor(row.Id, row.Name));
    }

    public async Task<IReadOnlyList<Guid>> OutletsInAsync(
        Guid? territoryId, CancellationToken cancellationToken = default)
    {
        // Membership rows only, with no join to `Territories`: an outlet is in a territory because a
        // row says so, and a territory with no shops contributes nothing either way. The tenant
        // filter on this table is what makes a foreign territory id answer empty rather than error.
        var memberships = db.TerritoryOutlets.AsNoTracking();

        if (territoryId is { } wanted)
        {
            memberships = memberships.Where(membership => membership.TerritoryId == wanted);
        }

        // Distinct even though `BR-ORG-1` makes an outlet's membership unique: the invariant lives in
        // a unique index one migration away from someone relaxing it, and a duplicated id here would
        // be counted twice by every aggregate downstream.
        return await memberships
            .Select(membership => membership.OutletId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }
}
