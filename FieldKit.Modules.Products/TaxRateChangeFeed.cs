using FieldKit.Infrastructure;
using FieldKit.Modules.Products.Contracts;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>
/// Tax's side of the pull protocol (<c>OFF-03</c>, <c>PRD-07</c>) — W11 slice 7b.
/// </summary>
internal sealed class TaxRateChangeFeed(ProductsDbContext db) : ITaxRateChangeFeed
{
    public async Task<TaxRateChangePage> GetChangesAsync(
        long cursor, int limit, CancellationToken cancellationToken = default)
    {
        /*
         * Materialised before the snapshot is shaped, for the reason `PriceChangeFeed` gives:
         * `WireDecimal.From` is .NET string formatting with no SQL translation, so projecting into
         * the snapshot inside the query would fail at runtime rather than at build.
         */
        var rows = await db.TaxRates
            .Where(rate => rate.RowVersion > cursor)
            .OrderBy(rate => rate.RowVersion)
            .Take(limit)
            .Select(rate => new
            {
                rate.Id,
                rate.TaxClassId,
                rate.CountryCode,
                rate.Percentage,
                rate.EffectiveFrom,
                rate.EffectiveTo,
                rate.RowVersion,
            })
            .ToListAsync(cancellationToken);

        var upserts = rows
            .Select(row => new TaxRateSnapshot(
                row.Id,
                row.TaxClassId,
                row.CountryCode,
                WireDecimal.From(row.Percentage),
                row.EffectiveFrom,
                row.EffectiveTo,
                row.RowVersion))
            .ToList();

        var tombstones = await db.Set<Tombstone>()
            .Where(tombstone =>
                tombstone.RowVersion > cursor && tombstone.EntityType == nameof(TaxRate))
            .OrderBy(tombstone => tombstone.RowVersion)
            .Take(limit)
            .Select(tombstone => new ReferenceTombstone(tombstone.EntityId, tombstone.RowVersion))
            .ToListAsync(cancellationToken);

        // The highest version *in this page*, never the table's maximum — reporting the maximum would
        // skip every row between the last one sent and it, and a device would price an order against
        // a partial rate table without anything looking wrong.
        var highest = cursor;

        foreach (var upsert in upserts) highest = Math.Max(highest, upsert.RowVersion);
        if (tombstones.Count > 0) highest = Math.Max(highest, tombstones[^1].RowVersion);

        return new TaxRateChangePage(upserts, tombstones, highest);
    }
}
