using FieldKit.Infrastructure;
using FieldKit.Modules.Products.Contracts;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>
/// Promotions' side of the pull protocol (<c>OFF-03</c>, W8 slice 8f).
/// </summary>
internal sealed class PromotionChangeFeed(ProductsDbContext db) : IPromotionChangeFeed
{
    public async Task<PromotionChangePage<PromotionSnapshot>> GetChangesAsync(
        long cursor, int limit, CancellationToken cancellationToken = default)
    {
        var changed = await db.Promotions
            .Where(promotion => promotion.RowVersion > cursor)
            .OrderBy(promotion => promotion.RowVersion)
            .Take(limit)
            .ToListAsync(cancellationToken);

        /*
         * Children are read in two queries for the page, not one per promotion.
         *
         * They are separate tables with no navigation property on the aggregate — Products reaches
         * them through their own DbSets everywhere — so `Include` is not available and a loop would
         * be 2N queries for a page that can be 500 rows.
         */
        var ids = changed.Select(promotion => promotion.Id).ToList();

        var targets = ids.Count == 0
            ? []
            : await db.PromotionTargets
                .Where(target => ids.Contains(target.PromotionId))
                .ToListAsync(cancellationToken);

        var tiers = ids.Count == 0
            ? []
            : await db.PromotionTiers
                .Where(tier => ids.Contains(tier.PromotionId))
                .OrderBy(tier => tier.MinQuantity)
                .ToListAsync(cancellationToken);

        var targetsByPromotion = targets.ToLookup(target => target.PromotionId);
        var tiersByPromotion = tiers.ToLookup(tier => tier.PromotionId);

        var upserts = changed
            .Select(promotion => Describe(
                promotion, targetsByPromotion[promotion.Id], tiersByPromotion[promotion.Id]))
            .ToList();

        var tombstones = await TombstonesAsync(nameof(Promotion), cursor, limit, cancellationToken);

        return new PromotionChangePage<PromotionSnapshot>(
            upserts, tombstones, Highest(cursor, upserts.Select(u => u.RowVersion), tombstones));
    }

    public async Task<PromotionChangePage<PromotionAssignmentSnapshot>> GetAssignmentChangesAsync(
        long cursor,
        IReadOnlyCollection<Guid> outletIds,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var ids = outletIds as IReadOnlyList<Guid> ?? [.. outletIds];

        // Channel assignments always; outlet assignments only for outlets this device holds. An
        // empty outlet set is *not* an empty answer, for the reason price assignments are not:
        // a rep with no territory still needs the channel-level policy.
        var upserts = await db.PromotionAssignments
            .Where(assignment => assignment.RowVersion > cursor
                && (assignment.OutletId == null || ids.Contains(assignment.OutletId.Value)))
            .OrderBy(assignment => assignment.RowVersion)
            .Take(limit)
            .Select(assignment => Describe(assignment))
            .ToListAsync(cancellationToken);

        var tombstones = await TombstonesAsync(
            nameof(PromotionAssignment), cursor, limit, cancellationToken);

        return new PromotionChangePage<PromotionAssignmentSnapshot>(
            upserts, tombstones, Highest(cursor, upserts.Select(u => u.RowVersion), tombstones));
    }

    public async Task<IReadOnlyList<PromotionAssignmentSnapshot>> GetAssignmentBaselineAsync(
        IReadOnlyCollection<Guid> outletIds,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (outletIds.Count == 0) return [];

        var ids = outletIds as IReadOnlyList<Guid> ?? [.. outletIds];

        return await db.PromotionAssignments
            .Where(assignment => assignment.OutletId != null && ids.Contains(assignment.OutletId.Value))
            .OrderBy(assignment => assignment.RowVersion)
            .Take(limit)
            .Select(assignment => Describe(assignment))
            .ToListAsync(cancellationToken);
    }

    private static PromotionSnapshot Describe(
        Promotion promotion,
        IEnumerable<PromotionTarget> targets,
        IEnumerable<PromotionTier> tiers) => new(
        promotion.Id,
        promotion.Name,
        promotion.Type.ToString(),
        promotion.PercentOff,
        promotion.AmountOff,
        promotion.Currency,
        promotion.BuyQuantity,
        promotion.GetQuantity,
        promotion.GetPercentOff,
        promotion.GetProductId,
        promotion.ValidFrom,
        promotion.ValidTo,
        promotion.Priority,
        [.. targets.Select(target => new PromotionTargetSnapshot(target.ProductId, target.CategoryId))],
        [.. tiers
            .OrderBy(tier => tier.MinQuantity)
            .Select(tier => new PromotionTierSnapshot(
                tier.MinQuantity, tier.PercentOff, tier.AmountOff, tier.Currency))],
        promotion.RowVersion);

    private static PromotionAssignmentSnapshot Describe(PromotionAssignment assignment) => new(
        assignment.Id,
        assignment.PromotionId,
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
