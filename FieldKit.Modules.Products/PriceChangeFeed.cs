using FieldKit.Infrastructure;
using FieldKit.Modules.Products.Contracts;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>
/// Pricing's side of the pull protocol (<c>OFF-03</c>, W8 slice 8e).
/// </summary>
internal sealed class PriceChangeFeed(ProductsDbContext db) : IPriceChangeFeed
{
    public async Task<PriceChangePage<PriceListSnapshot>> GetListChangesAsync(
        long cursor, int limit, CancellationToken cancellationToken = default)
    {
        var upserts = await db.PriceLists
            .Where(list => list.RowVersion > cursor)
            .OrderBy(list => list.RowVersion)
            .Take(limit)
            .Select(list => new PriceListSnapshot(
                list.Id, list.Name, list.Currency, list.EffectiveFrom, list.EffectiveTo, list.RowVersion))
            .ToListAsync(cancellationToken);

        var tombstones = await TombstonesAsync(nameof(PriceList), cursor, limit, cancellationToken);

        return new PriceChangePage<PriceListSnapshot>(
            upserts, tombstones, Highest(cursor, upserts.Select(u => u.RowVersion), tombstones));
    }

    public async Task<PriceChangePage<PriceLineSnapshot>> GetLineChangesAsync(
        long cursor, int limit, CancellationToken cancellationToken = default)
    {
        /*
         * Materialised before the snapshot is built, which the amount's string form forces.
         *
         * `WireDecimal.From` is invariant-culture .NET formatting and has no SQL translation, so
         * projecting into the snapshot inside the query would fail at runtime rather than at build.
         * Reading the four columns first and shaping them in memory costs nothing here — the page is
         * already bounded by `limit`.
         */
        var rows = await db.PriceListLines
            .Where(line => line.RowVersion > cursor)
            .OrderBy(line => line.RowVersion)
            .Take(limit)
            .Select(line => new
            {
                line.Id,
                line.PriceListId,
                line.ProductId,
                line.Amount,
                line.RowVersion,
            })
            .ToListAsync(cancellationToken);

        var upserts = rows
            .Select(row => new PriceLineSnapshot(
                row.Id,
                row.PriceListId,
                row.ProductId,
                WireDecimal.From(row.Amount),
                row.RowVersion))
            .ToList();

        /*
         * Lines are the largest thing this protocol carries — a list times a catalogue — and the one
         * most likely to fill every page on a first sync. Which is why the cursor rule below matters
         * more here than anywhere: reporting the table maximum would skip every line between the
         * last one sent and that maximum, and a device would price an order from a partial list
         * without anything looking wrong.
         */
        var tombstones = await TombstonesAsync(nameof(PriceListLine), cursor, limit, cancellationToken);

        return new PriceChangePage<PriceLineSnapshot>(
            upserts, tombstones, Highest(cursor, upserts.Select(u => u.RowVersion), tombstones));
    }

    public async Task<PriceChangePage<PriceAssignmentSnapshot>> GetAssignmentChangesAsync(
        long cursor,
        IReadOnlyCollection<Guid> outletIds,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var ids = outletIds as IReadOnlyList<Guid> ?? [.. outletIds];

        /*
         * Channel assignments always; outlet assignments only for outlets this device holds.
         *
         * An empty outlet set does *not* make this empty — a rep with no territory still needs the
         * channel-level pricing policy, because the outlets they are given tomorrow will be priced
         * by it. That is the one place this differs from the assortment override feed, where an
         * empty scope really does mean nothing to send.
         */
        var upserts = await db.PriceListAssignments
            .Where(assignment => assignment.RowVersion > cursor
                && (assignment.OutletId == null || ids.Contains(assignment.OutletId.Value)))
            .OrderBy(assignment => assignment.RowVersion)
            .Take(limit)
            .Select(assignment => Describe(assignment))
            .ToListAsync(cancellationToken);

        var tombstones = await TombstonesAsync(
            nameof(PriceListAssignment), cursor, limit, cancellationToken);

        return new PriceChangePage<PriceAssignmentSnapshot>(
            upserts, tombstones, Highest(cursor, upserts.Select(u => u.RowVersion), tombstones));
    }

    public async Task<IReadOnlyList<PriceAssignmentSnapshot>> GetAssignmentBaselineAsync(
        IReadOnlyCollection<Guid> outletIds,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (outletIds.Count == 0) return [];

        var ids = outletIds as IReadOnlyList<Guid> ?? [.. outletIds];

        // Outlet assignments only. A channel assignment is never *entering* — it was already being
        // sent, unscoped, on every previous pull.
        return await db.PriceListAssignments
            .Where(assignment => assignment.OutletId != null && ids.Contains(assignment.OutletId.Value))
            .OrderBy(assignment => assignment.RowVersion)
            .Take(limit)
            .Select(assignment => Describe(assignment))
            .ToListAsync(cancellationToken);
    }

    private static PriceAssignmentSnapshot Describe(PriceListAssignment assignment) => new(
        assignment.Id,
        assignment.PriceListId,
        assignment.ChannelId,
        assignment.OutletId,
        assignment.RowVersion);

    private Task<List<ReferenceTombstone>> TombstonesAsync(
        string entityType, long cursor, int limit, CancellationToken cancellationToken) =>
        db.Set<Tombstone>()
            .Where(tombstone => tombstone.RowVersion > cursor && tombstone.EntityType == entityType)
            .OrderBy(tombstone => tombstone.RowVersion)
            .Take(limit)
            .Select(tombstone => new ReferenceTombstone(tombstone.EntityId, tombstone.RowVersion))
            .ToListAsync(cancellationToken);

    /// <summary>The highest version <b>in this page</b>, never the table's maximum.</summary>
    private static long Highest(
        long cursor, IEnumerable<long> upserts, IReadOnlyList<ReferenceTombstone> tombstones)
    {
        var highest = cursor;

        foreach (var version in upserts) highest = Math.Max(highest, version);
        if (tombstones.Count > 0) highest = Math.Max(highest, tombstones[^1].RowVersion);

        return highest;
    }
}
