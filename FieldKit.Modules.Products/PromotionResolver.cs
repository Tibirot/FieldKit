namespace FieldKit.Modules.Products;

/// <summary>One threshold of a tiered candidate, as the resolver sees it.</summary>
public sealed record PromotionTierCandidate(
    int MinQuantity, decimal? PercentOff, decimal? AmountOff, string? Currency);

/// <summary>What a <see cref="PromotionType.BuyXGetY"/> candidate gives away.</summary>
public sealed record BundleCandidate(
    int BuyQuantity, int GetQuantity, decimal GetPercentOff, Guid? GetProductId);

/// <summary>
/// One promotion that could apply to a line, and everything needed to decide whether it does.
/// </summary>
/// <remarks>
/// Flat and self-contained, like <see cref="PriceCandidate"/>: no ids to follow, no entities, nothing
/// to load. That is what lets <see cref="PromotionResolver"/> be a pure function over data the caller
/// has already gathered, and what lets the same shape exist in TypeScript for the device mirror
/// (<c>PRD-08</c>).
/// <para>
/// <b>Scope is not here, and its absence is the point.</b> A price candidate carries a
/// <see cref="PriceScope"/> because <c>BR-PRD-2</c> ranks outlet above channel. <c>BR-PRD-3</c> ranks
/// nothing of the sort — it selects by <b>priority</b> — so whether a promotion reached this outlet
/// directly or through its channel changes nothing about which one wins. Scope is therefore a filter
/// the caller applies while gathering, not a field the resolver reasons about. Carrying it anyway
/// would invite exactly the precedence rule the spec does not have.
/// </para>
/// </remarks>
public sealed record PromotionCandidate(
    Guid PromotionId,
    PromotionType Type,
    int Priority,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    decimal? PercentOff = null,
    decimal? AmountOff = null,
    string? Currency = null,
    IReadOnlyList<PromotionTierCandidate>? Tiers = null,
    BundleCandidate? Bundle = null);

/// <summary>
/// The promotion that applies to a line, with its tier already chosen.
/// </summary>
/// <remarks>
/// A <see cref="PromotionType.VolumeTiered"/> promotion resolves to a concrete
/// <see cref="PercentOff"/> or <see cref="AmountOff"/> — the tier the quantity reached. The caller
/// gets a discount, not a table to search a second time, which is what keeps the tier-selection rule
/// in one place instead of in every consumer.
/// </remarks>
public sealed record ResolvedPromotion(
    Guid PromotionId,
    PromotionType Type,
    int Priority,
    decimal? PercentOff,
    decimal? AmountOff,
    string? Currency,
    BundleCandidate? Bundle);

/// <summary>
/// Picks the one promotion that applies to an order line (<c>PRD-06</c>, <c>BR-PRD-3</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure and side-effect-free</b> (<c>BR-PRD-7</c>), for the same reasons as
/// <see cref="PriceResolver"/>: no database, no clock, no tenant context. The date is a parameter
/// rather than a clock read because resolution has to be reproducible — an order re-priced during
/// sync must select the promotion it was taken under.
/// </para>
/// <para>
/// <b><c>BR-PRD-6</c> lands on that parameter.</b> A promotion's window is evaluated in the
/// <i>outlet's</i> timezone, and a function that asked what day it is would answer in the server's.
/// So the business date is computed where the timezone is known — on the device, or by a caller
/// holding the outlet — and handed in. Making it a parameter is not a way of avoiding the rule; it is
/// the only shape in which the rule can be obeyed by both engines at once.
/// </para>
/// <para>
/// <b>Line-level only.</b> <c>BR-PRD-3</c> allows at most one line-level promotion per line, which is
/// what this returns. Order-level promotions are separate and additive
/// (<see href="../../docs/product/decisions-and-assumptions.md">B1</see>); they arrive with Order in
/// Phase 3 and will not come through here.
/// </para>
/// </remarks>
public static class PromotionResolver
{
    /// <summary>
    /// The promotion that applies at <paramref name="quantity"/> on <paramref name="on"/>, or null
    /// when none does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order of preference, per <c>BR-PRD-3</c>:
    /// </para>
    /// <list type="number">
    /// <item>candidates whose window covers the date — half-open, as everywhere else;</item>
    /// <item>candidates that actually <i>do something</i> at this quantity. A tiered promotion whose
    /// lowest threshold is 6 does not apply to a line of 3, and a buy-two-get-one does not apply to a
    /// line of one. Filtered out rather than allowed to win and then take nothing off — the same
    /// hazard authoring refuses when it rejects a 0% discount;</item>
    /// <item>highest <see cref="PromotionCandidate.Priority"/> wins;</item>
    /// <item>still tied, the higher <c>PromotionId</c> wins, ordered as big-endian bytes.</item>
    /// </list>
    /// <para>
    /// The last rule is the one <see cref="PriceResolver"/> explains at length, and it is the same
    /// rule for the same reason: a tie is a data problem no tiebreak makes <i>right</i>, but
    /// determinism is what stops a rep and a supervisor seeing different answers for one shop. Byte
    /// order rather than the platform's Guid comparison, because .NET's sorts <c>ffffffff-…</c> below
    /// <c>00000001-…</c> and a TypeScript mirror comparing canonical strings would not.
    /// </para>
    /// </remarks>
    public static ResolvedPromotion? Resolve(
        IReadOnlyList<PromotionCandidate> candidates, int quantity, DateOnly on)
    {
        PromotionCandidate? winner = null;
        PromotionTierCandidate? winningTier = null;

        foreach (var candidate in candidates)
        {
            if (!Covers(candidate, on)) continue;

            // Chosen before the comparison, not after: for a tiered candidate this is also the test
            // of whether it applies at all, so a promotion with no reachable tier never enters the
            // priority contest it would otherwise win and then do nothing with.
            var tier = BestTier(candidate, quantity);
            if (!Applies(candidate, quantity, tier)) continue;

            if (winner is not null && !Beats(candidate, winner)) continue;

            winner = candidate;
            winningTier = tier;
        }

        if (winner is null) return null;

        // A tiered candidate resolves to the tier's discount; every other type to its own.
        return new ResolvedPromotion(
            winner.PromotionId,
            winner.Type,
            winner.Priority,
            winningTier?.PercentOff ?? winner.PercentOff,
            winningTier?.AmountOff ?? winner.AmountOff,
            winningTier?.Currency ?? winner.Currency,
            winner.Bundle);
    }

    /// <summary>Half-open: <c>[ValidFrom, ValidTo)</c>, matching <see cref="Promotion.Covers"/>.</summary>
    private static bool Covers(PromotionCandidate candidate, DateOnly on) =>
        on >= candidate.ValidFrom && (candidate.ValidTo is not { } end || on < end);

    /// <summary>
    /// The tier this quantity reaches — the highest threshold at or below it — or null.
    /// </summary>
    /// <remarks>
    /// Highest-reached rather than lowest-matching, because tiers are "N or more" and the author
    /// wrote them expecting the better deal to win as the order grows. A line of 30 against tiers at
    /// 6, 12 and 24 gets the 24 tier.
    /// <para>
    /// Ties on <c>MinQuantity</c> cannot arise — authoring refuses duplicate thresholds — so this
    /// takes the first of an equal pair without a tiebreak. If that rule were ever relaxed, this is
    /// where the non-determinism would appear.
    /// </para>
    /// </remarks>
    private static PromotionTierCandidate? BestTier(PromotionCandidate candidate, int quantity)
    {
        if (candidate.Type != PromotionType.VolumeTiered) return null;

        PromotionTierCandidate? best = null;

        foreach (var tier in candidate.Tiers ?? [])
        {
            if (tier.MinQuantity > quantity) continue;
            if (best is not null && tier.MinQuantity <= best.MinQuantity) continue;

            best = tier;
        }

        return best;
    }

    /// <summary>Whether this candidate does anything at all at this quantity.</summary>
    private static bool Applies(
        PromotionCandidate candidate, int quantity, PromotionTierCandidate? tier) =>
        candidate.Type switch
        {
            // A tiered promotion with no reachable threshold, or with no tiers authored at all, is
            // inert — the same state an untargeted promotion is in, and for the same reason it is
            // filtered rather than refused.
            PromotionType.VolumeTiered => tier is not null,

            // Fewer bought than the offer requires. "Buy two get one" on a line of one is not a
            // discount of zero; it is an offer that has not been earned.
            PromotionType.BuyXGetY => candidate.Bundle is { } bundle
                                      && quantity >= bundle.BuyQuantity,

            // Flat: applies to any line that got this far.
            _ => true,
        };

    /// <summary>
    /// Whether <paramref name="challenger"/> should displace <paramref name="holder"/>.
    /// </summary>
    /// <remarks>
    /// <b>Priority alone, then the id.</b> Notably <i>not</i> the size of the discount: the biggest
    /// saving does not win, the one the tenant ranked highest does. That is what makes priority worth
    /// authoring — a supplier-funded deal can be made to beat a bigger generic one — and it is why
    /// this function never looks at a value.
    /// </remarks>
    private static bool Beats(PromotionCandidate challenger, PromotionCandidate holder)
    {
        if (challenger.Priority != holder.Priority) return challenger.Priority > holder.Priority;

        Span<byte> left = stackalloc byte[16];
        Span<byte> right = stackalloc byte[16];
        challenger.PromotionId.TryWriteBytes(left, bigEndian: true, out _);
        holder.PromotionId.TryWriteBytes(right, bigEndian: true, out _);

        return left.SequenceCompareTo(right) > 0;
    }
}
