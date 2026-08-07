using FieldKit.Modules.Outlets.Contracts;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>One product in an assortment, and whether it must be stocked.</summary>
public sealed record AssortmentItemResponse(Guid ProductId, string Sku, string Name, bool MustStock);

/// <summary>One line of a channel assortment as an author sets it.</summary>
public sealed record AssortmentLineRequest(Guid ProductId, bool MustStock = false);

/// <summary>The whole assortment for a channel. A PUT replaces it — see the endpoint.</summary>
public sealed record SetAssortmentRequest(IReadOnlyList<AssortmentLineRequest> Items);

/// <summary>
/// Which products belong in which outlets (<c>PRD-02</c>).
/// </summary>
/// <remarks>
/// Assortments are authored per <b>channel</b> and read per <b>outlet</b>, and the join between
/// those is the whole reason this module consumes <see cref="IOutletClassification"/>: Products
/// knows which channel an assortment is for, and only Outlets knows which channel a shop trades in.
/// <para>
/// Per-outlet overrides (<c>B2</c>) and the <c>IAssortmentService</c> contract that Order and Audit
/// will consume are the next slice. This one establishes the channel assortment and the outlet read
/// that resolves through it.
/// </para>
/// </remarks>
internal static class AssortmentEndpoints
{
    public static void MapAssortmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var assortments = endpoints.MapGroup("/api/products/assortments").WithTags("Products");

        assortments.MapGet("/channels/{channelId:guid}", async (
            Guid channelId, ProductsDbContext db, CancellationToken ct) =>
                Results.Ok(await ForChannelAsync(db, channelId, ct)))
            .RequirePermission(ProductsPermissions.Read);

        assortments.MapPut("/channels/{channelId:guid}", async (
            Guid channelId,
            SetAssortmentRequest request,
            ProductsDbContext db,
            IOutletClassification outlets,
            IClock clock,
            CancellationToken ct) =>
        {
            if (await RequestProblem(db, outlets, channelId, request, ct) is { } problem) return problem;

            // Replace, not merge — the same semantics as every other PUT here. An assortment is a
            // set, and a partial update of a set has no obvious meaning: does an absent product mean
            // "leave it" or "remove it"? The screen that edits this renders the whole list and posts
            // the whole list back.
            var existing = await db.AssortmentItems
                .Where(item => item.ChannelId == channelId)
                .ToListAsync(ct);

            var wanted = request.Items.ToDictionary(line => line.ProductId, line => line.MustStock);

            // Rows that survive are updated rather than deleted and re-inserted, so their audit
            // stamps and ids stay put. A product that has been in an assortment since March should
            // not look newly added because somebody toggled a different one.
            foreach (var item in existing)
            {
                if (wanted.TryGetValue(item.ProductId, out var mustStock))
                {
                    if (item.IsMustStock != mustStock) item.Flag(mustStock, clock);
                    wanted.Remove(item.ProductId);
                }
                else
                {
                    db.AssortmentItems.Remove(item);
                }
            }

            foreach (var (productId, mustStock) in wanted)
            {
                db.AssortmentItems.Add(AssortmentItem.Create(channelId, productId, mustStock));
            }

            await db.SaveChangesAsync(ct);

            return Results.Ok(await ForChannelAsync(db, channelId, ct));
        }).RequirePermission(ProductsPermissions.Write);

        assortments.MapGet("/outlets/{outletId:guid}", async (
            Guid outletId, ProductsDbContext db, IOutletClassification outlets, CancellationToken ct) =>
        {
            // The join this module cannot make alone. Products knows which channel an assortment is
            // for; only Outlets knows which channel a shop trades in.
            var classified = await outlets.ClassifyManyAsync([outletId], ct);

            // Absent means the outlet does not exist *for this tenant* — the contract filters by
            // tenant like everything else, so this is a 404 rather than an empty assortment. Those
            // are different answers: one says "no such shop", the other "nothing is sold there".
            if (classified.Count == 0) return Results.NotFound();

            return Results.Ok(await ForChannelAsync(db, classified[0].ChannelId, ct));
        }).RequirePermission(ProductsPermissions.Read);
    }

    /// <summary>Everything in a channel's assortment, must-stock first.</summary>
    /// <remarks>
    /// Ordered before the projection, not after. Sorting on the projected record's properties reads
    /// better but does not translate — EF cannot see through the constructor to the columns
    /// underneath, and the query fails at runtime rather than at compile time. Ordering the joined
    /// pair keeps it in SQL.
    /// </remarks>
    private static async Task<IReadOnlyList<AssortmentItemResponse>> ForChannelAsync(
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

    private static async Task<IResult?> RequestProblem(
        ProductsDbContext db,
        IOutletClassification outlets,
        Guid channelId,
        SetAssortmentRequest request,
        CancellationToken ct)
    {
        var problems = new List<FieldProblem>();

        // Checked through the contract, because Products cannot see the channel table (AT-1).
        // Without this an assortment could name a channel no outlet will ever trade in — it would
        // save cleanly, show nothing wrong, and simply never apply to anybody.
        if (!await outlets.ChannelExistsAsync(channelId, ct))
        {
            problems.Add(new FieldProblem(
                "channelId", "That channel does not exist.", "product.assortment.channelMissing"));
        }

        var duplicates = request.Items
            .GroupBy(line => line.ProductId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        // Refused rather than deduplicated. Two lines for one product disagreeing about must-stock
        // is a request with no single meaning, and picking one silently would make the answer depend
        // on ordering.
        if (duplicates.Count > 0)
        {
            problems.Add(new FieldProblem(
                "items",
                $"{duplicates.Count} product(s) appear more than once.",
                "product.assortment.duplicateProduct",
                new Dictionary<string, string> { ["count"] = duplicates.Count.ToString() }));
        }

        var productIds = request.Items.Select(line => line.ProductId).Distinct().ToList();

        var known = await db.Products
            .Where(product => productIds.Contains(product.Id))
            .Select(product => product.Id)
            .ToListAsync(ct);

        // Tenant-filtered, so another tenant's product reads as "does not exist" — the only answer
        // that does not confirm the id is real somewhere else.
        var missing = productIds.Except(known).ToList();
        if (missing.Count > 0)
        {
            problems.Add(new FieldProblem(
                "items",
                $"{missing.Count} product(s) do not exist.",
                "product.assortment.productMissing",
                new Dictionary<string, string> { ["count"] = missing.Count.ToString() }));
        }

        return problems.Count > 0 ? Problems.BadRequest(problems) : null;
    }
}
