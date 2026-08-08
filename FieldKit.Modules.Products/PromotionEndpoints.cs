using System.Globalization;
using System.Text.Json.Serialization;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>A promotion, without its targets.</summary>
/// <remarks>
/// <c>Value</c> carries whichever the type calls for, as a string — a percentage for
/// <see cref="PromotionType.PercentOff"/>, an amount for <see cref="PromotionType.FixedAmountOff"/>,
/// and <b>null for <see cref="PromotionType.VolumeTiered"/></b>, whose discounts live on its tiers.
/// A string for the same reason money is one (<c>BR-PRD-8</c>): a JSON number is an IEEE-754 float
/// the moment a browser parses it, and "12.5% off" losing its last digit is the same class of bug as
/// a price doing so. <c>Currency</c> is null for anything that is not a fixed amount.
/// </remarks>
public sealed record PromotionResponse(
    Guid Id,
    string Name,
    [property: JsonConverter(typeof(JsonStringEnumConverter<PromotionType>))] PromotionType Type,
    string? Value,
    string? Currency,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    int Priority,
    BundleResponse? Bundle = null);

/// <summary>What a <see cref="PromotionType.BuyXGetY"/> promotion gives away.</summary>
/// <remarks>
/// <para>
/// Nested rather than four more flat fields, even though the columns behind it are flat. Four
/// nullable properties that are only ever all-set or all-null belong together in the shape a caller
/// reads, and a client can then ask <c>bundle == null</c> instead of checking four things that must
/// agree.
/// </para>
/// <para>
/// <c>GetProductId</c> null means <b>the same product that was bought</b> — see
/// <c>Promotion.GetProductId</c>. <c>GetPercentOff</c> of <c>"100.00"</c> is free.
/// </para>
/// </remarks>
public sealed record BundleResponse(
    int BuyQuantity, int GetQuantity, string GetPercentOff, Guid? GetProductId);

/// <summary>A bundle as an author states it.</summary>
public sealed record BundleRequest(
    int BuyQuantity, int GetQuantity, string GetPercentOff, Guid? GetProductId = null);

/// <summary>Author a promotion. The type is fixed at creation — see the endpoint.</summary>
/// <remarks>
/// <c>Value</c> and <c>Currency</c> are optional because not every type carries them: a
/// <see cref="PromotionType.VolumeTiered"/> promotion sends neither, and is refused if it sends
/// either.
/// </remarks>
public sealed record CreatePromotionRequest(
    string Name,
    [property: JsonConverter(typeof(JsonStringEnumConverter<PromotionType>))] PromotionType Type,
    DateOnly ValidFrom,
    string? Value = null,
    DateOnly? ValidTo = null,
    int Priority = 0,
    string? Currency = null,
    BundleRequest? Bundle = null);

/// <summary>Re-value, re-date and re-prioritise. No type: changing it would reinterpret the value.</summary>
public sealed record UpdatePromotionRequest(
    string Name,
    DateOnly ValidFrom,
    string? Value = null,
    DateOnly? ValidTo = null,
    int Priority = 0,
    BundleRequest? Bundle = null);

/// <summary>One threshold of a tiered promotion: buy this many, get this off.</summary>
public sealed record PromotionTierResponse(
    int MinQuantity, string Value, string? Currency);

/// <summary>One tier as an author sets it. The value is a string, like every other discount.</summary>
public sealed record PromotionTierRequest(
    int MinQuantity, string Value, string? Currency = null);

/// <summary>Every threshold of a tiered promotion. A PUT replaces the set.</summary>
/// <remarks>
/// An empty set is a real state, not a refusal — the promotion then discounts nothing, exactly as an
/// untargeted promotion or an unassigned price list does.
/// </remarks>
public sealed record SetPromotionTiersRequest(IReadOnlyList<PromotionTierRequest> Tiers);

/// <summary>One thing a promotion discounts. Exactly one id is set.</summary>
public sealed record PromotionTargetResponse(Guid? ProductId, Guid? CategoryId);

/// <summary>Everything a promotion discounts. A PUT replaces the set.</summary>
/// <remarks>
/// An empty set is a real state, not a refusal: the promotion then discounts nothing. That mirrors
/// emptying a price list's assignments, which is how a list is withdrawn — and it is how a promotion
/// is taken out of play without editing its window or deleting the record other things point at.
/// </remarks>
public sealed record SetPromotionTargetsRequest(
    IReadOnlyList<Guid> ProductIds, IReadOnlyList<Guid> CategoryIds);

/// <summary>
/// Authoring promotions (<c>PRD-05</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two of B1's four types</b> — percentage off and fixed amount off. Volume/tiered and BOGO/bundle
/// need child rows of their own and are the second PR the delivery plan budgets for `PRD-05`.
/// </para>
/// <para>
/// <b>Nothing here applies a discount.</b> Which promotion wins for an order line, within its window
/// and by priority (<c>BR-PRD-3</c>, <c>BR-PRD-6</c>), is <c>PRD-06</c>. And where a promotion
/// reaches — channels and outlets — is the next slice, mirroring how <see cref="PriceList"/> was
/// authored before <see cref="PriceListAssignment"/> said where it applied. A promotion authored
/// today is a rule that exists and discounts nobody, which is the same honest intermediate state a
/// price list passes through.
/// </para>
/// </remarks>
internal static class PromotionEndpoints
{
    public static void MapPromotionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var promotions = endpoints.MapGroup("/api/products/promotions").WithTags("Products");

        promotions.MapGet("/", async (ProductsDbContext db, CancellationToken ct) =>
                Results.Ok(Respond(await db.Promotions
                    // Best priority first, then by the day it opens — the order someone reviewing a
                    // tenant's deals reads them in, and the order resolution considers them.
                    .OrderByDescending(promotion => promotion.Priority)
                    .ThenBy(promotion => promotion.ValidFrom)
                    .ThenBy(promotion => promotion.Name)
                    .ToListAsync(ct))))
            .RequirePermission(ProductsPermissions.Read);

        promotions.MapGet("/{id:guid}", async (Guid id, ProductsDbContext db, CancellationToken ct) =>
        {
            var promotion = await db.Promotions.SingleOrDefaultAsync(p => p.Id == id, ct);

            return promotion is null ? Results.NotFound() : Results.Ok(Respond(promotion));
        }).RequirePermission(ProductsPermissions.Read);

        promotions.MapPost("/", async (
            CreatePromotionRequest request, ProductsDbContext db, CancellationToken ct) =>
        {
            var (value, bundle, problem) = await PromotionProblem(
                db,
                request.Name,
                request.Type,
                request.Value,
                request.Currency,
                request.Bundle,
                request.ValidFrom,
                request.ValidTo,
                excluding: null,
                ct);

            if (problem is not null) return problem;

            var created = request.Type switch
            {
                PromotionType.PercentOff => Promotion.PercentageOff(
                    request.Name, value!.Value, request.ValidFrom, request.ValidTo, request.Priority),
                PromotionType.FixedAmountOff => Promotion.FixedAmountOff(
                    request.Name, value!.Value, request.Currency!, request.ValidFrom, request.ValidTo,
                    request.Priority),
                PromotionType.BuyXGetY => Promotion.BuyXGetY(
                    request.Name, bundle!.Value.BuyQuantity, bundle.Value.GetQuantity,
                    bundle.Value.GetPercentOff, bundle.Value.GetProductId, request.ValidFrom,
                    request.ValidTo, request.Priority),
                _ => Promotion.VolumeTiered(
                    request.Name, request.ValidFrom, request.ValidTo, request.Priority),
            };

            db.Promotions.Add(created);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/products/promotions/{created.Id}", Respond(created));
        }).RequirePermission(ProductsPermissions.Write);

        promotions.MapPut("/{id:guid}", async (
            Guid id,
            UpdatePromotionRequest request,
            ProductsDbContext db,
            IClock clock,
            CancellationToken ct) =>
        {
            var promotion = await db.Promotions.SingleOrDefaultAsync(p => p.Id == id, ct);
            if (promotion is null) return Results.NotFound();

            // The type and the currency come from the stored promotion, not from the request.
            // Re-typing a promotion would reinterpret its value — 15 meaning "15% off" becoming 15
            // meaning "€15 off" — and every order already priced against it would then be explained
            // by a rule that no longer exists.
            var (value, bundle, problem) = await PromotionProblem(
                db,
                request.Name,
                promotion.Type,
                request.Value,
                promotion.Currency,
                request.Bundle,
                request.ValidFrom,
                request.ValidTo,
                excluding: id,
                ct);

            if (problem is not null) return problem;

            promotion.Update(
                request.Name, value, request.ValidFrom, request.ValidTo, request.Priority, clock);

            // Restated in full, like everything else here — a PUT replaces. The quantities are not
            // optional on the way in for this type, so there is no "leave the bundle alone" reading
            // to be had, which is the same promise the request makes about the window and the name.
            if (bundle is { } stated)
            {
                promotion.Rebundle(
                    stated.BuyQuantity, stated.GetQuantity, stated.GetPercentOff,
                    stated.GetProductId, clock);
            }

            await db.SaveChangesAsync(ct);

            return Results.Ok(Respond(promotion));
        }).RequirePermission(ProductsPermissions.Write);

        promotions.MapGet("/{id:guid}/targets", async (
            Guid id, ProductsDbContext db, CancellationToken ct) =>
        {
            if (!await db.Promotions.AnyAsync(p => p.Id == id, ct)) return Results.NotFound();

            return Results.Ok(await TargetsAsync(db, id, ct));
        }).RequirePermission(ProductsPermissions.Read);

        promotions.MapPut("/{id:guid}/targets", async (
            Guid id,
            SetPromotionTargetsRequest request,
            ProductsDbContext db,
            CancellationToken ct) =>
        {
            if (!await db.Promotions.AnyAsync(p => p.Id == id, ct)) return Results.NotFound();

            if (await TargetProblem(db, request, ct) is { } problem) return problem;

            var existing = await db.PromotionTargets
                .Where(target => target.PromotionId == id)
                .ToListAsync(ct);

            var wantedProducts = request.ProductIds.ToHashSet();
            var wantedCategories = request.CategoryIds.ToHashSet();

            // Replace, like every other set in this module — and keep the rows that survive rather
            // than deleting and re-inserting, so a target unchanged since March does not look newly
            // added because a different one moved.
            foreach (var target in existing)
            {
                var keep = target.ProductId is { } productId
                    ? wantedProducts.Remove(productId)
                    : wantedCategories.Remove(target.CategoryId!.Value);

                if (!keep) db.PromotionTargets.Remove(target);
            }

            foreach (var productId in wantedProducts)
            {
                db.PromotionTargets.Add(PromotionTarget.Product(id, productId));
            }

            foreach (var categoryId in wantedCategories)
            {
                db.PromotionTargets.Add(PromotionTarget.Category(id, categoryId));
            }

            await db.SaveChangesAsync(ct);

            return Results.Ok(await TargetsAsync(db, id, ct));
        }).RequirePermission(ProductsPermissions.Write);

        promotions.MapGet("/{id:guid}/tiers", async (
            Guid id, ProductsDbContext db, CancellationToken ct) =>
        {
            if (!await db.Promotions.AnyAsync(p => p.Id == id, ct)) return Results.NotFound();

            return Results.Ok(await TiersAsync(db, id, ct));
        }).RequirePermission(ProductsPermissions.Read);

        promotions.MapPut("/{id:guid}/tiers", async (
            Guid id,
            SetPromotionTiersRequest request,
            ProductsDbContext db,
            CancellationToken ct) =>
        {
            var promotion = await db.Promotions.SingleOrDefaultAsync(p => p.Id == id, ct);
            if (promotion is null) return Results.NotFound();

            var (tiers, problem) = TierProblem(promotion, request);
            if (problem is not null) return problem;

            // Replaced wholesale, like every other set here. Not diffed against what is stored: a
            // tier's identity is its threshold, and an author moving "10+" to "12+" has replaced the
            // tier rather than edited it, so keeping the row would preserve a CreatedAt that means
            // nothing. Targets and price lines keep theirs because their identity is a product id,
            // which does survive an edit.
            db.PromotionTiers.RemoveRange(
                await db.PromotionTiers.Where(tier => tier.PromotionId == id).ToListAsync(ct));

            foreach (var tier in tiers)
            {
                db.PromotionTiers.Add(tier.Currency is { } currency
                    ? PromotionTier.Amount(id, tier.MinQuantity, tier.Value, currency)
                    : PromotionTier.Percentage(id, tier.MinQuantity, tier.Value));
            }

            await db.SaveChangesAsync(ct);

            return Results.Ok(await TiersAsync(db, id, ct));
        }).RequirePermission(ProductsPermissions.Write);
    }

    private static IReadOnlyList<PromotionResponse> Respond(IEnumerable<Promotion> promotions) =>
        [.. promotions.Select(Respond)];

    private static PromotionResponse Respond(Promotion promotion) =>
        new(promotion.Id,
            promotion.Name,
            promotion.Type,
            // Whichever the type carries, formatted exactly as MoneyJsonConverter formats an amount:
            // at least two decimals, up to four, invariant.
            //
            // The fixed pattern is not cosmetic. `decimal` keeps its scale, so the value parsed from
            // "12.5" at creation renders as "12.5", while the same value read back from
            // numeric(5,2) renders as "12.50" — one promotion, two spellings, differing by which
            // request you asked. A client diffing the two would see a change that never happened.
            //
            // Null rather than "0.00" for a type whose discounts live on child rows: a zero there
            // would read as "no discount" instead of "look at the tiers".
            Format(promotion.PercentOff ?? promotion.AmountOff),
            promotion.Currency,
            promotion.ValidFrom,
            promotion.ValidTo,
            promotion.Priority,
            // All four move together or not at all, which is what makes the nesting honest rather
            // than decorative — a caller reads one null instead of checking four that must agree.
            promotion.BuyQuantity is { } buy
                ? new BundleResponse(
                    buy,
                    promotion.GetQuantity!.Value,
                    Format(promotion.GetPercentOff)!,
                    promotion.GetProductId)
                : null);

    /// <summary>Every discount in this API is spelled the way <c>MoneyJsonConverter</c> spells one.</summary>
    private static string? Format(decimal? value) =>
        value?.ToString("0.00##", CultureInfo.InvariantCulture);

    private static async Task<IReadOnlyList<PromotionTierResponse>> TiersAsync(
        ProductsDbContext db, Guid promotionId, CancellationToken ct) =>
        [
            .. (await db.PromotionTiers
                    .Where(tier => tier.PromotionId == promotionId)
                    // Ascending, which is how a tier table is read and how resolution will scan it.
                    .OrderBy(tier => tier.MinQuantity)
                    .ToListAsync(ct))
                .Select(tier => new PromotionTierResponse(
                    tier.MinQuantity, Format(tier.PercentOff ?? tier.AmountOff)!, tier.Currency)),
        ];

    private static async Task<IReadOnlyList<PromotionTargetResponse>> TargetsAsync(
        ProductsDbContext db, Guid promotionId, CancellationToken ct) =>
        await db.PromotionTargets
            .Where(target => target.PromotionId == promotionId)
            .OrderBy(target => target.ProductId)
            .ThenBy(target => target.CategoryId)
            .Select(target => new PromotionTargetResponse(target.ProductId, target.CategoryId))
            .ToListAsync(ct);

    /// <summary>
    /// Parses and checks one discount — a promotion's own, or a tier's. Appends to
    /// <paramref name="problems"/> and returns the value when it is usable.
    /// </summary>
    /// <remarks>
    /// Shared because a tier's discount obeys exactly the rules a flat promotion's does: same parse,
    /// same bounds, same currency-iff-amount pairing. Two copies would drift, and the copy that
    /// drifted would be the one nobody reads — the tier rows, which no back-office screen renders
    /// yet. <paramref name="field"/> is what the caller sent it under, so a refusal points at
    /// <c>value</c> or at <c>tiers[1].value</c> rather than at whichever the last author had in mind.
    /// </remarks>
    private static decimal? DiscountProblem(
        List<FieldProblem> problems, string field, string? rawValue, bool percentage, string? currency)
    {
        // Same parse as a price, and the same refusal of thousands separators. NumberStyles.Number
        // would make "12,50" parse to 1250 under invariant culture — a hundredfold discount that
        // reads as a plausible one.
        if (!decimal.TryParse(
                rawValue,
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var value))
        {
            problems.Add(new FieldProblem(
                field,
                $"'{rawValue}' is not a decimal value.",
                "product.promotion.valueNotANumber",
                new Dictionary<string, string> { ["value"] = rawValue ?? string.Empty }));

            return null;
        }

        if (percentage)
        {
            // Zero is refused along with the negatives. A 0% promotion is not a promotion that does
            // nothing — it is a rule that will win a priority contest against a real discount and
            // then take nothing off, which is worse than having no rule at all. Above 100 would make
            // the line negative, turning a discount into a payment to the shop.
            if (value <= 0 || value > 100)
            {
                problems.Add(new FieldProblem(
                    field,
                    "A percentage off is above 0 and at most 100.",
                    "product.promotion.percentOutOfRange",
                    new Dictionary<string, string> { ["value"] = rawValue! }));
            }

            if (currency is not null)
            {
                // Refused rather than ignored. A caller that sent a currency with a percentage has
                // misunderstood something, and silently dropping it means they find out when a
                // report disagrees with what they thought they authored.
                problems.Add(new FieldProblem(
                    field.Replace("value", "currency"),
                    "A percentage off has no currency.",
                    "product.promotion.currencyNotApplicable"));
            }

            return value;
        }

        // A fixed amount larger than the price is *not* refused: whether that floors the line at zero
        // or disqualifies the promotion is a resolution question (PRD-06), and it cannot be answered
        // at authoring time — the same promotion meets a different price at every outlet it reaches.
        if (value <= 0)
        {
            problems.Add(new FieldProblem(
                field,
                "A fixed amount off is above 0.",
                "product.promotion.amountNotPositive",
                new Dictionary<string, string> { ["value"] = rawValue! }));
        }

        // Shape only, as on a price list — what this refuses is "Euro", "eur " and "€".
        if (currency is not { Length: 3 } || !currency.All(char.IsAsciiLetter))
        {
            problems.Add(new FieldProblem(
                field.Replace("value", "currency"),
                "A fixed amount off needs a three-letter ISO-4217 currency, e.g. EUR.",
                "product.promotion.currencyInvalid",
                new Dictionary<string, string> { ["currency"] = currency ?? string.Empty }));
        }

        return value;
    }

    /// <summary>One bundle, parsed and checked.</summary>
    private readonly record struct CheckedBundle(
        int BuyQuantity, int GetQuantity, decimal GetPercentOff, Guid? GetProductId);

    /// <summary>Checks a promotion and returns whichever of a value and a bundle its type carries.</summary>
    private static async Task<(decimal? Value, CheckedBundle? Bundle, IResult? Problem)> PromotionProblem(
        ProductsDbContext db,
        string name,
        PromotionType type,
        string? rawValue,
        string? currency,
        BundleRequest? bundleRequest,
        DateOnly from,
        DateOnly? to,
        Guid? excluding,
        CancellationToken ct)
    {
        var problems = new List<FieldProblem>();
        decimal? value = null;
        CheckedBundle? bundle = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add(new FieldProblem(
                "name", "A promotion needs a name.", "product.promotion.nameRequired"));
        }

        if (TextLimits.TooLong("name", name, 120, "product.promotion.nameTooLong") is { } nameTooLong)
        {
            problems.Add(nameTooLong);
        }

        if (Promotion.CarriesItsOwnValue(type))
        {
            value = DiscountProblem(
                problems, "value", rawValue, type == PromotionType.PercentOff, currency);
        }
        else if (rawValue is not null || currency is not null)
        {
            // Refused rather than ignored, for the same reason a percentage carrying a currency is:
            // a caller sending a value for one of these types has misunderstood what it does, and
            // dropping it silently means they go on believing they authored a discount.
            problems.Add(new FieldProblem(
                "value",
                type == PromotionType.VolumeTiered
                    ? "A VolumeTiered promotion carries no value of its own — its discounts are its tiers."
                    : "A BuyXGetY promotion carries no value of its own — it gives units rather than reducing a price.",
                "product.promotion.valueNotApplicable",
                new Dictionary<string, string> { ["type"] = type.ToString() }));
        }

        if (type == PromotionType.BuyXGetY)
        {
            bundle = await BundleProblem(db, problems, bundleRequest, ct);
        }
        else if (bundleRequest is not null)
        {
            problems.Add(new FieldProblem(
                "bundle",
                $"Only a {PromotionType.BuyXGetY} promotion gives units away.",
                "product.promotion.bundleNotApplicable",
                new Dictionary<string, string> { ["type"] = type.ToString() }));
        }

        // Half-open, so equal dates are an empty window rather than a single day — a promotion that
        // is never live, which is certainly not what anyone meant to author.
        if (to is { } end && end <= from)
        {
            problems.Add(new FieldProblem(
                "validTo", "A promotion ends after it starts.", "product.promotion.windowInverted"));
        }

        var taken = await db.Promotions.AnyAsync(
            promotion => promotion.Name.ToLower() == name.ToLower()
                         && (excluding == null || promotion.Id != excluding),
            ct);

        if (taken)
        {
            problems.Add(new FieldProblem(
                "name",
                $"A promotion named '{name}' already exists.",
                "product.promotion.nameTaken",
                new Dictionary<string, string> { ["name"] = name }));
        }

        return (value, bundle, problems.Count > 0 ? Problems.BadRequest(problems) : null);
    }

    /// <summary>Checks what a <see cref="PromotionType.BuyXGetY"/> promotion gives away.</summary>
    private static async Task<CheckedBundle?> BundleProblem(
        ProductsDbContext db,
        List<FieldProblem> problems,
        BundleRequest? request,
        CancellationToken ct)
    {
        if (request is null)
        {
            // Required, unlike tiers and targets, which may be empty to mean "reaches nobody". The
            // difference is that an empty set is still a coherent promotion — one that discounts
            // nothing — whereas "buy ? get ?" is not a rule at all. A BuyXGetY promotion without its
            // quantities is not a draft; it is a row the check constraint refuses.
            problems.Add(new FieldProblem(
                "bundle",
                "A BuyXGetY promotion states how many are bought and how many are given.",
                "product.promotion.bundleRequired"));

            return null;
        }

        // At least one bought and at least one given. Zero on either side is not a bundle: "buy none
        // get one" gives the product away to anyone who orders anything, and "buy two get none" is a
        // rule that does nothing while still winning a priority contest against one that would have.
        if (request.BuyQuantity < 1)
        {
            problems.Add(new FieldProblem(
                "bundle.buyQuantity",
                "At least one must be bought.",
                "product.promotion.bundleQuantityTooSmall",
                new Dictionary<string, string> { ["quantity"] = request.BuyQuantity.ToString() }));
        }

        if (request.GetQuantity < 1)
        {
            problems.Add(new FieldProblem(
                "bundle.getQuantity",
                "At least one must be given.",
                "product.promotion.bundleQuantityTooSmall",
                new Dictionary<string, string> { ["quantity"] = request.GetQuantity.ToString() }));
        }

        // The same percentage rules as everywhere else in this module, through the same checker: 100
        // is free and is the whole point of the type, 0 is a rule that gives nothing at full price.
        var percentOff = DiscountProblem(
            problems, "bundle.getPercentOff", request.GetPercentOff, percentage: true, currency: null);

        if (request.GetProductId is { } getProductId
            && !await db.Products.AnyAsync(product => product.Id == getProductId, ct))
        {
            // Tenant-filtered by the global query filter, so another tenant's product reads as
            // missing — the only answer that does not confirm it exists elsewhere.
            problems.Add(new FieldProblem(
                "bundle.getProductId",
                "That product does not exist.",
                "product.promotion.bundleProductMissing",
                new Dictionary<string, string> { ["productId"] = getProductId.ToString() }));
        }

        return percentOff is { } percent && problems.Count == 0
            ? new CheckedBundle(
                request.BuyQuantity, request.GetQuantity, percent, request.GetProductId)
            : null;
    }

    /// <summary>One tier, parsed and checked.</summary>
    private readonly record struct CheckedTier(int MinQuantity, decimal Value, string? Currency);

    /// <summary>Checks a whole tier set and returns it parsed.</summary>
    /// <remarks>
    /// Whole-set rather than per-tier, because two of the three rules here are about the set: the
    /// kinds have to agree, and the thresholds have to be distinct. Neither can be seen from one row,
    /// which is also why the database enforces only the third (a tier's own value shape) — SQL can
    /// state what a row must look like, not what its siblings must look like, and a trigger to say so
    /// would be a rule living somewhere nobody reads.
    /// </remarks>
    private static (IReadOnlyList<CheckedTier> Tiers, IResult? Problem) TierProblem(
        Promotion promotion, SetPromotionTiersRequest request)
    {
        var problems = new List<FieldProblem>();
        var tiers = new List<CheckedTier>();

        if (promotion.Type != PromotionType.VolumeTiered)
        {
            // A flat promotion with tiers would have two discounts and no rule saying which applies.
            problems.Add(new FieldProblem(
                "tiers",
                $"Only a {PromotionType.VolumeTiered} promotion has tiers.",
                "product.promotion.tiersNotApplicable",
                new Dictionary<string, string> { ["type"] = promotion.Type.ToString() }));

            return ([], Problems.BadRequest(problems));
        }

        // An empty set is allowed and means the promotion discounts nothing — the same meaning as an
        // empty target set, and as a price list with no assignments.

        for (var index = 0; index < request.Tiers.Count; index++)
        {
            var tier = request.Tiers[index];

            // Below 2 is refused. A tier at 1 is "buy one or more", which is every order line that
            // matched at all — a flat discount wearing a tier's clothes, and one that would silently
            // shadow the PercentOff type it duplicates. Zero and negatives are not quantities.
            if (tier.MinQuantity < 2)
            {
                problems.Add(new FieldProblem(
                    $"tiers[{index}].minQuantity",
                    "A tier starts at 2 or more — a tier at 1 is a flat discount.",
                    "product.promotion.tierQuantityTooSmall",
                    new Dictionary<string, string> { ["minQuantity"] = tier.MinQuantity.ToString() }));
            }

            var value = DiscountProblem(
                problems, $"tiers[{index}].value", tier.Value, tier.Currency is null, tier.Currency);

            if (value is { } parsed)
            {
                tiers.Add(new CheckedTier(tier.MinQuantity, parsed, tier.Currency?.ToUpperInvariant()));
            }
        }

        var duplicates = request.Tiers
            .GroupBy(tier => tier.MinQuantity)
            .Count(group => group.Count() > 1);

        if (duplicates > 0)
        {
            problems.Add(new FieldProblem(
                "tiers",
                $"{duplicates} threshold(s) appear more than once.",
                "product.promotion.tierQuantityDuplicated",
                new Dictionary<string, string> { ["count"] = duplicates.ToString() }));
        }

        // All percentages or all amounts. Nothing about resolution requires it — tiers are selected
        // by quantity, not compared to each other, so a mixed set is well-defined. It is refused
        // because "5% off at 10, three euros off at 24" is a set nobody can sanity-check at a glance,
        // and is far more likely to be a mistake than an intention.
        if (tiers.Select(tier => tier.Currency is null).Distinct().Count() > 1)
        {
            problems.Add(new FieldProblem(
                "tiers",
                "Every tier of one promotion is a percentage, or every tier is an amount.",
                "product.promotion.tierKindsMixed"));
        }

        // And one currency across the amount tiers, for the reason BR-PRD-1 gives: a set that
        // discounts by EUR at one threshold and RON at another cannot be compared or summed, and
        // resolution would have to pick a currency the promotion never declared.
        if (tiers.Select(tier => tier.Currency).Where(c => c is not null).Distinct().Count() > 1)
        {
            problems.Add(new FieldProblem(
                "tiers",
                "Every amount tier of one promotion is in the same currency.",
                "product.promotion.tierCurrenciesMixed"));
        }

        return (tiers, problems.Count > 0 ? Problems.BadRequest(problems) : null);
    }

    private static async Task<IResult?> TargetProblem(
        ProductsDbContext db, SetPromotionTargetsRequest request, CancellationToken ct)
    {
        var problems = new List<FieldProblem>();

        var productIds = request.ProductIds.Distinct().ToList();
        var categoryIds = request.CategoryIds.Distinct().ToList();

        // An empty set is allowed, and it means the promotion discounts nothing — the same shape and
        // the same meaning as emptying a price list's assignments, which is how a list is withdrawn.
        //
        // No reading has to be guessed at for it to be safe. Resolution matches target rows; no rows
        // is no match, so "targets nothing" falls out of the data rather than needing a rule. The
        // alternative reading — empty meaning *everything* — is the one that would be dangerous, and
        // nothing here or in PRD-06 invites it.

        if (productIds.Count > 0)
        {
            var known = await db.Products
                .Where(product => productIds.Contains(product.Id))
                .Select(product => product.Id)
                .ToListAsync(ct);

            var missing = productIds.Except(known).Count();
            if (missing > 0)
            {
                problems.Add(new FieldProblem(
                    "productIds",
                    $"{missing} product(s) do not exist.",
                    "product.promotion.productMissing",
                    new Dictionary<string, string> { ["count"] = missing.ToString() }));
            }
        }

        if (categoryIds.Count > 0)
        {
            var known = await db.Categories
                .Where(category => categoryIds.Contains(category.Id))
                .Select(category => category.Id)
                .ToListAsync(ct);

            var missing = categoryIds.Except(known).Count();
            if (missing > 0)
            {
                problems.Add(new FieldProblem(
                    "categoryIds",
                    $"{missing} categor(ies) do not exist.",
                    "product.promotion.categoryMissing",
                    new Dictionary<string, string> { ["count"] = missing.ToString() }));
            }
        }

        return problems.Count > 0 ? Problems.BadRequest(problems) : null;
    }
}
