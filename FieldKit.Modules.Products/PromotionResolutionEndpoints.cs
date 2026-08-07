using System.Globalization;
using System.Text.Json.Serialization;
using FieldKit.Modules.Outlets.Contracts;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>
/// The promotion that applies to one line, and what it does.
/// </summary>
/// <remarks>
/// <c>PercentOff</c> and <c>AmountOff</c> are strings for the reason every other discount in this API
/// is (<c>BR-PRD-8</c>). Exactly one of them is set for the three reducing types; both are null for
/// <see cref="PromotionType.BuyXGetY"/>, which fills <c>Bundle</c> instead.
/// <para>
/// A tiered promotion arrives here already collapsed to the tier the quantity reached — the caller
/// gets a discount, not a table to search a second time.
/// </para>
/// </remarks>
public sealed record ResolvedPromotionResponse(
    Guid PromotionId,
    [property: JsonConverter(typeof(JsonStringEnumConverter<PromotionType>))] PromotionType Type,
    int Priority,
    string? PercentOff,
    string? AmountOff,
    string? Currency,
    BundleResponse? Bundle);

/// <summary>
/// The answer to "what applies to this line" — a promotion, or explicitly none.
/// </summary>
/// <remarks>
/// <para>
/// <b>A wrapper rather than a bare promotion-or-null</b>, so the response is always a JSON object.
/// The first draft returned the promotion directly and null when there was none, which ASP.NET Core
/// turns into an <i>empty body</i> — it short-circuits on a null value and writes nothing, for
/// <c>Results.Ok</c> and <c>Results.Json</c> alike. An empty body is not JSON, and every client
/// deserializing this response throws on it.
/// </para>
/// <para>
/// Fighting the framework to emit four literal characters would have been possible and wrong. The
/// wrapper is better on its own merits: "no promotion applies" is a real answer and deserves to be
/// stated rather than implied by absence, and an object leaves room to say <i>why</i> if that ever
/// earns its keep.
/// </para>
/// </remarks>
public sealed record PromotionResolutionResponse(ResolvedPromotionResponse? Promotion);

/// <summary>
/// Resolving the promotion for one order line (<c>PRD-06</c>, <c>BR-PRD-3</c>).
/// </summary>
/// <remarks>
/// <para>
/// Thin, like <see cref="PriceResolutionEndpoints"/>: this gathers candidates and hands them to
/// <see cref="PromotionResolver"/>. The selection rules live there, in a pure function, because W7
/// has to reimplement them in TypeScript and a rule embedded in a LINQ query cannot be
/// reimplemented, only re-derived.
/// </para>
/// <para>
/// <b>One line at a time, deliberately.</b> An order has many lines and re-pricing one at submit is
/// obviously a batch, so a batch endpoint is the tempting design — and it would be designed against
/// no consumer, since Order is Phase 3. That is the guess this module has refused four times now
/// (<c>IAssortmentService</c>, <c>IPricingService</c>, <c>Products.Contracts</c>,
/// <c>IReferenceChangeFeed</c>), and refusing it a fifth time costs a round trip in a path that does
/// not exist yet. The device does not call this at all: it holds the rules and runs the same resolver
/// locally, which is what <c>PRD-08</c> is for.
/// </para>
/// </remarks>
internal static class PromotionResolutionEndpoints
{
    public static void MapPromotionResolutionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var outletPromotions = endpoints
            .MapGroup("/api/products/outlets/{outletId:guid}/promotions")
            .WithTags("Products");

        outletPromotions.MapGet("", async (
            Guid outletId,
            string? on,
            Guid? productId,
            int? quantity,
            ProductsDbContext db,
            IOutletClassification classification,
            CancellationToken ct) =>
        {
            if (BusinessDate.Parse(
                    on, "product.promotion.dateRequired", "product.promotion.dateMalformed",
                    out var date) is { } dateProblem)
            {
                return dateProblem;
            }

            if (LineProblem(productId, quantity) is { } lineProblem) return lineProblem;

            // Which channel this shop trades in decides which promotions reach it. Through the
            // contract, because Products cannot see the outlet table (AT-1) — and tenant-filtered by
            // it, so another tenant's outlet is absent here and reads as 404 below.
            var classified = await classification.ClassifyManyAsync([outletId], ct);
            if (classified.Count == 0) return Results.NotFound();

            var candidates = await CandidatesAsync(
                db, outletId, classified[0].ChannelId, productId!.Value, date, ct);

            var resolved = PromotionResolver.Resolve(candidates, quantity!.Value, date);

            // No promotion is a real answer, not a 404: this line simply has none today. Always
            // wrapped, so the body is always an object — see PromotionResolutionResponse.
            return Results.Ok(new PromotionResolutionResponse(
                resolved is null ? null : Respond(resolved)));
        }).RequirePermission(ProductsPermissions.Read);
    }

    private static ResolvedPromotionResponse Respond(ResolvedPromotion resolved) =>
        new(resolved.PromotionId,
            resolved.Type,
            resolved.Priority,
            Format(resolved.PercentOff),
            Format(resolved.AmountOff),
            resolved.Currency,
            resolved.Bundle is { } bundle
                ? new BundleResponse(
                    bundle.BuyQuantity,
                    bundle.GetQuantity,
                    Format(bundle.GetPercentOff)!,
                    bundle.GetProductId)
                : null);

    /// <summary>Every discount in this API is spelled the way <c>MoneyJsonConverter</c> spells one.</summary>
    private static string? Format(decimal? value) =>
        value?.ToString("0.00##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Every promotion that could apply to this product at this outlet on this date.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three filters, all of which are the caller's job rather than the resolver's because none of
    /// them ranks anything (<c>BR-PRD-3</c> ranks by priority alone):
    /// </para>
    /// <list type="bullet">
    /// <item><b>reach</b> — assigned to this outlet, or to its channel. A promotion assigned to both
    /// would otherwise arrive twice, so the ids are distinct-ed;</item>
    /// <item><b>window</b> — filtered in SQL as well as in the resolver. Not redundancy for its own
    /// sake: a tenant accumulates promotions for years, and loading all of them to discard all but
    /// one date's worth would grow the query without bound. The resolver re-checks because it is a
    /// pure function that cannot assume its caller filtered;</item>
    /// <item><b>target</b> — the product itself, or any category at or above the one it is filed
    /// under.</item>
    /// </list>
    /// <para>
    /// Quantity is <i>not</i> filtered here. Whether a tier is reached or a bundle is earned is a
    /// selection rule, it is stated in the vectors, and doing it in SQL would put half of
    /// <c>BR-PRD-3</c> somewhere W7's mirror cannot see it.
    /// </para>
    /// </remarks>
    private static async Task<List<PromotionCandidate>> CandidatesAsync(
        ProductsDbContext db,
        Guid outletId,
        Guid channelId,
        Guid productId,
        DateOnly date,
        CancellationToken ct)
    {
        var categoryIds = await TargetableCategoriesAsync(db, productId, ct);

        var targeted = db.PromotionTargets
            .Where(target => target.ProductId == productId
                             || (target.CategoryId != null && categoryIds.Contains(target.CategoryId.Value)))
            .Select(target => target.PromotionId);

        var reached = db.PromotionAssignments
            .Where(assignment => assignment.OutletId == outletId || assignment.ChannelId == channelId)
            .Select(assignment => assignment.PromotionId);

        var promotions = await db.Promotions
            .Where(promotion => targeted.Contains(promotion.Id) && reached.Contains(promotion.Id))
            .Where(promotion => promotion.ValidFrom <= date
                                && (promotion.ValidTo == null || date < promotion.ValidTo))
            .ToListAsync(ct);

        var tiered = promotions
            .Where(promotion => promotion.Type == PromotionType.VolumeTiered)
            .Select(promotion => promotion.Id)
            .ToList();

        // One query for every tier of every tiered candidate, grouped in memory. A per-promotion
        // query would be one round trip per deal on a path a rep hits for every line they enter.
        var tiers = tiered.Count == 0
            ? []
            : (await db.PromotionTiers
                    .Where(tier => tiered.Contains(tier.PromotionId))
                    .ToListAsync(ct))
                .GroupBy(tier => tier.PromotionId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<PromotionTierCandidate>)
                    [
                        .. group.Select(tier => new PromotionTierCandidate(
                            tier.MinQuantity, tier.PercentOff, tier.AmountOff, tier.Currency)),
                    ]);

        return
        [
            .. promotions.Select(promotion => new PromotionCandidate(
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
                    : null)),
        ];
    }

    /// <summary>
    /// The product's category and every category above it — the set a category target may name.
    /// </summary>
    /// <remarks>
    /// The parent pointers are loaded for the whole tenant and walked in memory. A tenant's category
    /// tree is small (tens to hundreds), and the alternative is a recursive CTE that Postgres would
    /// run per resolution — or a closure table, which is a second thing to keep in step with the
    /// first. Reading a small table once is the cheaper honest option; if a tenant ever has a tree
    /// where it is not, a closure table is the change to make.
    /// </remarks>
    private static async Task<List<Guid>> TargetableCategoriesAsync(
        ProductsDbContext db, Guid productId, CancellationToken ct)
    {
        var categoryId = await db.Products
            .Where(product => product.Id == productId)
            .Select(product => product.CategoryId)
            .SingleOrDefaultAsync(ct);

        // A product with no category can still be targeted directly; it simply matches no category
        // target, which is what an empty list gives.
        if (categoryId is not { } filed) return [];

        var parentOf = await db.Categories.ToDictionaryAsync(
            category => category.Id, category => category.ParentId, ct);

        return [.. CategoryHierarchy.SelfAndAncestors(filed, parentOf)];
    }

    /// <summary>
    /// Refuses a line that does not say what it is.
    /// </summary>
    /// <remarks>
    /// Both are required, and <c>quantity</c> is the one worth explaining. Left to default it would
    /// bind to 0, and a line of zero reaches no tier and earns no bundle — so every volume deal and
    /// every BOGO would silently resolve to nothing for a caller who forgot it, which reads as "this
    /// shop has no promotions" rather than as a mistake.
    /// </remarks>
    private static IResult? LineProblem(Guid? productId, int? quantity)
    {
        var problems = new List<FieldProblem>();

        if (productId is null)
        {
            problems.Add(new FieldProblem(
                "productId",
                "A product is required — promotions resolve per line.",
                "product.promotion.productRequired"));
        }

        if (quantity is null)
        {
            problems.Add(new FieldProblem(
                "quantity",
                "A quantity is required — volume tiers and bundles depend on it.",
                "product.promotion.quantityRequired"));
        }
        else if (quantity < 1)
        {
            problems.Add(new FieldProblem(
                "quantity",
                "A quantity is at least 1.",
                "product.promotion.quantityTooSmall",
                new Dictionary<string, string> { ["quantity"] = quantity.Value.ToString() }));
        }

        return problems.Count > 0 ? Problems.BadRequest(problems) : null;
    }
}
