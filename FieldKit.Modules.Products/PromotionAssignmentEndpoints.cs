using FieldKit.Modules.Outlets.Contracts;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>Where a promotion applies. Exactly one of the two ids is set on each entry.</summary>
public sealed record PromotionAssignmentResponse(Guid? ChannelId, Guid? OutletId);

/// <summary>The whole scope of a promotion. A PUT replaces it; an empty scope withdraws it.</summary>
public sealed record SetPromotionScopeRequest(
    IReadOnlyList<Guid> ChannelIds, IReadOnlyList<Guid> OutletIds);

/// <summary>
/// Which outlets a promotion reaches (<c>PRD-05</c>).
/// </summary>
/// <remarks>
/// <para>
/// The last thing a promotion needs before it does anything. Type, value, targets and window all
/// describe a rule; this says who it happens to — and until it is set, a promotion is a draft that
/// discounts nobody, which is the same intermediate state a price list passes through.
/// </para>
/// <para>
/// Two scopes, checked against two different Outlets contracts: a channel through
/// <see cref="IOutletClassification.ChannelExistsAsync"/>, an outlet through
/// <see cref="IOutletCatalog.FindManyAsync"/>. Products cannot see either table (AT-1), and a scope
/// naming something that does not exist would save cleanly and reach nobody.
/// </para>
/// <para>
/// <b>Selection is not here.</b> Which promotion wins for an order line — one per line, by priority,
/// within the window (<c>BR-PRD-3</c>, <c>BR-PRD-6</c>) — is <c>PRD-06</c>, and it will read these
/// rows the way <c>PriceResolver</c> reads price-list assignments. Recording reach without the
/// precedence keeps the rule in the resolver where it can be read, rather than spread across rows.
/// </para>
/// </remarks>
internal static class PromotionAssignmentEndpoints
{
    public static void MapPromotionAssignmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var promotions = endpoints.MapGroup("/api/products/promotions").WithTags("Products");

        promotions.MapGet("/{id:guid}/assignments", async (
            Guid id, ProductsDbContext db, CancellationToken ct) =>
        {
            if (!await db.Promotions.AnyAsync(p => p.Id == id, ct)) return Results.NotFound();

            return Results.Ok(await AssignmentsAsync(db, id, ct));
        }).RequirePermission(ProductsPermissions.Read);

        promotions.MapPut("/{id:guid}/assignments", async (
            Guid id,
            SetPromotionScopeRequest request,
            ProductsDbContext db,
            IOutletClassification classification,
            IOutletCatalog outlets,
            IClock clock,
            CancellationToken ct) =>
        {
            var promotion = await db.Promotions.SingleOrDefaultAsync(p => p.Id == id, ct);
            if (promotion is null) return Results.NotFound();

            if (await ScopeProblem(request, classification, outlets, ct) is { } problem) return problem;

            var existing = await db.PromotionAssignments
                .Where(assignment => assignment.PromotionId == id)
                .ToListAsync(ct);

            var wantedChannels = request.ChannelIds.ToHashSet();
            var wantedOutlets = request.OutletIds.ToHashSet();

            foreach (var assignment in existing)
            {
                var keep = assignment.ChannelId is { } channelId
                    ? wantedChannels.Remove(channelId)
                    : wantedOutlets.Remove(assignment.OutletId!.Value);

                if (!keep) db.PromotionAssignments.Remove(assignment);
            }

            foreach (var channelId in wantedChannels)
            {
                db.PromotionAssignments.Add(PromotionAssignment.ToChannel(id, channelId));
            }

            foreach (var outletId in wantedOutlets)
            {
                db.PromotionAssignments.Add(PromotionAssignment.ToOutlet(id, outletId));
            }

            // Raised on the promotion, because the promotion is the thing that became live. Written
            // to the outbox in the same transaction as the rows above (ADR-0006), so a device is
            // never told about a scope the database did not keep.
            promotion.Activate(
                request.ChannelIds.Distinct().Count(), request.OutletIds.Distinct().Count(), clock);

            await db.SaveChangesAsync(ct);

            return Results.Ok(await AssignmentsAsync(db, id, ct));
        }).RequirePermission(ProductsPermissions.Write);
    }

    private static async Task<IReadOnlyList<PromotionAssignmentResponse>> AssignmentsAsync(
        ProductsDbContext db, Guid promotionId, CancellationToken ct) =>
        await db.PromotionAssignments
            .Where(assignment => assignment.PromotionId == promotionId)
            .OrderBy(assignment => assignment.ChannelId)
            .ThenBy(assignment => assignment.OutletId)
            .Select(assignment => new PromotionAssignmentResponse(
                assignment.ChannelId, assignment.OutletId))
            .ToListAsync(ct);

    private static async Task<IResult?> ScopeProblem(
        SetPromotionScopeRequest request,
        IOutletClassification classification,
        IOutletCatalog outlets,
        CancellationToken ct)
    {
        var problems = new List<FieldProblem>();

        // One call per channel, because the contract offers a predicate rather than a list — Products
        // has no business enumerating a tenant's channels. A tenant has a handful, and this is an
        // authoring path rather than a read one.
        foreach (var channelId in request.ChannelIds.Distinct())
        {
            if (!await classification.ChannelExistsAsync(channelId, ct))
            {
                problems.Add(new FieldProblem(
                    "channelIds",
                    "That channel does not exist.",
                    "product.promotion.channelMissing",
                    new Dictionary<string, string> { ["channelId"] = channelId.ToString() }));
            }
        }

        var outletIds = request.OutletIds.Distinct().ToList();
        if (outletIds.Count > 0)
        {
            // Batch, because IOutletCatalog offers one — and outlets are the scope a tenant names
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
                    "product.promotion.outletMissing",
                    new Dictionary<string, string> { ["count"] = missing.ToString() }));
            }
        }

        return problems.Count > 0 ? Problems.BadRequest(problems) : null;
    }
}
