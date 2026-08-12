using System.Globalization;
using FieldKit.Modules.Outlets.Contracts;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>One minimum as it is stored. The amount is a string, like every other money here.</summary>
public sealed record OrderMinimumResponse(
    Guid Id, Guid? ChannelId, Guid? OutletId, string Amount, string CurrencyCode);

/// <summary>One minimum as an author states it. Exactly one of the two ids is set.</summary>
public sealed record OrderMinimumRequest(
    Guid? ChannelId, Guid? OutletId, string Amount, string CurrencyCode);

/// <summary>Every minimum this tenant has. A PUT replaces the set.</summary>
/// <remarks>
/// An empty set is a real state and the ordinary one: no minimum anywhere, so every order is
/// submittable at any value. It is also how a tenant withdraws one — the same shape a promotion's
/// targets, a tax class's rates and a price list's assignments all use.
/// </remarks>
public sealed record SetOrderMinimumsRequest(IReadOnlyList<OrderMinimumRequest> Minimums);

/// <summary>The minimum that applies at one outlet, or an explicit nothing.</summary>
/// <remarks>
/// A wrapper for the reason <c>TaxResolutionResponse</c> gives: ASP.NET Core writes an empty body
/// for a null result value, and "no minimum applies" is worth saying rather than implying by
/// silence — especially here, where absence means *every order passes* rather than *none do*.
/// </remarks>
public sealed record OrderMinimumResolutionResponse(ResolvedOrderMinimumResponse? Minimum);

/// <summary>The minimum that applies, and where it came from.</summary>
public sealed record ResolvedOrderMinimumResponse(
    Guid OrderMinimumId, string Scope, string Amount, string CurrencyCode);

/// <summary>
/// Order minimums, and which one applies (<c>ORD-06</c>, <c>BR-ORD-5</c>) — W11 slice 8b-i.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authoring here, enforcement elsewhere.</b> This slice gives <c>BR-ORD-5</c> something to read;
/// the refusal a rep meets is 8b-ii, on the device, because "must be met to submit" has to be
/// answered at a counter with no signal. Splitting them is the same call every rule in this module
/// has taken — the resolver is pure so both sides can run it.
/// </para>
/// <para>
/// <b>Per channel with a per-outlet override</b>, from <c>B1</c>. Not invented here, and the third
/// rule in this module to take that shape.
/// </para>
/// </remarks>
internal static class OrderMinimumEndpoints
{
    public static void MapOrderMinimumEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var minimums = endpoints.MapGroup("/api/products/order-minimums").WithTags("Products");

        minimums.MapGet("", async (ProductsDbContext db, CancellationToken ct) =>
            Results.Ok(await AllAsync(db, ct))).RequirePermission(ProductsPermissions.Read);

        minimums.MapPut("", async (
            SetOrderMinimumsRequest request,
            ProductsDbContext db,
            IOutletCatalog outlets,
            IOutletClassification classification,
            CancellationToken ct) =>
        {
            var (checked_, problem) = Checked(request);
            if (problem is not null) return problem;

            if (await ScopeProblem(checked_, outlets, classification, ct) is { } scopeProblem)
            {
                return scopeProblem;
            }

            // Replaced wholesale, like a class's tax rates: a minimum's identity is the scope it
            // applies to, so an author moving one from a channel to an outlet has replaced it rather
            // than edited it, and keeping the row would preserve a CreatedAt describing a rule that
            // no longer exists.
            db.OrderMinimums.RemoveRange(await db.OrderMinimums.ToListAsync(ct));

            foreach (var minimum in checked_)
            {
                db.OrderMinimums.Add(minimum.ChannelId is { } channelId
                    ? OrderMinimum.ForChannel(channelId, minimum.Amount, minimum.CurrencyCode)
                    : OrderMinimum.ForOutlet(minimum.OutletId!.Value, minimum.Amount, minimum.CurrencyCode));
            }

            await db.SaveChangesAsync(ct);

            return Results.Ok(await AllAsync(db, ct));
        }).RequirePermission(ProductsPermissions.Write);

        endpoints.MapGet("/api/products/outlets/{outletId:guid}/order-minimum", async (
            Guid outletId,
            ProductsDbContext db,
            IOutletClassification classification,
            CancellationToken ct) =>
        {
            var classified = await classification.ClassifyManyAsync([outletId], ct);
            if (classified.Count == 0) return Results.NotFound();

            var channelId = classified[0].ChannelId;

            var rows = await db.OrderMinimums
                .Where(row => row.OutletId == outletId || row.ChannelId == channelId)
                .ToListAsync(ct);

            var resolved = OrderMinimumResolver.Resolve(
                [.. rows.Select(row => new OrderMinimumCandidate(
                    row.Id,
                    row.OutletId is null ? OrderMinimumScope.Channel : OrderMinimumScope.Outlet,
                    row.CurrencyCode,
                    WireDecimal.From(row.Amount)))]);

            return Results.Ok(new OrderMinimumResolutionResponse(
                resolved is null
                    ? null
                    : new ResolvedOrderMinimumResponse(
                        resolved.OrderMinimumId,
                        resolved.Scope.ToString(),
                        resolved.Amount,
                        resolved.CurrencyCode)));
        }).RequirePermission(ProductsPermissions.Read).WithTags("Products");
    }

    private static async Task<IReadOnlyList<OrderMinimumResponse>> AllAsync(
        ProductsDbContext db, CancellationToken ct) =>
        [
            .. (await db.OrderMinimums.OrderBy(row => row.CurrencyCode).ToListAsync(ct))
                .Select(row => new OrderMinimumResponse(
                    row.Id, row.ChannelId, row.OutletId, WireDecimal.From(row.Amount), row.CurrencyCode)),
        ];

    /// <summary>One checked minimum, ready to store.</summary>
    private readonly record struct CheckedMinimum(
        Guid? ChannelId, Guid? OutletId, decimal Amount, string CurrencyCode);

    private static (IReadOnlyList<CheckedMinimum> Minimums, IResult? Problem) Checked(
        SetOrderMinimumsRequest request)
    {
        var problems = new List<FieldProblem>();
        var minimums = new List<CheckedMinimum>();

        for (var index = 0; index < request.Minimums.Count; index++)
        {
            var minimum = request.Minimums[index];

            // Exactly one scope, refused by name here as well as by the check constraint — a
            // constraint violation surfaces as a 500 and names a database object at an author.
            if ((minimum.ChannelId is null) == (minimum.OutletId is null))
            {
                problems.Add(new FieldProblem(
                    $"minimums[{index}]",
                    "A minimum applies to a channel or to an outlet, not both and not neither.",
                    "product.orderMinimum.oneScope"));
            }

            // Shape only, as everywhere a currency is taken: what this refuses is "lei", "RONN"
            // and an empty string.
            if (minimum.CurrencyCode is not { Length: 3 } || !minimum.CurrencyCode.All(char.IsAsciiLetter))
            {
                problems.Add(new FieldProblem(
                    $"minimums[{index}].currencyCode",
                    "A currency is a three-letter ISO-4217 code, e.g. RON.",
                    "product.orderMinimum.currencyInvalid"));
            }

            // The same parse and the same refusal of thousands separators as every other amount in
            // this module: `NumberStyles.Number` would read "1,500" as 1500 under invariant culture.
            if (!decimal.TryParse(
                    minimum.Amount,
                    NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var amount))
            {
                problems.Add(new FieldProblem(
                    $"minimums[{index}].amount",
                    $"'{minimum.Amount}' is not a decimal value.",
                    "product.orderMinimum.amountNotANumber"));
            }
            // Zero is refused rather than allowed, unlike a tax rate. A 0.00 tax means zero-rated
            // goods, which is a real commercial fact; a minimum of zero means *no minimum*, which
            // is already expressible by not having a row — and two ways to say the same thing is how
            // a screen ends up showing "minimum: 0.00" at a rep who then wonders what it is for.
            else if (amount <= 0)
            {
                problems.Add(new FieldProblem(
                    $"minimums[{index}].amount",
                    "A minimum is above zero. Remove it instead of setting it to nothing.",
                    "product.orderMinimum.amountNotPositive"));
            }
            else if (minimum.CurrencyCode is { Length: 3 }
                     && (minimum.ChannelId is null) != (minimum.OutletId is null))
            {
                minimums.Add(new CheckedMinimum(
                    minimum.ChannelId, minimum.OutletId, amount, minimum.CurrencyCode.ToUpperInvariant()));
            }
        }

        var duplicates = minimums
            .GroupBy(minimum => (minimum.ChannelId, minimum.OutletId))
            .Count(group => group.Count() > 1);

        if (duplicates > 0)
        {
            // The unique indexes would refuse these anyway; saying so here names the field and the
            // count instead of surfacing a constraint violation.
            problems.Add(new FieldProblem(
                "minimums",
                $"{duplicates} scope(s) appear more than once.",
                "product.orderMinimum.scopeDuplicated",
                new Dictionary<string, string> { ["count"] = duplicates.ToString() }));
        }

        return (minimums, problems.Count > 0 ? Problems.BadRequest(problems) : null);
    }

    /// <summary>
    /// Confirms every scope names something this tenant has.
    /// </summary>
    /// <remarks>
    /// Both ids point into Outlets, which is why there is no foreign key — so the check is a
    /// question asked through the contracts rather than a constraint. Without it a minimum saves
    /// against a channel nobody has and silently applies to nothing, which reads as the rule being
    /// off rather than as a typo.
    /// </remarks>
    private static async Task<IResult?> ScopeProblem(
        IReadOnlyList<CheckedMinimum> minimums,
        IOutletCatalog outlets,
        IOutletClassification classification,
        CancellationToken ct)
    {
        var outletIds = minimums.Where(m => m.OutletId is not null).Select(m => m.OutletId!.Value).ToList();

        if (outletIds.Count > 0)
        {
            var known = await outlets.FindManyAsync(outletIds, ct);

            if (known.Count != outletIds.Distinct().Count())
            {
                return Problems.BadRequest(
                    "minimums", "No such outlet in this tenant.", "product.orderMinimum.unknownOutlet");
            }
        }

        foreach (var channelId in minimums.Where(m => m.ChannelId is not null).Select(m => m.ChannelId!.Value))
        {
            if (!await classification.ChannelExistsAsync(channelId, ct))
            {
                return Problems.BadRequest(
                    "minimums", "No such channel in this tenant.", "product.orderMinimum.unknownChannel");
            }
        }

        return null;
    }
}
