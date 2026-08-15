using FieldKit.Modules.Outlets.Contracts;
using FieldKit.Modules.Products.Contracts;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>
/// What an order costs, gathered from this tenant's data (<c>ORD-02</c>, <c>ORD-03</c>) — W11
/// slice 2c.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thin, and deliberately boring.</b> Everything that decides anything lives in a pure function
/// with vectors — <see cref="PriceResolver"/>, <see cref="PromotionResolver"/>,
/// <see cref="TaxEngine"/> and <see cref="LinePricing"/>. This gathers candidates and hands them
/// over. Anything in here that reads like a pricing decision is a bug, because the device mirror
/// cannot see it and <c>BR-ORD-2</c> would quietly stop holding.
/// </para>
/// <para>
/// <b>Batched, unlike the endpoints it resembles.</b> <c>PromotionResolutionEndpoints</c> and
/// <c>TaxEndpoints</c> gather for one product because they answer about one product. An order is
/// tens of lines, and a per-line round trip is tens of queries on a path a rep waits for — so the
/// gathering here is by *set*, and that is why it is a second implementation rather than a fourth
/// copy of the first. The single-product endpoints become expressible in terms of this once anything
/// needs them to be; nothing does yet.
/// </para>
/// <para>
/// <b>There is no order-level promotion here.</b> <c>BR-ORD-3</c> allows "one line-level promotion
/// plus an optional order-level one" and <c>B1</c> calls them "separate and additive" — but the model
/// has no such thing. A <see cref="Promotion"/> targets products or categories and reaches an outlet
/// or a channel; nothing marks one as applying to an order total, <c>PRD-05</c> lists four
/// line-level types and no fifth, and no requirement asks for authoring. Inventing the concept here
/// would mean a new field, an authoring screen and a stacking rule, decided inside a pricing slice.
/// It is Products' to add when a requirement asks — the third dependency this week that the plan
/// assumed and the model does not have.
/// </para>
/// </remarks>
internal sealed class PricingService(
    ProductsDbContext db, IOutletClassification classification, PricingMetrics metrics)
    : IPricingService
{
    public async Task<PricedOrder?> PriceAsync(
        Guid outletId,
        DateOnly on,
        IReadOnlyList<LineToPrice> lines,
        CancellationToken cancellationToken = default)
    {
        using var span = ProductsTracing.Pricing(outletId, lines.Count);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            return await ResolveAsync(outletId, on, lines, cancellationToken);
        }
        finally
        {
            /*
             * Both exits, and that is what the `finally` is for here (W13 slice 4).
             *
             * An outlet this tenant does not have returns after one query rather than four, and
             * recording only the long path would bias the distribution towards the expensive case —
             * a p95 that never sees the cheap answers is not a p95. Unlike the one in `/sync/push`,
             * this covers a return a test actually takes.
             */
            metrics.Resolved(db.CurrentTenantId, System.Diagnostics.Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<PricedOrder?> ResolveAsync(
        Guid outletId,
        DateOnly on,
        IReadOnlyList<LineToPrice> lines,
        CancellationToken cancellationToken)
    {
        // Through the contract, because Products cannot see the outlet table (AT-1) — and
        // tenant-filtered by it, so another tenant's outlet is simply absent and reads as null here.
        var classified = await classification.ClassifyManyAsync([outletId], cancellationToken);
        if (classified.Count == 0) return null;

        var channelId = classified[0].ChannelId;
        var countryCode = classified[0].CountryCode;

        var productIds = lines.Select(line => line.ProductId).Distinct().ToList();

        var prices = await PricesAsync(outletId, channelId, on, productIds, cancellationToken);
        var promotions = await PromotionsAsync(outletId, channelId, productIds, cancellationToken);
        var taxRates = await TaxAsync(countryCode, productIds, cancellationToken);

        var priced = new List<PricedOrderLine>();
        var unpriced = new List<Guid>();

        foreach (var line in lines)
        {
            if (!prices.TryGetValue(line.ProductId, out var candidates)
                || PriceResolver.Resolve(candidates, on) is not { } price)
            {
                unpriced.Add(line.ProductId);
                continue;
            }

            var unitPrice = new Money(price.Amount, price.Currency);

            /*
             * The promotion resolver takes an `int` quantity and a line carries a decimal.
             *
             * Truncated rather than rounded: a tier reading "buy 6 or more" is a promise about whole
             * units the shopkeeper has taken, and 5.9 kg has not reached six of anything. Rounding up
             * would hand a tier to an order that never earned it — and the tier's own discount then
             * applies to the whole line, so the error is not proportional to the rounding.
             */
            var whole = (int)Math.Floor(line.Quantity);

            var promotion = promotions.TryGetValue(line.ProductId, out var offers)
                ? PromotionResolver.Resolve(offers, whole, on)
                : null;

            var rate = taxRates.TryGetValue(line.ProductId, out var rates)
                ? TaxEngine.Resolve(rates, on)?.Percentage
                : null;

            var computed = LinePricing.Price(unitPrice, line.Quantity, promotion, rate);

            priced.Add(new PricedOrderLine(
                line.ProductId,
                line.Quantity,
                unitPrice,
                price.PriceListId,
                promotion?.PromotionId,
                computed.Subtotal,
                computed.Discount,
                computed.Net,
                computed.Tax,
                computed.Total));
        }

        return Total(priced, unpriced);
    }

    /// <summary>
    /// Adds the lines up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The order's totals are sums of the lines' rounded amounts, not a re-derivation.</b> Each
    /// line was already rounded to the currency's minor units so that it reads correctly on its own
    /// row; re-computing the order from unrounded intermediates would give a total that disagrees
    /// with the column above it by a cent or two, which is the one arithmetic error a reader always
    /// notices.
    /// </para>
    /// <para>
    /// <b>The currency comes from the lines.</b> There is no cross-currency order (<c>BR-ORD-7</c>),
    /// and rather than assert that here it falls out: every price on this order resolved from lists
    /// reaching one outlet, and <see cref="Money"/> refuses arithmetic across currencies — so a
    /// tenant that had somehow assigned two currencies to one shop gets an exception naming both
    /// rather than a total quietly computed in the first one seen.
    /// </para>
    /// </remarks>
    private static PricedOrder Total(
        IReadOnlyList<PricedOrderLine> lines, IReadOnlyList<Guid> unpriced)
    {
        // An order whose every line was unpriced has no currency to report. Empty strings and a
        // fabricated "EUR" are both worse than the honest shape: no lines, no money, and the ids.
        if (lines.Count == 0)
        {
            return new PricedOrder(
                string.Empty, [], default, default, default, default, default, unpriced);
        }

        var currency = lines[0].Total.Currency;

        var subtotal = Sum(lines, line => line.Subtotal, currency);
        var discount = Sum(lines, line => line.Discount, currency);
        var net = Sum(lines, line => line.Net, currency);
        var tax = Sum(lines, line => line.Tax, currency);
        var total = Sum(lines, line => line.Total, currency);

        return new PricedOrder(currency, lines, subtotal, discount, net, tax, total, unpriced);
    }

    private static Money Sum(
        IReadOnlyList<PricedOrderLine> lines, Func<PricedOrderLine, Money> pick, string currency) =>
        lines.Aggregate(Money.Zero(currency).Round(), (running, line) => running + pick(line));

    /// <summary>Every price these products could carry at this outlet, by product.</summary>
    /// <remarks>
    /// The window filter is applied here as well as in the resolver, for the reason
    /// <c>PriceResolutionEndpoints</c> gives: a tenant accumulates lists for years, and loading all
    /// of them to discard all but one date's worth grows the query without bound. The resolver
    /// re-checks because it is a pure function that cannot assume its caller filtered.
    /// </remarks>
    private async Task<Dictionary<Guid, List<PriceCandidate>>> PricesAsync(
        Guid outletId,
        Guid channelId,
        DateOnly date,
        IReadOnlyList<Guid> productIds,
        CancellationToken ct)
    {
        var rows = await (
            from assignment in db.PriceListAssignments
            where assignment.OutletId == outletId || assignment.ChannelId == channelId
            join list in db.PriceLists on assignment.PriceListId equals list.Id
            where list.EffectiveFrom <= date && (list.EffectiveTo == null || date < list.EffectiveTo)
            join line in db.PriceListLines on list.Id equals line.PriceListId
            where productIds.Contains(line.ProductId)
            select new
            {
                line.ProductId,
                list.Id,
                list.Currency,
                list.EffectiveFrom,
                list.EffectiveTo,
                line.Amount,
                assignment.OutletId,
            }).ToListAsync(ct);

        return rows
            .GroupBy(row => row.ProductId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => new PriceCandidate(
                    row.Id,
                    row.OutletId is null ? PriceScope.Channel : PriceScope.Outlet,
                    row.Currency,
                    row.EffectiveFrom,
                    row.EffectiveTo,
                    row.Amount)).ToList());
    }

    /// <summary>Every promotion these products could carry at this outlet, by product.</summary>
    /// <remarks>
    /// <b>No date filter, unlike the prices above.</b> The resolver applies the validity window
    /// itself and a tenant's promotion table is bounded by how many deals they run, not by how many
    /// years they have been trading — so filtering here would duplicate a rule the mirror has to
    /// implement anyway for no meaningful saving. The single-product endpoint makes the same call.
    /// </remarks>
    private async Task<Dictionary<Guid, List<PromotionCandidate>>> PromotionsAsync(
        Guid outletId, Guid channelId, IReadOnlyList<Guid> productIds, CancellationToken ct)
    {
        var categoriesOf = await TargetableCategoriesAsync(productIds, ct);

        // Every category any of these products could be targeted through, flattened once.
        var categoryIds = categoriesOf.Values.SelectMany(ids => ids).Distinct().ToList();

        var reached = db.PromotionAssignments
            .Where(assignment => assignment.OutletId == outletId || assignment.ChannelId == channelId)
            .Select(assignment => assignment.PromotionId);

        // Targets carried rather than reduced to promotion ids: a promotion reaching this order
        // through two different products has to end up on both, and an id set loses which.
        var targets = await db.PromotionTargets
            .Where(target =>
                (target.ProductId != null && productIds.Contains(target.ProductId.Value))
                || (target.CategoryId != null && categoryIds.Contains(target.CategoryId.Value)))
            .Where(target => reached.Contains(target.PromotionId))
            .Select(target => new { target.PromotionId, target.ProductId, target.CategoryId })
            .ToListAsync(ct);

        if (targets.Count == 0) return [];

        var promotionIds = targets.Select(target => target.PromotionId).Distinct().ToList();

        var promotions = await db.Promotions
            .Where(promotion => promotionIds.Contains(promotion.Id))
            .ToListAsync(ct);

        var tiers = await TiersAsync(promotions, ct);

        var candidates = promotions.ToDictionary(
            promotion => promotion.Id, promotion => Candidate(promotion, tiers));

        var byProduct = new Dictionary<Guid, List<PromotionCandidate>>();

        foreach (var productId in productIds)
        {
            var applicable = targets
                .Where(target => target.ProductId == productId
                                 || (target.CategoryId is { } category
                                     && categoriesOf.GetValueOrDefault(productId, []).Contains(category)))
                .Select(target => target.PromotionId)
                .Distinct()
                .Where(candidates.ContainsKey)
                .Select(id => candidates[id])
                .ToList();

            if (applicable.Count > 0) byProduct[productId] = applicable;
        }

        return byProduct;
    }

    private async Task<Dictionary<Guid, IReadOnlyList<PromotionTierCandidate>>> TiersAsync(
        IReadOnlyList<Promotion> promotions, CancellationToken ct)
    {
        var tiered = promotions
            .Where(promotion => promotion.Type == PromotionType.VolumeTiered)
            .Select(promotion => promotion.Id)
            .ToList();

        if (tiered.Count == 0) return [];

        // One query for every tier of every tiered candidate, grouped in memory — a per-promotion
        // query would be one round trip per deal on a path a rep waits for.
        var rows = await db.PromotionTiers
            .Where(tier => tiered.Contains(tier.PromotionId))
            .ToListAsync(ct);

        return rows
            .GroupBy(tier => tier.PromotionId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PromotionTierCandidate>)
                [
                    .. group.Select(tier => new PromotionTierCandidate(
                        tier.MinQuantity, tier.PercentOff, tier.AmountOff, tier.Currency)),
                ]);
    }

    private static PromotionCandidate Candidate(
        Promotion promotion, Dictionary<Guid, IReadOnlyList<PromotionTierCandidate>> tiers) => new(
        promotion.Id,
        promotion.Type,
        promotion.Priority,
        promotion.ValidFrom,
        promotion.ValidTo,
        promotion.PercentOff,
        promotion.AmountOff,
        promotion.Currency,
        tiers.GetValueOrDefault(promotion.Id),
        promotion.BuyQuantity is { } buy
            ? new BundleCandidate(
                buy,
                promotion.GetQuantity!.Value,
                promotion.GetPercentOff!.Value,
                promotion.GetProductId)
            : null);

    /// <summary>Each product's category and every category above it.</summary>
    /// <remarks>
    /// The parent pointers are loaded once for the whole tenant and walked in memory, exactly as the
    /// single-product endpoint does — a tenant's category tree is tens to hundreds of rows, and the
    /// alternatives are a recursive CTE per resolution or a closure table to keep in step. Loaded
    /// once here rather than once per product, which is the whole reason this method takes a set.
    /// </remarks>
    private async Task<Dictionary<Guid, IReadOnlyList<Guid>>> TargetableCategoriesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct)
    {
        var filed = await db.Products
            .Where(product => productIds.Contains(product.Id) && product.CategoryId != null)
            .Select(product => new { product.Id, CategoryId = product.CategoryId!.Value })
            .ToListAsync(ct);

        if (filed.Count == 0) return [];

        var parentOf = await db.Categories.ToDictionaryAsync(
            category => category.Id, category => category.ParentId, ct);

        return filed.ToDictionary(
            product => product.Id,
            product => (IReadOnlyList<Guid>)
                [.. CategoryHierarchy.SelfAndAncestors(product.CategoryId, parentOf)]);
    }

    /// <summary>Every tax rate these products could carry in this outlet's country, by product.</summary>
    /// <remarks>
    /// An outlet with no country yields nothing at all, and every line comes back untaxed rather than
    /// zero-rated — <c>TaxEndpoints</c> makes the same call, and <see cref="LinePricing"/> keeps
    /// "unknown" and "zero" apart precisely so this can.
    /// </remarks>
    private async Task<Dictionary<Guid, List<TaxRateCandidate>>> TaxAsync(
        string? countryCode, IReadOnlyList<Guid> productIds, CancellationToken ct)
    {
        if (countryCode is null) return [];

        var classOf = await db.Products
            .Where(product => productIds.Contains(product.Id) && product.TaxClassId != null)
            .Select(product => new { product.Id, TaxClassId = product.TaxClassId!.Value })
            .ToListAsync(ct);

        if (classOf.Count == 0) return [];

        var classIds = classOf.Select(product => product.TaxClassId).Distinct().ToList();

        var rows = await db.TaxRates
            .Where(rate => classIds.Contains(rate.TaxClassId) && rate.CountryCode == countryCode)
            .Select(rate => new
            {
                rate.TaxClassId,
                Candidate = new TaxRateCandidate(
                    rate.Id, rate.Percentage, rate.EffectiveFrom, rate.EffectiveTo),
            })
            .ToListAsync(ct);

        var byClass = rows
            .GroupBy(row => row.TaxClassId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.Candidate).ToList());

        return classOf
            .Where(product => byClass.ContainsKey(product.TaxClassId))
            .ToDictionary(product => product.Id, product => byClass[product.TaxClassId]);
    }
}
