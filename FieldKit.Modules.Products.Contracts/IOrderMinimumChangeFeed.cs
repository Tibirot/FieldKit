using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products.Contracts;

/// <summary>
/// The smallest order one channel or one shop may place, as the device holds it (<c>ORD-06</c>) —
/// W11 slice 8b-ii.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Amount"/> is a <c>string</c></b>, by the rule W11 slice 7a established for this
/// whole protocol: a bare <c>150.00</c> is a JSON number, and <c>JSON.parse</c> makes an IEEE-754
/// float of it before <c>decimal.js</c> is handed anything. A threshold is the one number on this
/// feed where being out by a hundredth decides whether a rep may send their order at all.
/// </para>
/// <para>
/// <b>The currency travels with it</b>, which no other reference row on this feed needs. An order's
/// currency comes from the list that priced it (<c>BR-ORD-7</c>); comparing 50 EUR against 200 RON
/// by their numbers alone would refuse orders comfortably over the threshold while looking like the
/// rule working, so the device has to be able to tell they disagree.
/// </para>
/// <para>
/// <b>Exactly one of the two scope ids is set</b>, and the device relies on that to rank them —
/// outlet beats channel, the same precedence a price list has. A row with both would be a rule with
/// two scopes; the database refuses it and the authoring endpoint refuses it by name.
/// </para>
/// </remarks>
public sealed record OrderMinimumSnapshot(
    Guid Id,
    Guid? ChannelId,
    Guid? OutletId,
    string Amount,
    string CurrencyCode,
    long RowVersion);

/// <summary>One page of order-minimum changes.</summary>
public sealed record OrderMinimumChangePage(
    IReadOnlyList<OrderMinimumSnapshot> Upserts,
    IReadOnlyList<ReferenceTombstone> Tombstones,
    long Cursor);

/// <summary>
/// The order minimums a device should hold, as a delta (<c>OFF-03</c>, <c>ORD-06</c>) — W11 slice
/// 8b-ii.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenant-wide, not territory-scoped</b>, like the price lists and tax rates beside it. A minimum
/// is a statement about a channel or a shop rather than about a rep, and narrowing it would need a
/// per-device record of which channels are reachable through the rep's territory — which changes
/// when the territory does rather than when a minimum does.
/// </para>
/// <para>
/// <b>Tombstones matter here more than the row count suggests.</b> The authoring PUT replaces the
/// whole set, so an administrator correcting one figure deletes and recreates every row — and a
/// device that only ever upserted would keep enforcing a threshold its tenant withdrew, refusing
/// orders a rep is entitled to send. That is the worst failure this feed has: silent, and it looks
/// like the rule working.
/// </para>
/// </remarks>
public interface IOrderMinimumChangeFeed
{
    /// <summary>Order minimums whose row version is above <paramref name="cursor"/>.</summary>
    Task<OrderMinimumChangePage> GetChangesAsync(
        long cursor, int limit, CancellationToken cancellationToken = default);
}
