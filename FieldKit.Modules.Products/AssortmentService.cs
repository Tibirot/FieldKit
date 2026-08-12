using FieldKit.Modules.Outlets.Contracts;
using FieldKit.Modules.Products.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>
/// Answers what an outlet may be sold (<c>PRD-02</c>, <c>BR-ORD-1</c>) — W11 slice 4b.
/// </summary>
/// <remarks>
/// <para>
/// <b>One implementation, not a second one.</b> The effective assortment — a channel's list with the
/// outlet's own additions and removals applied — is a rule with three edge cases, and
/// <c>AssortmentEndpoints</c> already had it. Writing a leaner version here for Order to call would
/// mean an outlet could be told a product is orderable by one path and not the other, which is the
/// worst possible shape for a disagreement: both answers look right in isolation.
/// </para>
/// <para>
/// So the computation moved here and the endpoint now calls it. Order pays for a join it does not
/// need — the sku and name that a screen renders — and that is the trade taken deliberately. W11
/// slice 2c allowed <c>PricingService</c> a second implementation only because gathering per line
/// meant tens of round trips on a path a rep waits for; nothing like that applies here, where both
/// callers want one outlet's whole list in one query.
/// </para>
/// </remarks>
internal sealed class AssortmentService(ProductsDbContext db, IOutletClassification outlets)
    : IAssortmentService
{
    public async Task<IReadOnlySet<Guid>> AssortedAsync(
        Guid outletId,
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken = default)
    {
        if (productIds.Count == 0) return new HashSet<Guid>();

        /*
         * An outlet this tenant does not have is assorted nothing, rather than an error.
         *
         * The caller has already established the outlet exists — Order reaches it through the visit —
         * so the only way to arrive here with an unknown one is a bug or another tenant's id, and
         * both should answer "you may sell nothing here" rather than leak the difference.
         */
        var classified = await outlets.ClassifyManyAsync([outletId], cancellationToken);

        if (classified.Count == 0) return new HashSet<Guid>();

        var effective = await EffectiveAsync(
            db, outletId, classified[0].ChannelId, cancellationToken);

        var assorted = effective.Select(item => item.ProductId).ToHashSet();

        // Intersected rather than returned whole: the caller asked about its own lines, and handing
        // back an outlet's entire catalogue would make the answer grow with the shop.
        assorted.IntersectWith(productIds);

        return assorted;
    }

    /// <summary>
    /// The channel's assortment with this outlet's overrides applied (<c>PRD-02</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Computed on read rather than materialised. There is no per-outlet list to keep in step, so a
    /// change to the channel assortment is immediately true everywhere it should be — no backfill
    /// that can half-fail and leave two shops disagreeing about the same channel.
    /// </para>
    /// <para>
    /// An <c>Added</c> override for a product already in the channel assortment is not an error; it
    /// wins, which is how an outlet raises a line to must-stock that its channel treats as optional.
    /// A <c>Removed</c> override for a product not in the assortment is inert, and also not an
    /// error — it is what a shop's record looks like after the channel drops a line the shop had
    /// already excluded.
    /// </para>
    /// </remarks>
    internal static async Task<IReadOnlyList<AssortmentItemResponse>> EffectiveAsync(
        ProductsDbContext db, Guid outletId, Guid channelId, CancellationToken ct)
    {
        var fromChannel = await ForChannelAsync(db, channelId, ct);
        var overrides = await OverridesAsync(db, outletId, ct);

        if (overrides.Count == 0) return fromChannel;

        var removed = overrides
            .Where(o => o.Kind is AssortmentOverrideKind.Removed)
            .Select(o => o.ProductId)
            .ToHashSet();

        var added = overrides
            .Where(o => o.Kind is AssortmentOverrideKind.Added)
            .ToDictionary(o => o.ProductId);

        var effective = fromChannel
            .Where(item => !removed.Contains(item.ProductId))
            .Select(item => added.TryGetValue(item.ProductId, out var over)
                ? item with { MustStock = over.MustStock }
                : item)
            .ToList();

        var alreadyIn = effective.Select(item => item.ProductId).ToHashSet();

        effective.AddRange(
            added.Values
                .Where(over => !alreadyIn.Contains(over.ProductId))
                .Select(over => new AssortmentItemResponse(
                    over.ProductId, over.Sku, over.Name, over.MustStock)));

        // Sorted here rather than in SQL, because the set is a union of two queries. Same order the
        // channel read promises: must-stock first, then by SKU.
        return
        [
            .. effective
                .OrderByDescending(item => item.MustStock)
                .ThenBy(item => item.Sku, StringComparer.Ordinal),
        ];
    }

    internal static async Task<IReadOnlyList<AssortmentItemResponse>> ForChannelAsync(
        ProductsDbContext db, Guid channelId, CancellationToken ct) =>
        await db.AssortmentItems
            .Where(item => item.ChannelId == channelId)
            .Join(
                db.Products,
                item => item.ProductId,
                product => product.Id,
                (item, product) => new { item.IsMustStock, product })
            .OrderByDescending(pair => pair.IsMustStock)
            .ThenBy(pair => pair.product.Sku)
            .Select(pair => new AssortmentItemResponse(
                pair.product.Id, pair.product.Sku, pair.product.Name, pair.IsMustStock))
            .ToListAsync(ct);

    internal static async Task<IReadOnlyList<OverrideResponse>> OverridesAsync(
        ProductsDbContext db, Guid outletId, CancellationToken ct) =>
        await db.AssortmentOverrides
            .Where(o => o.OutletId == outletId)
            .Join(
                db.Products,
                o => o.ProductId,
                product => product.Id,
                (o, product) => new { o.Kind, o.IsMustStock, product })
            .OrderBy(pair => pair.product.Sku)
            .Select(pair => new OverrideResponse(
                pair.product.Id, pair.product.Sku, pair.product.Name, pair.Kind, pair.IsMustStock))
            .ToListAsync(ct);
}
