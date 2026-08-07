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
/// <c>Value</c> carries whichever of the two the type calls for, as a string — a percentage for
/// <see cref="PromotionType.PercentOff"/>, an amount for <see cref="PromotionType.FixedAmountOff"/>.
/// A string for the same reason money is one (<c>BR-PRD-8</c>): a JSON number is an IEEE-754 float
/// the moment a browser parses it, and "12.5% off" losing its last digit is the same class of bug as
/// a price doing so. <c>Currency</c> is null for a percentage.
/// </remarks>
public sealed record PromotionResponse(
    Guid Id,
    string Name,
    [property: JsonConverter(typeof(JsonStringEnumConverter<PromotionType>))] PromotionType Type,
    string Value,
    string? Currency,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    int Priority);

/// <summary>Author a promotion. The type is fixed at creation — see the endpoint.</summary>
public sealed record CreatePromotionRequest(
    string Name,
    [property: JsonConverter(typeof(JsonStringEnumConverter<PromotionType>))] PromotionType Type,
    string Value,
    DateOnly ValidFrom,
    DateOnly? ValidTo = null,
    int Priority = 0,
    string? Currency = null);

/// <summary>Re-value, re-date and re-prioritise. No type: changing it would reinterpret the value.</summary>
public sealed record UpdatePromotionRequest(
    string Name, string Value, DateOnly ValidFrom, DateOnly? ValidTo = null, int Priority = 0);

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
            var (value, problem) = await PromotionProblem(
                db,
                request.Name,
                request.Type,
                request.Value,
                request.Currency,
                request.ValidFrom,
                request.ValidTo,
                excluding: null,
                ct);

            if (problem is not null) return problem;

            var created = request.Type == PromotionType.PercentOff
                ? Promotion.PercentageOff(
                    request.Name, value, request.ValidFrom, request.ValidTo, request.Priority)
                : Promotion.FixedAmountOff(
                    request.Name, value, request.Currency!, request.ValidFrom, request.ValidTo,
                    request.Priority);

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
            var (value, problem) = await PromotionProblem(
                db,
                request.Name,
                promotion.Type,
                request.Value,
                promotion.Currency,
                request.ValidFrom,
                request.ValidTo,
                excluding: id,
                ct);

            if (problem is not null) return problem;

            promotion.Update(
                request.Name, value, request.ValidFrom, request.ValidTo, request.Priority, clock);

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
            (promotion.PercentOff ?? promotion.AmountOff ?? 0m).ToString("0.00##", CultureInfo.InvariantCulture),
            promotion.Currency,
            promotion.ValidFrom,
            promotion.ValidTo,
            promotion.Priority);

    private static async Task<IReadOnlyList<PromotionTargetResponse>> TargetsAsync(
        ProductsDbContext db, Guid promotionId, CancellationToken ct) =>
        await db.PromotionTargets
            .Where(target => target.PromotionId == promotionId)
            .OrderBy(target => target.ProductId)
            .ThenBy(target => target.CategoryId)
            .Select(target => new PromotionTargetResponse(target.ProductId, target.CategoryId))
            .ToListAsync(ct);

    /// <summary>Checks a promotion and returns its parsed value.</summary>
    private static async Task<(decimal Value, IResult? Problem)> PromotionProblem(
        ProductsDbContext db,
        string name,
        PromotionType type,
        string rawValue,
        string? currency,
        DateOnly from,
        DateOnly? to,
        Guid? excluding,
        CancellationToken ct)
    {
        var problems = new List<FieldProblem>();

        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add(new FieldProblem(
                "name", "A promotion needs a name.", "product.promotion.nameRequired"));
        }

        // Same parse as a price, and the same refusal of thousands separators. NumberStyles.Number
        // would make "12,50" parse to 1250 under invariant culture — a hundredfold discount that
        // reads as a plausible one.
        var parsed = decimal.TryParse(
            rawValue,
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out var value);

        if (!parsed)
        {
            problems.Add(new FieldProblem(
                "value",
                $"'{rawValue}' is not a decimal value.",
                "product.promotion.valueNotANumber",
                new Dictionary<string, string> { ["value"] = rawValue ?? string.Empty }));
        }
        else if (type == PromotionType.PercentOff)
        {
            // Zero is refused along with the negatives. A 0% promotion is not a promotion that does
            // nothing — it is a rule that will win a priority contest against a real discount and
            // then take nothing off, which is worse than having no rule at all. Above 100 would make
            // the line negative, turning a discount into a payment to the shop.
            if (value <= 0 || value > 100)
            {
                problems.Add(new FieldProblem(
                    "value",
                    "A percentage off is above 0 and at most 100.",
                    "product.promotion.percentOutOfRange",
                    new Dictionary<string, string> { ["value"] = rawValue }));
            }
        }
        else if (value <= 0)
        {
            // A fixed amount larger than the price is *not* refused here: whether that floors the
            // line at zero or refuses the promotion is a resolution question (PRD-06), and it cannot
            // be answered at authoring time — the same promotion meets a different price at every
            // outlet it reaches.
            problems.Add(new FieldProblem(
                "value",
                "A fixed amount off is above 0.",
                "product.promotion.amountNotPositive",
                new Dictionary<string, string> { ["value"] = rawValue }));
        }

        if (type == PromotionType.FixedAmountOff)
        {
            // Shape only, as on a price list — what this refuses is "Euro", "eur " and "€".
            if (currency is not { Length: 3 } || !currency.All(char.IsAsciiLetter))
            {
                problems.Add(new FieldProblem(
                    "currency",
                    "A fixed amount off needs a three-letter ISO-4217 currency, e.g. EUR.",
                    "product.promotion.currencyInvalid",
                    new Dictionary<string, string> { ["currency"] = currency ?? string.Empty }));
            }
        }
        else if (currency is not null)
        {
            // Refused rather than ignored. A caller that sent a currency with a percentage has
            // misunderstood something, and silently dropping it means they find out when a report
            // disagrees with what they thought they authored.
            problems.Add(new FieldProblem(
                "currency",
                "A percentage off has no currency.",
                "product.promotion.currencyNotApplicable"));
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

        return (value, problems.Count > 0 ? Problems.BadRequest(problems) : null);
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
