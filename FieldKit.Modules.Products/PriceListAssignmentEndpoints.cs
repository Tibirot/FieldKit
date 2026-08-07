using FieldKit.Modules.Outlets.Contracts;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>Where a price list applies. Exactly one of the two ids is set on each entry.</summary>
public sealed record AssignmentResponse(Guid? ChannelId, Guid? OutletId);

/// <summary>The whole scope of a price list. A PUT replaces it.</summary>
public sealed record SetAssignmentsRequest(
    IReadOnlyList<Guid> ChannelIds, IReadOnlyList<Guid> OutletIds);

/// <summary>
/// Which outlets a price list reaches (<c>PRD-03</c>).
/// </summary>
/// <remarks>
/// Two scopes, checked against two different Outlets contracts: a channel through
/// <see cref="IOutletClassification.ChannelExistsAsync"/>, an outlet through
/// <see cref="IOutletCatalog.FindManyAsync"/>. Products cannot see either table (AT-1), and an
/// assignment naming something that does not exist would save cleanly and price nobody.
/// <para>
/// <b>Resolution is not here.</b> This records where a list applies; working out which list wins for
/// a given outlet and date — outlet over channel, then most-recent effective (<c>BR-PRD-2</c>) — is
/// <c>PRD-04</c> and the next slice. Storing the scope without the precedence keeps the rule in the
/// resolver where it can be read, rather than spread across rows.
/// </para>
/// </remarks>
internal static class PriceListAssignmentEndpoints
{
    public static void MapPriceListAssignmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var lists = endpoints.MapGroup("/api/products/price-lists").WithTags("Products");

        lists.MapGet("/{id:guid}/assignments", async (
            Guid id, ProductsDbContext db, CancellationToken ct) =>
        {
            if (!await db.PriceLists.AnyAsync(l => l.Id == id, ct)) return Results.NotFound();

            return Results.Ok(await AssignmentsAsync(db, id, ct));
        }).RequirePermission(ProductsPermissions.Read);

        lists.MapPut("/{id:guid}/assignments", async (
            Guid id,
            SetAssignmentsRequest request,
            ProductsDbContext db,
            IOutletClassification classification,
            IOutletCatalog outlets,
            IClock clock,
            CancellationToken ct) =>
        {
            var list = await db.PriceLists.SingleOrDefaultAsync(l => l.Id == id, ct);
            if (list is null) return Results.NotFound();

            if (await ScopeProblem(request, classification, outlets, ct) is { } problem) return problem;

            var existing = await db.PriceListAssignments
                .Where(assignment => assignment.PriceListId == id)
                .ToListAsync(ct);

            var wantedChannels = request.ChannelIds.ToHashSet();
            var wantedOutlets = request.OutletIds.ToHashSet();

            foreach (var assignment in existing)
            {
                var keep = assignment.ChannelId is { } channelId
                    ? wantedChannels.Remove(channelId)
                    : wantedOutlets.Remove(assignment.OutletId!.Value);

                if (!keep) db.PriceListAssignments.Remove(assignment);
            }

            foreach (var channelId in wantedChannels)
            {
                db.PriceListAssignments.Add(PriceListAssignment.ToChannel(id, channelId));
            }

            foreach (var outletId in wantedOutlets)
            {
                db.PriceListAssignments.Add(PriceListAssignment.ToOutlet(id, outletId));
            }

            // Raised on the list, because the list is the thing that became reachable. Written to
            // the outbox in the same transaction as the rows above (ADR-0006), so a device is never
            // told about a scope the database did not keep.
            list.Publish(
                request.ChannelIds.Distinct().Count(), request.OutletIds.Distinct().Count(), clock);

            await db.SaveChangesAsync(ct);

            return Results.Ok(await AssignmentsAsync(db, id, ct));
        }).RequirePermission(ProductsPermissions.Write);
    }

    private static async Task<IReadOnlyList<AssignmentResponse>> AssignmentsAsync(
        ProductsDbContext db, Guid priceListId, CancellationToken ct) =>
        await db.PriceListAssignments
            .Where(assignment => assignment.PriceListId == priceListId)
            .OrderBy(assignment => assignment.ChannelId)
            .ThenBy(assignment => assignment.OutletId)
            .Select(assignment => new AssignmentResponse(assignment.ChannelId, assignment.OutletId))
            .ToListAsync(ct);

    private static async Task<IResult?> ScopeProblem(
        SetAssignmentsRequest request,
        IOutletClassification classification,
        IOutletCatalog outlets,
        CancellationToken ct)
    {
        var problems = new List<FieldProblem>();

        // One call per channel, because the contract offers a predicate rather than a list — Products
        // has no business enumerating a tenant's channels. The set an author assigns is small (a
        // tenant has a handful of channels), so this is a handful of round trips at authoring time
        // rather than anything on a read path.
        foreach (var channelId in request.ChannelIds.Distinct())
        {
            if (!await classification.ChannelExistsAsync(channelId, ct))
            {
                problems.Add(new FieldProblem(
                    "channelIds",
                    "That channel does not exist.",
                    "product.priceList.channelMissing",
                    new Dictionary<string, string> { ["channelId"] = channelId.ToString() }));
            }
        }

        var outletIds = request.OutletIds.Distinct().ToList();
        if (outletIds.Count > 0)
        {
            // Batch, because IOutletCatalog offers one — and outlets are the scope a tenant assigns
            // many of, where per-id calls would actually cost something.
            var known = await outlets.FindManyAsync(outletIds, ct);
            var missing = outletIds.Count - known.Count;

            // Tenant-filtered by the contract, so another tenant's outlet reads as missing — the only
            // answer that does not confirm it exists elsewhere.
            if (missing > 0)
            {
                problems.Add(new FieldProblem(
                    "outletIds",
                    $"{missing} outlet(s) do not exist.",
                    "product.priceList.outletMissing",
                    new Dictionary<string, string> { ["count"] = missing.ToString() }));
            }
        }

        return problems.Count > 0 ? Problems.BadRequest(problems) : null;
    }
}
