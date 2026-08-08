using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>A price list, without its prices.</summary>
public sealed record PriceListResponse(
    Guid Id, string Name, string Currency, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

/// <summary>Create a price list. The currency is fixed at creation — see the endpoint.</summary>
public sealed record CreatePriceListRequest(
    string Name, string Currency, DateOnly EffectiveFrom, DateOnly? EffectiveTo = null);

/// <summary>Rename or re-date a price list. No currency: changing it would reinterpret every price.</summary>
public sealed record UpdatePriceListRequest(string Name, DateOnly EffectiveFrom, DateOnly? EffectiveTo = null);

/// <summary>One product's price, as money rather than a bare number.</summary>
public sealed record PriceResponse(Guid ProductId, string Sku, string Name, Money Price);

/// <summary>One line as an author sets it. The amount is a string for the reason in the converter.</summary>
public sealed record PriceLineRequest(Guid ProductId, string Amount);

/// <summary>The whole price list. A PUT replaces it.</summary>
public sealed record SetPricesRequest(IReadOnlyList<PriceLineRequest> Prices);

/// <summary>
/// What products cost (<c>PRD-03</c>).
/// </summary>
/// <remarks>
/// <b>Assignment to a channel or outlet is not here.</b> A list exists and has prices; which outlets
/// it applies to, and the <c>PriceListPublished</c> event that announces it, are the next slice.
/// Until then a list is authored and read directly, which is enough to get the money-on-the-wire
/// contract and the decimal storage right before anything depends on them.
/// </remarks>
internal static class PriceListEndpoints
{
    public static void MapPriceListEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var lists = endpoints.MapGroup("/api/products/price-lists").WithTags("Products");

        lists.MapGet("/", async (ProductsDbContext db, CancellationToken ct) =>
                await db.PriceLists
                    .OrderBy(list => list.EffectiveFrom)
                    .ThenBy(list => list.Name)
                    .Select(list => new PriceListResponse(
                        list.Id, list.Name, list.Currency, list.EffectiveFrom, list.EffectiveTo))
                    .ToListAsync(ct))
            .RequirePermission(ProductsPermissions.Read);

        lists.MapPost("/", async (
            CreatePriceListRequest request, ProductsDbContext db, CancellationToken ct) =>
        {
            if (await ListProblem(db, request.Name, request.Currency, request.EffectiveFrom,
                    request.EffectiveTo, excluding: null, ct) is { } problem)
            {
                return problem;
            }

            var created = PriceList.Create(
                request.Name, request.Currency, request.EffectiveFrom, request.EffectiveTo);

            db.PriceLists.Add(created);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/products/price-lists/{created.Id}", Respond(created));
        }).RequirePermission(ProductsPermissions.Write);

        lists.MapPut("/{id:guid}", async (
            Guid id,
            UpdatePriceListRequest request,
            ProductsDbContext db,
            IClock clock,
            CancellationToken ct) =>
        {
            var list = await db.PriceLists.SingleOrDefaultAsync(l => l.Id == id, ct);
            if (list is null) return Results.NotFound();

            // The currency is deliberately absent from the request. Changing it would reinterpret
            // every price in the list — 12.50 EUR becoming 12.50 RON is not a conversion, it is a
            // different number wearing the old one's clothes.
            if (await ListProblem(db, request.Name, list.Currency, request.EffectiveFrom,
                    request.EffectiveTo, excluding: id, ct) is { } problem)
            {
                return problem;
            }

            list.Update(request.Name, request.EffectiveFrom, request.EffectiveTo, clock);
            await db.SaveChangesAsync(ct);

            return Results.Ok(Respond(list));
        }).RequirePermission(ProductsPermissions.Write);

        lists.MapGet("/{id:guid}/prices", async (
            Guid id, ProductsDbContext db, CancellationToken ct) =>
        {
            var list = await db.PriceLists.SingleOrDefaultAsync(l => l.Id == id, ct);
            if (list is null) return Results.NotFound();

            return Results.Ok(await PricesAsync(db, list, ct));
        }).RequirePermission(ProductsPermissions.Read);

        lists.MapPut("/{id:guid}/prices", async (
            Guid id,
            SetPricesRequest request,
            ProductsDbContext db,
            IClock clock,
            CancellationToken ct) =>
        {
            var list = await db.PriceLists.SingleOrDefaultAsync(l => l.Id == id, ct);
            if (list is null) return Results.NotFound();

            var (amounts, problem) = await PriceProblem(db, request, ct);
            if (problem is not null) return problem;

            var existing = await db.PriceListLines.Where(l => l.PriceListId == id).ToListAsync(ct);

            // Replace, like every other set in this module — and surviving rows are repriced rather
            // than deleted and re-inserted, so a price unchanged since March does not look newly set
            // because a different product moved.
            foreach (var line in existing)
            {
                if (amounts.TryGetValue(line.ProductId, out var amount))
                {
                    if (line.Amount != amount) line.Reprice(amount, clock);
                    amounts.Remove(line.ProductId);
                }
                else
                {
                    db.PriceListLines.Remove(line);
                }
            }

            foreach (var (productId, amount) in amounts)
            {
                db.PriceListLines.Add(PriceListLine.Create(id, productId, amount));
            }

            await db.SaveChangesAsync(ct);

            return Results.Ok(await PricesAsync(db, list, ct));
        }).RequirePermission(ProductsPermissions.Write);
    }

    private static PriceListResponse Respond(PriceList list) =>
        new(list.Id, list.Name, list.Currency, list.EffectiveFrom, list.EffectiveTo);

    /// <summary>
    /// The list's prices, each reassembled into <see cref="Money"/> with the list's currency.
    /// </summary>
    /// <remarks>
    /// The currency is attached here rather than stored per line, so nothing downstream ever handles
    /// a bare decimal it has to remember the units of.
    /// </remarks>
    private static async Task<IReadOnlyList<PriceResponse>> PricesAsync(
        ProductsDbContext db, PriceList list, CancellationToken ct)
    {
        var rows = await db.PriceListLines
            .Where(line => line.PriceListId == list.Id)
            .Join(
                db.Products,
                line => line.ProductId,
                product => product.Id,
                (line, product) => new { line.Amount, product })
            .OrderBy(pair => pair.product.Sku)
            .ToListAsync(ct);

        return [.. rows.Select(row => new PriceResponse(
            row.product.Id, row.product.Sku, row.product.Name, new Money(row.Amount, list.Currency)))];
    }

    private static async Task<IResult?> ListProblem(
        ProductsDbContext db,
        string name,
        string currency,
        DateOnly from,
        DateOnly? to,
        Guid? excluding,
        CancellationToken ct)
    {
        var problems = new List<FieldProblem>();

        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add(new FieldProblem("name", "A price list needs a name.", "product.priceList.nameRequired"));
        }

        if (TextLimits.TooLong("name", name, 120, "product.priceList.nameTooLong") is { } tooLong)
        {
            problems.Add(tooLong);
        }

        // Shape only — this is not a table of the world's currencies. What it refuses is "Euro",
        // "eur " and "€", which are the ways a caller gets this wrong in practice.
        if (currency is not { Length: 3 } || !currency.All(char.IsAsciiLetter))
        {
            problems.Add(new FieldProblem(
                "currency",
                "A currency is a three-letter ISO-4217 code, e.g. EUR.",
                "product.priceList.currencyInvalid",
                new Dictionary<string, string> { ["currency"] = currency ?? string.Empty }));
        }

        // Half-open, so equal dates are an empty window rather than a single day — a list that is
        // never in effect, which is certainly not what anyone meant to author.
        if (to is { } end && end <= from)
        {
            problems.Add(new FieldProblem(
                "effectiveTo",
                "A price list ends after it starts.",
                "product.priceList.windowInverted"));
        }

        var taken = await db.PriceLists.AnyAsync(
            list => list.Name.ToLower() == name.ToLower() && (excluding == null || list.Id != excluding),
            ct);

        if (taken)
        {
            problems.Add(new FieldProblem(
                "name",
                $"A price list named '{name}' already exists.",
                "product.priceList.nameTaken",
                new Dictionary<string, string> { ["name"] = name }));
        }

        return problems.Count > 0 ? Problems.BadRequest(problems) : null;
    }

    /// <summary>Parses and checks every line, returning the amounts by product.</summary>
    private static async Task<(Dictionary<Guid, decimal> Amounts, IResult? Problem)> PriceProblem(
        ProductsDbContext db, SetPricesRequest request, CancellationToken ct)
    {
        var problems = new List<FieldProblem>();
        var amounts = new Dictionary<Guid, decimal>();

        var duplicates = request.Prices
            .GroupBy(line => line.ProductId)
            .Count(group => group.Count() > 1);

        if (duplicates > 0)
        {
            problems.Add(new FieldProblem(
                "prices",
                $"{duplicates} product(s) are priced more than once.",
                "product.price.duplicateProduct",
                new Dictionary<string, string> { ["count"] = duplicates.ToString() }));
        }

        foreach (var line in request.Prices)
        {
            // Invariant culture AND no thousands separators. NumberStyles.Number allows them, which
            // makes "12,50" parse as 1250 under invariant culture — a hundredfold error that reads
            // as a plausible price. A caller sending a grouped amount is a caller whose locale is
            // leaking; refusing is the only answer that cannot be silently wrong.
            if (!decimal.TryParse(
                    line.Amount,
                    System.Globalization.NumberStyles.AllowDecimalPoint
                        | System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var amount))
            {
                problems.Add(new FieldProblem(
                    "prices",
                    $"'{line.Amount}' is not a decimal amount.",
                    "product.price.notANumber",
                    new Dictionary<string, string> { ["amount"] = line.Amount ?? string.Empty }));
                continue;
            }

            // Zero is allowed — a free line, a sample, a listing fee absorbed elsewhere. Negative is
            // not: a price that pays the shop is a rebate, which is a promotion's job, and letting
            // one in here would have every total quietly able to go the wrong way.
            if (amount < 0)
            {
                problems.Add(new FieldProblem(
                    "prices",
                    "A price is not negative.",
                    "product.price.negative",
                    new Dictionary<string, string> { ["amount"] = line.Amount }));
                continue;
            }

            amounts[line.ProductId] = amount;
        }

        var productIds = amounts.Keys.ToList();

        var known = await db.Products
            .Where(product => productIds.Contains(product.Id))
            .Select(product => product.Id)
            .ToListAsync(ct);

        var missing = productIds.Except(known).Count();
        if (missing > 0)
        {
            problems.Add(new FieldProblem(
                "prices",
                $"{missing} product(s) do not exist.",
                "product.price.productMissing",
                new Dictionary<string, string> { ["count"] = missing.ToString() }));
        }

        return (amounts, problems.Count > 0 ? Problems.BadRequest(problems) : null);
    }
}
