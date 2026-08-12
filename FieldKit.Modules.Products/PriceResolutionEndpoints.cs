using System.Globalization;
using System.Text.Json.Serialization;
using FieldKit.Modules.Outlets.Contracts;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>
/// What one product costs at one outlet on one date, and which list said so.
/// </summary>
/// <remarks>
/// <c>Price</c> is a <see cref="Money"/>, so it crosses the wire as
/// <c>{ "amount": "12.50", "currency": "EUR" }</c> (<c>BR-PRD-8</c>) rather than as a JSON number a
/// JavaScript client would parse into a float.
/// <para>
/// <c>PriceListId</c> and <c>Scope</c> are the answer to "why". A rep told a price they did not
/// expect asks their supervisor, and a supervisor who can see *which list* and *whether it was set
/// for this shop or its whole channel* can answer without opening the database.
/// </para>
/// </remarks>
public sealed record ResolvedPriceResponse(
    Guid ProductId,
    Money Price,
    Guid PriceListId,
    // By name, not by ordinal — matching OutletResponse.Status and ProductResponse.Status. On the
    // property rather than in global options so the mapping is symmetric: a .NET client
    // deserializing this record gets it without configuring anything.
    PriceScope Scope);

/// <summary>
/// Resolving prices for an outlet (<c>PRD-04</c>, <c>BR-PRD-2</c>).
/// </summary>
/// <remarks>
/// <para>
/// Thin on purpose. Everything this file does is gather candidates and hand them to
/// <see cref="PriceResolver"/>; the precedence rules live there, in a pure function, because W7 has
/// to reimplement them in TypeScript and a rule embedded in a LINQ query cannot be reimplemented,
/// only re-derived. Anything that reads like a pricing decision in here is a bug.
/// </para>
/// <para>
/// <b>The date is required.</b> Defaulting to today would mean the server's today — and an outlet in
/// Bucharest changes day six hours before one in London (<c>BR-PRD-6</c>), so a default would quietly
/// price some shops against yesterday's list for part of every day. Worse, it would make the endpoint
/// non-reproducible: an order re-priced during sync must resolve to the price it was taken at, which
/// only holds if the caller says which day it means.
/// </para>
/// </remarks>
internal static class PriceResolutionEndpoints
{
    public static void MapPriceResolutionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var outletPrices = endpoints
            .MapGroup("/api/products/outlets/{outletId:guid}/prices")
            .WithTags("Products");

        outletPrices.MapGet("", async (
            Guid outletId,
            string? on,
            Guid[] productId,
            ProductsDbContext db,
            IOutletClassification classification,
            CancellationToken ct) =>
        {
            if (BusinessDate.Parse(
                    on, "product.price.dateRequired", "product.price.dateMalformed", out var date)
                is { } problem)
            {
                return problem;
            }

            // Which channel this shop trades in decides which lists reach it. Through the contract,
            // because Products cannot see the outlet table (AT-1) — and tenant-filtered by it, so
            // another tenant's outlet is simply absent here and reads as 404 below.
            var classified = await classification.ClassifyManyAsync([outletId], ct);
            if (classified.Count == 0) return Results.NotFound();

            var channelId = classified[0].ChannelId;
            var requested = productId.Distinct().ToList();

            var rows = await CandidatesAsync(db, outletId, channelId, date, requested, ct);

            // Grouped in memory rather than in SQL: the rows are already the small set that survived
            // the window filter, and the grouping exists only to hand the resolver one product's
            // candidates at a time.
            var resolved = rows
                .GroupBy(row => row.ProductId)
                .Select(group => new
                {
                    ProductId = group.Key,
                    Price = PriceResolver.Resolve([.. group.Select(row => row.Candidate)], date),
                })
                .Where(entry => entry.Price is not null)
                .Select(entry => new ResolvedPriceResponse(
                    entry.ProductId,
                    new Money(entry.Price!.Amount, entry.Price.Currency),
                    entry.Price.PriceListId,
                    entry.Price.Scope))
                .OrderBy(entry => entry.ProductId)
                .ToList();

            return Results.Ok(resolved);
        }).RequirePermission(ProductsPermissions.Read);
    }

    /// <summary>
    /// Every price this outlet could be charged on <paramref name="date"/>, flattened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window filter is here rather than left to the resolver, even though the resolver applies it
    /// too. That is not redundancy for its own sake: a tenant accumulates price lists for years, and
    /// loading all of them to discard all but one date's worth would grow the query without bound.
    /// The resolver re-checks because it is a pure function that cannot assume its caller filtered —
    /// and because the vectors exercise the check directly.
    /// </para>
    /// <para>
    /// <b>A list assigned to both this outlet and its channel yields two candidates</b>, one at each
    /// scope. Left alone deliberately: the outlet-scoped one wins by <c>BR-PRD-2</c>, which is the
    /// right answer and needs no special case. Collapsing the pair here would be the resolver's rule,
    /// duplicated in SQL, where W7 cannot see it.
    /// </para>
    /// </remarks>
    private static async Task<List<(Guid ProductId, PriceCandidate Candidate)>> CandidatesAsync(
        ProductsDbContext db,
        Guid outletId,
        Guid channelId,
        DateOnly date,
        IReadOnlyList<Guid> productIds,
        CancellationToken ct)
    {
        var query =
            from assignment in db.PriceListAssignments
            where assignment.OutletId == outletId || assignment.ChannelId == channelId
            join list in db.PriceLists on assignment.PriceListId equals list.Id
            where list.EffectiveFrom <= date && (list.EffectiveTo == null || date < list.EffectiveTo)
            join line in db.PriceListLines on list.Id equals line.PriceListId
            select new
            {
                line.ProductId,
                list.Id,
                list.Currency,
                list.EffectiveFrom,
                list.EffectiveTo,
                line.Amount,
                assignment.OutletId,
            };

        if (productIds.Count > 0) query = query.Where(row => productIds.Contains(row.ProductId));

        return
        [
            .. (await query.ToListAsync(ct)).Select(row => (
                row.ProductId,
                new PriceCandidate(
                    row.Id,
                    row.OutletId is null ? PriceScope.Channel : PriceScope.Outlet,
                    row.Currency,
                    row.EffectiveFrom,
                    row.EffectiveTo,
                    row.Amount))),
        ];
    }

}
