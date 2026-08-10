using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products.Contracts;

/// <summary>
/// One line of a channel's assortment, as the device holds it (<c>PRD-02</c>).
/// </summary>
/// <remarks>
/// Authored per channel, so it reaches every device unscoped — the same answer visit workflows and
/// the catalogue get. A tenant has a handful of channels, and which products a channel carries is
/// not something one rep may know and another may not.
/// </remarks>
public sealed record AssortmentLineSnapshot(
    Guid Id,
    Guid ChannelId,
    Guid ProductId,
    bool IsMustStock,
    long RowVersion);

/// <summary>
/// One outlet's departure from its channel's list (<c>PRD-02</c>, <c>B2</c>).
/// </summary>
/// <remarks>
/// <see cref="Kind"/> travels by name rather than as an ordinal — an inserted enum value would
/// otherwise turn every stored <c>Removed</c> into an <c>Added</c> on every device, which is a
/// product appearing in an order screen that a buyer has explicitly refused.
/// </remarks>
public sealed record AssortmentOverrideSnapshot(
    Guid Id,
    Guid OutletId,
    Guid ProductId,
    string Kind,
    bool IsMustStock,
    long RowVersion);

/// <summary>One page of assortment changes, for either half of the rule.</summary>
public sealed record AssortmentChangePage<T>(
    IReadOnlyList<T> Upserts,
    IReadOnlyList<ReferenceTombstone> Tombstones,
    long Cursor);

/// <summary>
/// What a rep may sell at a shop, as a delta (<c>OFF-03</c>, W8 slice 8d).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two halves with two different scopes, which is why this interface has three methods rather
/// than one.</b> The channel list is tenant-wide; the overrides belong to individual outlets, and an
/// outlet's overrides are exactly as private as the outlet is.
/// </para>
/// <para>
/// <b>The overrides are the first entity scoped by the device's outlet set</b>, and so the first to
/// need the shape outlets have had since slice 3: an outlet entering a rep's territory brings its
/// overrides with it <i>without editing them</i>, so their row versions sit below the device's
/// cursor and a pure delta would never mention them. Hence
/// <see cref="GetOverrideBaselineAsync"/>.
/// </para>
/// <para>
/// <b>There is no scope tombstone half, and that is a consequence rather than a gap.</b> When an
/// outlet leaves a rep's territory the device is already told so — Sync mints an outlet tombstone —
/// and an override is meaningless without the outlet it qualifies. The device drops both, locally,
/// from a fact it already holds. Minting a second set of tombstones would mean the server
/// enumerating rows it is about to stop being allowed to talk about.
/// </para>
/// <para>
/// <b>The effective assortment is computed on the device, not resolved here.</b> `PRD-02` stores
/// overrides precisely so there is no materialised per-outlet list to keep in step; sending a
/// resolved list would rebuild that materialisation on the wire, and a channel edit would then have
/// to invalidate every outlet it touches.
/// </para>
/// </remarks>
public interface IAssortmentChangeFeed
{
    /// <summary>Channel assortment lines whose row version is above <paramref name="cursor"/>.</summary>
    Task<AssortmentChangePage<AssortmentLineSnapshot>> GetLineChangesAsync(
        long cursor, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Overrides for <paramref name="outletIds"/> whose row version is above
    /// <paramref name="cursor"/>, plus tombstones for any of them deleted since.
    /// </summary>
    /// <param name="outletIds">
    /// The outlets the device already held and still holds. Resolved by the caller, because Products
    /// does not know what a territory is — Organization does.
    /// </param>
    Task<AssortmentChangePage<AssortmentOverrideSnapshot>> GetOverrideChangesAsync(
        long cursor,
        IReadOnlyCollection<Guid> outletIds,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every override on the named outlets as it stands, ignoring any cursor — the first thing a
    /// device is told about outlets that have just entered its scope.
    /// </summary>
    Task<IReadOnlyList<AssortmentOverrideSnapshot>> GetOverrideBaselineAsync(
        IReadOnlyCollection<Guid> outletIds,
        int limit,
        CancellationToken cancellationToken = default);
}
