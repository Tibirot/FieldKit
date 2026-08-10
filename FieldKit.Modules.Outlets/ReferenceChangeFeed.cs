using FieldKit.Infrastructure;
using FieldKit.Modules.Outlets.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Outlets;

/// <summary>
/// Outlets' side of the pull protocol (<c>OFF-03</c>, sync engine §3).
/// </summary>
/// <remarks>
/// <para>
/// Reads two sources and merges them: live rows above the cursor, and tombstones above the cursor
/// for outlets the device may still be holding. Both are ordered by the same counter (ADR-0013), so
/// "created then deleted between two pulls" arrives as a tombstone and nothing else, and a device
/// never has to reason about which of two pages won.
/// </para>
/// <para>
/// The tenant filter is the DbContext's, as everywhere — this class never writes a tenant predicate,
/// which is what stops a sync path becoming the one place isolation was hand-rolled.
/// </para>
/// </remarks>
internal sealed class ReferenceChangeFeed(OutletsDbContext db) : IReferenceChangeFeed
{
    public async Task<ReferenceChangePage> GetChangesAsync(
        long cursor,
        IReadOnlyCollection<Guid> outletIds,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // An empty scope is a real answer — a rep with no territory assigned covers nothing — and
        // it must not become "no filter", which would hand them the tenant's entire outlet base.
        if (outletIds.Count == 0) return new ReferenceChangePage([], [], cursor);

        var ids = outletIds as IReadOnlyList<Guid> ?? [.. outletIds];

        var upserts = await db.Outlets
            .Where(outlet => outlet.RowVersion > cursor && ids.Contains(outlet.Id))
            .OrderBy(outlet => outlet.RowVersion)
            .Take(limit)
            .Select(outlet => new OutletSnapshot(
                outlet.Id,
                outlet.Name,
                outlet.ChannelId,
                outlet.Segment,
                outlet.Status.ToString(),
                outlet.Latitude,
                outlet.Longitude,
                outlet.RowVersion))
            .ToListAsync(cancellationToken);

        var tombstones = await db.Set<Tombstone>()
            .Where(tombstone => tombstone.RowVersion > cursor
                && tombstone.EntityType == nameof(Outlet)
                && ids.Contains(tombstone.EntityId))
            .OrderBy(tombstone => tombstone.RowVersion)
            .Take(limit)
            .Select(tombstone => new ReferenceTombstone(tombstone.EntityId, tombstone.RowVersion))
            .ToListAsync(cancellationToken);

        /*
         * The page's cursor is the highest version *in this page*, not the highest in the table.
         *
         * Getting this wrong is how a device loses a row for good. If both lists are truncated at
         * `limit` and the cursor reported the table's maximum, everything between the last row sent
         * and that maximum would be skipped on the next pull — silently, permanently, and only for
         * devices that happened to be far enough behind to fill a page.
         *
         * Taking the max of what was actually sent means a truncated page simply resumes.
         */
        var highest = cursor;
        if (upserts.Count > 0) highest = Math.Max(highest, upserts[^1].RowVersion);
        if (tombstones.Count > 0) highest = Math.Max(highest, tombstones[^1].RowVersion);

        return new ReferenceChangePage(upserts, tombstones, highest);
    }

    public async Task<IReadOnlyList<OutletSnapshot>> GetBaselineAsync(
        IReadOnlyCollection<Guid> outletIds,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (outletIds.Count == 0) return [];

        var ids = outletIds as IReadOnlyList<Guid> ?? [.. outletIds];

        // No cursor predicate at all. These outlets are new to the asking device, so their age is
        // irrelevant — an outlet last edited two years ago is exactly the case a delta cannot serve.
        return await db.Outlets
            .Where(outlet => ids.Contains(outlet.Id))
            .OrderBy(outlet => outlet.RowVersion)
            .Take(limit)
            .Select(outlet => new OutletSnapshot(
                outlet.Id,
                outlet.Name,
                outlet.ChannelId,
                outlet.Segment,
                outlet.Status.ToString(),
                outlet.Latitude,
                outlet.Longitude,
                outlet.RowVersion))
            .ToListAsync(cancellationToken);
    }
}
