using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products.Contracts;

/// <summary>A price list header, as the device holds it (<c>PRD-03</c>).</summary>
/// <remarks>
/// The effective window travels because the device resolves prices itself, and `BR-PRD-2` picks the
/// list that is in effect <i>on the day of the order</i> — which for a device working offline may
/// not be the day it last synced.
/// </remarks>
public sealed record PriceListSnapshot(
    Guid Id,
    string Name,
    string Currency,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    long RowVersion);

/// <summary>One product's price on one list.</summary>
public sealed record PriceLineSnapshot(
    Guid Id,
    Guid PriceListId,
    Guid ProductId,
    decimal Amount,
    long RowVersion);

/// <summary>
/// Which list applies where (<c>PRD-03</c>, <c>BR-PRD-2</c>).
/// </summary>
/// <remarks>
/// Exactly one of <see cref="ChannelId"/> and <see cref="OutletId"/> is set, and which one decides
/// both the precedence (outlet beats channel) and — for this protocol — who may see it.
/// </remarks>
public sealed record PriceAssignmentSnapshot(
    Guid Id,
    Guid PriceListId,
    Guid? ChannelId,
    Guid? OutletId,
    long RowVersion);

/// <summary>One page of pricing changes.</summary>
public sealed record PriceChangePage<T>(
    IReadOnlyList<T> Upserts,
    IReadOnlyList<ReferenceTombstone> Tombstones,
    long Cursor);

/// <summary>
/// The prices a device should hold, as a delta (<c>OFF-03</c>, W8 slice 8e).
/// </summary>
/// <remarks>
/// <para>
/// <b>Three shapes, and the split follows the same line as the assortment's.</b> Lists and their
/// lines are tenant-wide; assignments are split, because a channel assignment is a tenant's pricing
/// policy and an outlet assignment is a fact about one shop.
/// </para>
/// <para>
/// <b>Lists and lines go to every device, and this is the least comfortable scoping decision in the
/// engine.</b> It means a rep's phone holds price lists for regions and channels they never visit.
/// The alternative — narrowing to the lists assigned to this rep's outlets and their channels —
/// needs a per-device record of which lists were sent, because a list enters scope when an
/// <i>assignment</i> changes rather than when the list does, and a pure delta would never mention
/// it. That is a second scope-set table and a baseline, for the first entity where the leak is a
/// commercial one rather than a privacy one.
/// </para>
/// <para>
/// It is recorded as a limitation rather than defended as a design: what is on a device is
/// tenant-internal, a rep can already read the price of everything they sell, and prices are not
/// personal data. If a tenant ever objects, the narrowing above is what to build, and the entity
/// that makes it tractable is the assignment — resolve the list ids from the device's outlets and
/// their channels, and store <i>that</i> set per device the way outlets already are.
/// </para>
/// </remarks>
public interface IPriceChangeFeed
{
    /// <summary>Price lists whose row version is above <paramref name="cursor"/>.</summary>
    Task<PriceChangePage<PriceListSnapshot>> GetListChangesAsync(
        long cursor, int limit, CancellationToken cancellationToken = default);

    /// <summary>Price lines whose row version is above <paramref name="cursor"/>.</summary>
    Task<PriceChangePage<PriceLineSnapshot>> GetLineChangesAsync(
        long cursor, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Assignments above <paramref name="cursor"/>: every channel assignment, plus the outlet
    /// assignments for <paramref name="outletIds"/>.
    /// </summary>
    Task<PriceChangePage<PriceAssignmentSnapshot>> GetAssignmentChangesAsync(
        long cursor,
        IReadOnlyCollection<Guid> outletIds,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every outlet assignment on the named outlets as it stands, ignoring any cursor — what a
    /// device is told about shops that have just entered its scope.
    /// </summary>
    Task<IReadOnlyList<PriceAssignmentSnapshot>> GetAssignmentBaselineAsync(
        IReadOnlyCollection<Guid> outletIds,
        int limit,
        CancellationToken cancellationToken = default);
}
