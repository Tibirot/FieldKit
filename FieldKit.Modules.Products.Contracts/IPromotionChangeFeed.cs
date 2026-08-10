using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products.Contracts;

/// <summary>What a promotion applies to: a product, or a category (<c>PRD-05</c>).</summary>
/// <remarks>Exactly one id is set. An empty target list means the promotion applies to everything.</remarks>
public sealed record PromotionTargetSnapshot(Guid? ProductId, Guid? CategoryId);

/// <summary>One threshold of a volume promotion. Ordered by <see cref="MinQuantity"/>.</summary>
public sealed record PromotionTierSnapshot(
    int MinQuantity, decimal? PercentOff, decimal? AmountOff, string? Currency);

/// <summary>
/// One promotion as the device holds it (<c>PRD-05</c>, sync engine §3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Targets and tiers travel inside it</b>, the way a visit workflow's steps do and for a sharper
/// version of the same reason: a device holding four of five tiers does not fail, it computes a
/// <i>different discount</i> — and neither the rep nor the shop has any way to notice. Sending the
/// aggregate as one row makes a partial promotion unrepresentable rather than merely unlikely.
/// </para>
/// <para>
/// <see cref="Type"/> travels by name. An ordinal would silently reinterpret every stored promotion
/// the day a value is inserted into that enum — a percentage becoming a fixed amount, on a device
/// that is offline and cannot be told.
/// </para>
/// </remarks>
public sealed record PromotionSnapshot(
    Guid Id,
    string Name,
    string Type,
    decimal? PercentOff,
    decimal? AmountOff,
    string? Currency,
    int? BuyQuantity,
    int? GetQuantity,
    decimal? GetPercentOff,
    Guid? GetProductId,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    int Priority,
    IReadOnlyList<PromotionTargetSnapshot> Targets,
    IReadOnlyList<PromotionTierSnapshot> Tiers,
    long RowVersion);

/// <summary>Which promotion applies where. Exactly one of the two ids is set.</summary>
public sealed record PromotionAssignmentSnapshot(
    Guid Id,
    Guid PromotionId,
    Guid? ChannelId,
    Guid? OutletId,
    long RowVersion);

/// <summary>One page of promotion changes.</summary>
public sealed record PromotionChangePage<T>(
    IReadOnlyList<T> Upserts,
    IReadOnlyList<ReferenceTombstone> Tombstones,
    long Cursor);

/// <summary>
/// The promotions a device should hold, as a delta (<c>OFF-03</c>, W8 slice 8f).
/// </summary>
/// <remarks>
/// <para>
/// The last reference entity, and it reuses the split established by prices: the promotions
/// themselves are tenant-wide, the assignments are channel-or-outlet and therefore scoped one row at
/// a time.
/// </para>
/// <para>
/// <b>Expired promotions are sent, not filtered.</b> A device pricing an order dated last Tuesday
/// needs the promotion that was running last Tuesday, and `BR-PRD-4` resolves against the order's
/// date rather than today's. Filtering here would make a device that has been offline for a week
/// compute a different total from the server for the same order — which is precisely the
/// disagreement the parity suite exists to prevent, arriving through the sync layer instead.
/// </para>
/// </remarks>
public interface IPromotionChangeFeed
{
    /// <summary>Promotions whose row version is above <paramref name="cursor"/>, with their children.</summary>
    Task<PromotionChangePage<PromotionSnapshot>> GetChangesAsync(
        long cursor, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Assignments above <paramref name="cursor"/>: every channel assignment, plus the outlet
    /// assignments for <paramref name="outletIds"/>.
    /// </summary>
    Task<PromotionChangePage<PromotionAssignmentSnapshot>> GetAssignmentChangesAsync(
        long cursor,
        IReadOnlyCollection<Guid> outletIds,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Every outlet assignment on the named outlets, ignoring any cursor.</summary>
    Task<IReadOnlyList<PromotionAssignmentSnapshot>> GetAssignmentBaselineAsync(
        IReadOnlyCollection<Guid> outletIds,
        int limit,
        CancellationToken cancellationToken = default);
}
