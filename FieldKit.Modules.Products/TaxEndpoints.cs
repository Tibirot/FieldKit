using System.Globalization;
using FieldKit.Modules.Outlets.Contracts;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>One rate of a tax class. The percentage is a string, like every other rate here.</summary>
public sealed record TaxRateResponse(
    Guid Id, string CountryCode, string Percentage, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

/// <summary>One rate as an author states it.</summary>
public sealed record TaxRateRequest(
    string CountryCode, string Percentage, DateOnly EffectiveFrom, DateOnly? EffectiveTo = null);

/// <summary>Every rate of a tax class. A PUT replaces the set.</summary>
/// <remarks>
/// An empty set is allowed and means the class has no rate anywhere — which resolves to
/// <i>unknown</i>, not to zero. See <see cref="TaxEngine.Resolve"/>.
/// </remarks>
public sealed record SetTaxRatesRequest(IReadOnlyList<TaxRateRequest> Rates);

/// <summary>The tax that applies to a product at an outlet, or an explicit nothing.</summary>
/// <remarks>
/// <para>
/// A wrapper, like <see cref="PromotionResolutionResponse"/>, and for the same two reasons: ASP.NET
/// Core writes an empty body for a null result value, and "no rate applies" is an answer worth
/// stating rather than implying by silence.
/// </para>
/// <para>
/// <b><c>Tax</c> being null does not mean zero.</b> It means this line's tax is <i>unknown</i> —
/// either the outlet has no country on its address, or nobody has authored a rate for this class
/// there on this date. A caller that treats it as zero invoices untaxed and looks deliberate doing
/// it, which is the whole reason a 0% rate is authorable and distinguishable from an absent one.
/// </para>
/// </remarks>
public sealed record TaxResolutionResponse(ResolvedTaxResponse? Tax);

/// <summary>The rate that applies, and where it came from.</summary>
public sealed record ResolvedTaxResponse(
    Guid TaxRateId, Guid TaxClassId, string CountryCode, string Percentage);

/// <summary>
/// Tax rates, and which one applies (<c>PRD-07</c>, <c>BR-PRD-5</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Rates hang off a tax class, not off a product.</b> A product is classified once
/// (<c>PRD-01</c>); what that classification costs is a fact about a jurisdiction, and a tenant
/// selling in four countries authors four rates rather than re-classifying every SKU.
/// </para>
/// <para>
/// <b>Computation is not here.</b> Turning a net line into net + tax + gross is
/// <see cref="TaxEngine.Apply"/>, a pure function with vectors, because that is where
/// <c>BR-PRD-9</c>'s rounding has to agree with the device to the cent. This endpoint answers which
/// rate; composing price, promotion and tax into a line total is Order's, in Phase 3.
/// </para>
/// </remarks>
internal static class TaxEndpoints
{
    public static void MapTaxEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var classes = endpoints.MapGroup("/api/products/tax-classes").WithTags("Products");

        classes.MapGet("/{id:guid}/rates", async (
            Guid id, ProductsDbContext db, CancellationToken ct) =>
        {
            if (!await db.TaxClasses.AnyAsync(c => c.Id == id, ct)) return Results.NotFound();

            return Results.Ok(await RatesAsync(db, id, ct));
        }).RequirePermission(ProductsPermissions.Read);

        classes.MapPut("/{id:guid}/rates", async (
            Guid id, SetTaxRatesRequest request, ProductsDbContext db, CancellationToken ct) =>
        {
            if (!await db.TaxClasses.AnyAsync(c => c.Id == id, ct)) return Results.NotFound();

            var (rates, problem) = RateProblem(request);
            if (problem is not null) return problem;

            // Replaced wholesale, like tiers: a rate's identity is its country and start date
            // together, so an author moving a rate's effective date has replaced it rather than
            // edited it, and keeping the row would preserve a CreatedAt describing a rule that no
            // longer exists.
            db.TaxRates.RemoveRange(
                await db.TaxRates.Where(rate => rate.TaxClassId == id).ToListAsync(ct));

            foreach (var rate in rates)
            {
                db.TaxRates.Add(TaxRate.Create(
                    id, rate.CountryCode, rate.Percentage, rate.EffectiveFrom, rate.EffectiveTo));
            }

            await db.SaveChangesAsync(ct);

            return Results.Ok(await RatesAsync(db, id, ct));
        }).RequirePermission(ProductsPermissions.Write);

        var outletTax = endpoints
            .MapGroup("/api/products/outlets/{outletId:guid}/tax")
            .WithTags("Products");

        outletTax.MapGet("", async (
            Guid outletId,
            string? on,
            Guid? productId,
            ProductsDbContext db,
            IOutletClassification classification,
            CancellationToken ct) =>
        {
            if (BusinessDate.Parse(
                    on, "product.tax.dateRequired", "product.tax.dateMalformed", out var date)
                is { } dateProblem)
            {
                return dateProblem;
            }

            if (productId is null)
            {
                return Problems.BadRequest(
                    "productId",
                    "A product is required — tax follows the product's classification.",
                    "product.tax.productRequired");
            }

            var classified = await classification.ClassifyManyAsync([outletId], ct);
            if (classified.Count == 0) return Results.NotFound();

            // No country on the outlet is *unknown tax*, not untaxed. Answered rather than refused,
            // so a rep pricing at a shop whose address was never completed gets the same shape as
            // anywhere else — and a caller that mistakes it for zero has ignored a documented null
            // rather than been misled by a number.
            if (classified[0].CountryCode is not { } countryCode)
            {
                return Results.Ok(new TaxResolutionResponse(null));
            }

            var taxClassId = await db.Products
                .Where(product => product.Id == productId)
                .Select(product => product.TaxClassId)
                .SingleOrDefaultAsync(ct);

            // A product with no tax class is the same kind of unknown: it has not been said what
            // kind of thing this is, so nothing can say what it costs to sell.
            if (taxClassId is not { } classId) return Results.Ok(new TaxResolutionResponse(null));

            var candidates = await db.TaxRates
                .Where(rate => rate.TaxClassId == classId && rate.CountryCode == countryCode)
                .Select(rate => new TaxRateCandidate(
                    rate.Id, rate.Percentage, rate.EffectiveFrom, rate.EffectiveTo))
                .ToListAsync(ct);

            var resolved = TaxEngine.Resolve(candidates, date);

            return Results.Ok(new TaxResolutionResponse(
                resolved is null
                    ? null
                    : new ResolvedTaxResponse(
                        resolved.TaxRateId, classId, countryCode, Format(resolved.Percentage))));
        }).RequirePermission(ProductsPermissions.Read);
    }

    private static string Format(decimal value) =>
        value.ToString("0.00##", CultureInfo.InvariantCulture);

    private static async Task<IReadOnlyList<TaxRateResponse>> RatesAsync(
        ProductsDbContext db, Guid taxClassId, CancellationToken ct) =>
        [
            .. (await db.TaxRates
                    .Where(rate => rate.TaxClassId == taxClassId)
                    .OrderBy(rate => rate.CountryCode)
                    .ThenBy(rate => rate.EffectiveFrom)
                    .ToListAsync(ct))
                .Select(rate => new TaxRateResponse(
                    rate.Id, rate.CountryCode, Format(rate.Percentage),
                    rate.EffectiveFrom, rate.EffectiveTo)),
        ];

    /// <summary>One checked rate, ready to store.</summary>
    private readonly record struct CheckedRate(
        string CountryCode, decimal Percentage, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

    private static (IReadOnlyList<CheckedRate> Rates, IResult? Problem) RateProblem(
        SetTaxRatesRequest request)
    {
        var problems = new List<FieldProblem>();
        var rates = new List<CheckedRate>();

        for (var index = 0; index < request.Rates.Count; index++)
        {
            var rate = request.Rates[index];

            // Shape only, as with a currency — what this refuses is "Romania", "ro " and "ROU".
            if (rate.CountryCode is not { Length: 2 } || !rate.CountryCode.All(char.IsAsciiLetter))
            {
                problems.Add(new FieldProblem(
                    $"rates[{index}].countryCode",
                    "A country is a two-letter ISO-3166-1 code, e.g. RO.",
                    "product.tax.countryInvalid",
                    new Dictionary<string, string> { ["countryCode"] = rate.CountryCode ?? string.Empty }));
            }

            // Same parse and the same refusal of thousands separators as every other rate in this
            // module: NumberStyles.Number would make "19,5" parse to 195 under invariant culture.
            if (!decimal.TryParse(
                    rate.Percentage,
                    NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var percentage))
            {
                problems.Add(new FieldProblem(
                    $"rates[{index}].percentage",
                    $"'{rate.Percentage}' is not a decimal value.",
                    "product.tax.percentageNotANumber",
                    new Dictionary<string, string> { ["percentage"] = rate.Percentage ?? string.Empty }));
            }
            // Zero is allowed here, unlike a zero discount — it is how zero-rated goods are taxed,
            // and forcing a tenant to express that by omitting a rate would make "no VAT" and "we
            // never set this up" the same state. Negative is not: a tax that pays the shop is a
            // subsidy, and nothing downstream could sensibly handle one.
            else if (percentage is < 0 or > 100)
            {
                problems.Add(new FieldProblem(
                    $"rates[{index}].percentage",
                    "A tax rate is between 0 and 100.",
                    "product.tax.percentageOutOfRange",
                    new Dictionary<string, string> { ["percentage"] = rate.Percentage }));
            }
            else if (rate.CountryCode is { Length: 2 })
            {
                rates.Add(new CheckedRate(
                    rate.CountryCode.ToUpperInvariant(), percentage, rate.EffectiveFrom, rate.EffectiveTo));
            }

            // Half-open, so equal dates are an empty window — a rate that never applies.
            if (rate.EffectiveTo is { } end && end <= rate.EffectiveFrom)
            {
                problems.Add(new FieldProblem(
                    $"rates[{index}].effectiveTo",
                    "A rate ends after it starts.",
                    "product.tax.windowInverted"));
            }
        }

        var duplicates = rates
            .GroupBy(rate => (rate.CountryCode, rate.EffectiveFrom))
            .Count(group => group.Count() > 1);

        if (duplicates > 0)
        {
            // The unique index would refuse these anyway; saying so here names the field and the
            // count instead of surfacing a constraint violation.
            problems.Add(new FieldProblem(
                "rates",
                $"{duplicates} country and start date pair(s) appear more than once.",
                "product.tax.rateDuplicated",
                new Dictionary<string, string> { ["count"] = duplicates.ToString() }));
        }

        return (rates, problems.Count > 0 ? Problems.BadRequest(problems) : null);
    }
}
