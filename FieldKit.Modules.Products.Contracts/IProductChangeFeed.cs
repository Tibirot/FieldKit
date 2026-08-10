using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products.Contracts;

/// <summary>
/// One product as the device holds it (<c>PRD-01</c>, sync engine §3).
/// </summary>
/// <remarks>
/// <para>
/// Enough to <b>name and count</b> a product, which is what a rep standing in a shop does: find it
/// in a list, say how many are on the shelf, put it on an order. The classification ids travel
/// because a screen groups by them; the classification <i>names</i> do not, because that would be
/// three joins to save the device one lookup it has no reason to do yet.
/// </para>
/// <para>
/// <see cref="Status"/> is a string, not the enum ordinal. Serialised, an ordinal would be silently
/// reinterpreted the day a value is inserted into the middle of that list — and a discontinued
/// product would read as active on every device already holding it.
/// </para>
/// </remarks>
public sealed record ProductSnapshot(
    Guid Id,
    string Sku,
    string Name,
    Guid? BrandId,
    Guid? CategoryId,
    Guid? TaxClassId,
    string? UnitOfMeasure,
    int? PackSize,
    string Status,
    long RowVersion);

/// <summary>One page of catalogue changes: what to upsert, what to drop, and how far the device is.</summary>
public sealed record ProductChangePage(
    IReadOnlyList<ProductSnapshot> Upserts,
    IReadOnlyList<ReferenceTombstone> Tombstones,
    long Cursor);

/// <summary>
/// The products a device should hold, as a delta (<c>OFF-03</c>, W8 slice 8c).
/// </summary>
/// <remarks>
/// <para>
/// <b>No scope argument.</b> The whole catalogue goes to every device in the tenant — the same
/// answer visit workflows get, for the same reason and one more of its own.
/// </para>
/// <para>
/// The narrowing on offer is the <i>assortment</i>: the products that apply at the outlets this rep
/// covers. It was rejected because a rep standing in a shop has to be able to <b>name what they are
/// looking at</b>. An unplanned call, a shop whose assortment changed this morning, a competitor
/// facing that turns out to be one of ours — in each case the device needs the product row, and a
/// scoped catalogue would give a blank where a name should be. The failure would look like missing
/// data rather than like a decision somebody made.
/// </para>
/// <para>
/// It would also cost what every id-set scope costs: a second scope-set table per device, a baseline
/// method, and the membership problem in full — an outlet joining an assortment brings products into
/// scope <i>without editing them</i>, so a pure delta would never send them.
/// </para>
/// <para>
/// <b>What this does not settle is what a rep may sell.</b> Holding a product is not being allowed to
/// order it; the assortment decides that (<c>PRD-02</c>), and it reaches the device as its own
/// entity in a later slice. Catalogue and permission are different questions and this answers only
/// the first.
/// </para>
/// <para>
/// <b>The one honest cost is a first sync.</b> A tenant with five thousand SKUs sends five thousand
/// rows once, paged. Every sync after that is a delta of what an administrator changed, which is
/// nearly always nothing.
/// </para>
/// </remarks>
public interface IProductChangeFeed
{
    /// <summary>
    /// Products whose row version is above <paramref name="cursor"/>, plus tombstones for any
    /// deleted since.
    /// </summary>
    Task<ProductChangePage> GetChangesAsync(
        long cursor, int limit, CancellationToken cancellationToken = default);
}
