using FieldKit.Infrastructure;
using FieldKit.Modules.Products.Contracts;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>
/// Order minimums' side of the pull protocol (<c>OFF-03</c>, <c>ORD-06</c>) — W11 slice 8b-ii.
/// </summary>
internal sealed class OrderMinimumChangeFeed(ProductsDbContext db) : IOrderMinimumChangeFeed
{
    public async Task<OrderMinimumChangePage> GetChangesAsync(
        long cursor, int limit, CancellationToken cancellationToken = default)
    {
        /*
         * Materialised before the snapshot is shaped, for the reason `PriceChangeFeed` and
         * `TaxRateChangeFeed` both give: `WireDecimal.From` is .NET string formatting with no SQL
         * translation, so projecting into the snapshot inside the query fails at runtime rather than
         * at build.
         */
        var rows = await db.OrderMinimums
            .Where(minimum => minimum.RowVersion > cursor)
            .OrderBy(minimum => minimum.RowVersion)
            .Take(limit)
            .Select(minimum => new
            {
                minimum.Id,
                minimum.ChannelId,
                minimum.OutletId,
                minimum.Amount,
                minimum.CurrencyCode,
                minimum.RowVersion,
            })
            .ToListAsync(cancellationToken);

        var upserts = rows
            .Select(row => new OrderMinimumSnapshot(
                row.Id,
                row.ChannelId,
                row.OutletId,
                WireDecimal.From(row.Amount),
                row.CurrencyCode,
                row.RowVersion))
            .ToList();

        var tombstones = await db.Set<Tombstone>()
            .Where(tombstone =>
                tombstone.RowVersion > cursor && tombstone.EntityType == nameof(OrderMinimum))
            .OrderBy(tombstone => tombstone.RowVersion)
            .Take(limit)
            .Select(tombstone => new ReferenceTombstone(tombstone.EntityId, tombstone.RowVersion))
            .ToListAsync(cancellationToken);

        // The highest version *in this page*, never the table's maximum — reporting the maximum
        // would skip every row between the last one sent and it, and a device enforcing half a set
        // of minimums looks exactly like one enforcing all of them.
        var highest = cursor;

        foreach (var upsert in upserts) highest = Math.Max(highest, upsert.RowVersion);
        if (tombstones.Count > 0) highest = Math.Max(highest, tombstones[^1].RowVersion);

        return new OrderMinimumChangePage(upserts, tombstones, highest);
    }
}
