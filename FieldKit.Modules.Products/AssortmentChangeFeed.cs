using FieldKit.Infrastructure;
using FieldKit.Modules.Products.Contracts;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>
/// The assortment's side of the pull protocol (<c>OFF-03</c>, W8 slice 8d).
/// </summary>
/// <remarks>
/// Both halves read the same per-tenant counter — they live in the same schema — so their cursors
/// are comparable numbers drawn from one sequence. Nothing relies on that, and nothing should: they
/// are separate watermarks because they advance for separate reasons.
/// </remarks>
internal sealed class AssortmentChangeFeed(ProductsDbContext db) : IAssortmentChangeFeed
{
    public async Task<AssortmentChangePage<AssortmentLineSnapshot>> GetLineChangesAsync(
        long cursor, int limit, CancellationToken cancellationToken = default)
    {
        var upserts = await db.AssortmentItems
            .Where(item => item.RowVersion > cursor)
            .OrderBy(item => item.RowVersion)
            .Take(limit)
            .Select(item => new AssortmentLineSnapshot(
                item.Id, item.ChannelId, item.ProductId, item.IsMustStock, item.RowVersion))
            .ToListAsync(cancellationToken);

        /*
         * Tombstones matter more here than anywhere else so far.
         *
         * Setting a channel's assortment *replaces* it, so an ordinary edit deletes every line that
         * is no longer in the list. Without tombstones a device would accumulate the union of every
         * assortment the channel has ever had, and a rep would be offered products the tenant
         * removed months ago — with no way to tell which.
         */
        var tombstones = await TombstonesAsync(nameof(AssortmentItem), cursor, limit, cancellationToken);

        return new AssortmentChangePage<AssortmentLineSnapshot>(
            upserts, tombstones, Highest(cursor, upserts.Select(u => u.RowVersion), tombstones));
    }

    public async Task<AssortmentChangePage<AssortmentOverrideSnapshot>> GetOverrideChangesAsync(
        long cursor,
        IReadOnlyCollection<Guid> outletIds,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // An empty scope is a real answer — a rep with no territory has no outlets to override —
        // and it must not become "no filter", which would hand them every exception in the tenant.
        if (outletIds.Count == 0)
        {
            return new AssortmentChangePage<AssortmentOverrideSnapshot>([], [], cursor);
        }

        var ids = outletIds as IReadOnlyList<Guid> ?? [.. outletIds];

        var upserts = await db.AssortmentOverrides
            .Where(over => over.RowVersion > cursor && ids.Contains(over.OutletId))
            .OrderBy(over => over.RowVersion)
            .Take(limit)
            .Select(over => Describe(over))
            .ToListAsync(cancellationToken);

        /*
         * Tombstones are narrowed after the read, the way Journey's would have to be — but here it
         * is possible, because an override's outlet is not what was deleted.
         *
         * The row is gone, so there is nothing left to join to an outlet. What survives is the
         * *device's* record of which overrides it was sent, and that lives on the device rather than
         * here. So the server sends every override tombstone above the cursor and lets the device
         * ignore ids it never held — which is safe because an id it never held tells it nothing, and
         * because the alternative is a second scope-set table for a page that is nearly always empty.
         */
        var tombstones = await TombstonesAsync(
            nameof(OutletAssortmentOverride), cursor, limit, cancellationToken);

        return new AssortmentChangePage<AssortmentOverrideSnapshot>(
            upserts, tombstones, Highest(cursor, upserts.Select(u => u.RowVersion), tombstones));
    }

    public async Task<IReadOnlyList<AssortmentOverrideSnapshot>> GetOverrideBaselineAsync(
        IReadOnlyCollection<Guid> outletIds,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (outletIds.Count == 0) return [];

        var ids = outletIds as IReadOnlyList<Guid> ?? [.. outletIds];

        // No cursor predicate at all. These outlets are new to the asking device, so the age of
        // their overrides is irrelevant — one written two years ago is exactly the case a delta
        // cannot serve.
        return await db.AssortmentOverrides
            .Where(over => ids.Contains(over.OutletId))
            .OrderBy(over => over.RowVersion)
            .Take(limit)
            .Select(over => Describe(over))
            .ToListAsync(cancellationToken);
    }

    private static AssortmentOverrideSnapshot Describe(OutletAssortmentOverride over) => new(
        over.Id, over.OutletId, over.ProductId, over.Kind.ToString(), over.IsMustStock, over.RowVersion);

    private Task<List<ReferenceTombstone>> TombstonesAsync(
        string entityType, long cursor, int limit, CancellationToken cancellationToken) =>
        db.Set<Tombstone>()
            .Where(tombstone => tombstone.RowVersion > cursor && tombstone.EntityType == entityType)
            .OrderBy(tombstone => tombstone.RowVersion)
            .Take(limit)
            .Select(tombstone => new ReferenceTombstone(tombstone.EntityId, tombstone.RowVersion))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The highest version <b>in this page</b>, never the table's maximum.
    /// </summary>
    /// <remarks>
    /// A cursor reporting the high-water mark would skip everything between the last row sent and
    /// that mark — permanently, and only for devices far enough behind to fill a page.
    /// </remarks>
    private static long Highest(
        long cursor, IEnumerable<long> upserts, IReadOnlyList<ReferenceTombstone> tombstones)
    {
        var highest = cursor;

        foreach (var version in upserts) highest = Math.Max(highest, version);
        if (tombstones.Count > 0) highest = Math.Max(highest, tombstones[^1].RowVersion);

        return highest;
    }
}
