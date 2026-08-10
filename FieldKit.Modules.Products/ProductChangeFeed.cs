using FieldKit.Infrastructure;
using FieldKit.Modules.Products.Contracts;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>
/// Products' side of the pull protocol (<c>OFF-03</c>, W8 slice 8c).
/// </summary>
/// <remarks>
/// The same two-source merge as the other feeds — live rows above the cursor, tombstones above the
/// cursor — with no scope predicate. The tenant filter is the DbContext's, as everywhere; this class
/// never writes one.
/// </remarks>
internal sealed class ProductChangeFeed(ProductsDbContext db) : IProductChangeFeed
{
    public async Task<ProductChangePage> GetChangesAsync(
        long cursor, int limit, CancellationToken cancellationToken = default)
    {
        var upserts = await db.Products
            .Where(product => product.RowVersion > cursor)
            .OrderBy(product => product.RowVersion)
            .Take(limit)
            .Select(product => new ProductSnapshot(
                product.Id,
                product.Sku,
                product.Name,
                product.BrandId,
                product.CategoryId,
                product.TaxClassId,
                product.UnitOfMeasure,
                product.PackSize,
                product.Status.ToString(),
                product.RowVersion))
            .ToListAsync(cancellationToken);

        /*
         * Discontinued products are sent, not filtered.
         *
         * A device holding an order taken last week, or an audit that counted a facing of something
         * the tenant has since dropped, still has to be able to name it. Filtering here would make
         * the row vanish from the device on the next pull — no tombstone, no explanation — and the
         * screen would show an id. Status travels *with* the row so the device can decide what to
         * offer; whether a discontinued product may be ordered is `PRD-02`'s question, not this
         * one's.
         */
        var tombstones = await db.Set<Tombstone>()
            .Where(tombstone => tombstone.RowVersion > cursor && tombstone.EntityType == nameof(Product))
            .OrderBy(tombstone => tombstone.RowVersion)
            .Take(limit)
            .Select(tombstone => new ReferenceTombstone(tombstone.EntityId, tombstone.RowVersion))
            .ToListAsync(cancellationToken);

        // The highest version *in this page*, never the table's maximum — a truncated page must
        // resume rather than skip everything between the last row sent and the high-water mark. This
        // is the feed most likely to truncate: a first sync of a real catalogue fills every page.
        var highest = cursor;
        if (upserts.Count > 0) highest = Math.Max(highest, upserts[^1].RowVersion);
        if (tombstones.Count > 0) highest = Math.Max(highest, tombstones[^1].RowVersion);

        return new ProductChangePage(upserts, tombstones, highest);
    }
}
