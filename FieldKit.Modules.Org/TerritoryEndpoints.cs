using FieldKit.Modules.Outlets.Contracts;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Org;

/// <summary>A territory and how many outlets it holds.</summary>
public sealed record TerritoryResponse(Guid Id, string Name, Guid OrgUnitId, int OutletCount);

/// <summary>Create or update a territory.</summary>
public sealed record TerritoryRequest(string Name, Guid OrgUnitId);

/// <summary>An outlet in a territory, named through the Outlets contract.</summary>
/// <param name="Code">Null when the outlet no longer resolves — see the endpoint for why it stays.</param>
public sealed record TerritoryOutletResponse(Guid OutletId, string? Code, string? Name, bool? IsOpen);

/// <summary>Add outlets to a territory. Ids the tenant does not have are rejected as a set.</summary>
public sealed record AssignOutletsRequest(IReadOnlyList<Guid> OutletIds);

/// <summary>
/// Territories and the outlets in them (<c>ORG-03</c>, <c>ORG-05</c>).
/// </summary>
internal static class TerritoryEndpoints
{
    public static void MapTerritoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var territories = endpoints.MapGroup("/api/org/territories").WithTags("Organization");

        territories.MapGet("/", async (Guid? orgUnitId, OrgDbContext db, CancellationToken ct) =>
                await db.Territories
                    .Where(territory => orgUnitId == null || territory.OrgUnitId == orgUnitId)
                    .OrderBy(territory => territory.Name)
                    .Select(territory => new TerritoryResponse(
                        territory.Id,
                        territory.Name,
                        territory.OrgUnitId,
                        db.TerritoryOutlets.Count(m => m.TerritoryId == territory.Id)))
                    .ToListAsync(ct))
            .RequirePermission(OrgPermissions.TerritoryRead);

        territories.MapPost("/", async (TerritoryRequest request, OrgDbContext db, CancellationToken ct) =>
        {
            if (await ValidateAsync(request, db, excluding: null, ct) is { } problem) return problem;

            var created = Territory.Create(request.Name, request.OrgUnitId);
            db.Territories.Add(created);
            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/org/territories/{created.Id}",
                new TerritoryResponse(created.Id, created.Name, created.OrgUnitId, 0));
        }).RequirePermission(OrgPermissions.TerritoryWrite);

        territories.MapPut("/{id:guid}", async (
            Guid id, TerritoryRequest request, OrgDbContext db, IClock clock, CancellationToken ct) =>
        {
            var territory = await db.Territories.SingleOrDefaultAsync(t => t.Id == id, ct);
            if (territory is null) return Results.NotFound();

            if (await ValidateAsync(request, db, id, ct) is { } problem) return problem;

            territory.Update(request.Name, request.OrgUnitId, clock);
            await db.SaveChangesAsync(ct);

            var count = await db.TerritoryOutlets.CountAsync(m => m.TerritoryId == id, ct);

            return Results.Ok(new TerritoryResponse(territory.Id, territory.Name, territory.OrgUnitId, count));
        }).RequirePermission(OrgPermissions.TerritoryWrite);

        territories.MapDelete("/{id:guid}", async (Guid id, OrgDbContext db, CancellationToken ct) =>
        {
            var territory = await db.Territories.SingleOrDefaultAsync(t => t.Id == id, ct);
            if (territory is null) return Results.NotFound();

            // Refused rather than cascaded. Every outlet in a territory would silently become
            // unassigned — and since a territory's membership is a rep's offline scope (BR-ORG-3),
            // that is a set of outlets vanishing from somebody's device tomorrow morning.
            var outlets = await db.TerritoryOutlets.CountAsync(m => m.TerritoryId == id, ct);
            if (outlets > 0)
            {
                return Results.Conflict(new
                {
                    error = $"'{territory.Name}' still holds {outlets} outlet(s). Move them first.",
                });
            }

            db.Territories.Remove(territory);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }).RequirePermission(OrgPermissions.TerritoryWrite);

        territories.MapGet("/{id:guid}/outlets", async (
            Guid id, OrgDbContext db, IOutletCatalog outlets, CancellationToken ct) =>
        {
            if (!await db.Territories.AnyAsync(t => t.Id == id, ct)) return Results.NotFound();

            var memberships = await db.TerritoryOutlets
                .Where(m => m.TerritoryId == id)
                .Select(m => m.OutletId)
                .ToListAsync(ct);

            // One batched call across the module boundary rather than one per row — the round trips
            // an N+1 would produce here are invisible from inside Organization.
            var named = (await outlets.FindManyAsync(memberships, ct))
                .ToDictionary(outlet => outlet.OutletId);

            // An outlet that no longer resolves keeps its row with nulls rather than disappearing.
            // The membership is real; hiding it would make a territory quietly smaller than the
            // count on the list screen says, and nobody would know which outlet went missing.
            return Results.Ok(memberships
                .Select(outletId => named.TryGetValue(outletId, out var outlet)
                    ? new TerritoryOutletResponse(outletId, outlet.Code, outlet.Name, outlet.IsOpen)
                    : new TerritoryOutletResponse(outletId, null, null, null))
                .OrderBy(outlet => outlet.Code ?? string.Empty, StringComparer.Ordinal)
                .ToList());
        }).RequirePermission(OrgPermissions.TerritoryRead);

        territories.MapPost("/{id:guid}/outlets", async (
            Guid id, AssignOutletsRequest request, OrgDbContext db, IOutletCatalog outlets,
            CancellationToken ct) =>
        {
            if (!await db.Territories.AnyAsync(t => t.Id == id, ct)) return Results.NotFound();

            var requested = request.OutletIds.Distinct().ToList();
            if (requested.Count == 0) return Results.BadRequest(new { error = "No outlets given." });

            // Validated through the contract, not by reading the outlets schema. Another tenant's
            // outlet id simply does not resolve, which is the query filter inside Outlets doing the
            // work rather than a hand-written check here.
            var known = (await outlets.FindManyAsync(requested, ct)).Select(o => o.OutletId).ToHashSet();
            var unknown = requested.Where(outletId => !known.Contains(outletId)).ToList();

            if (unknown.Count > 0)
            {
                return Results.BadRequest(new { error = "Unknown outlets in this tenant.", unknown });
            }

            // ORG-05: an outlet belongs to exactly one territory. Reassignment is refused rather
            // than performed silently, because it changes which rep serves the outlet and what their
            // device downloads. Remove it from the old territory first — the same two-step the org
            // module already requires for moving a position, for the same reason: the audit trail
            // should show both halves.
            var taken = await db.TerritoryOutlets
                .Where(m => requested.Contains(m.OutletId) && m.TerritoryId != id)
                .Select(m => m.OutletId)
                .ToListAsync(ct);

            if (taken.Count > 0)
            {
                return Results.Conflict(new
                {
                    error = "Some outlets already belong to another territory. Remove them from it first.",
                    outlets = taken,
                });
            }

            var already = await db.TerritoryOutlets
                .Where(m => m.TerritoryId == id && requested.Contains(m.OutletId))
                .Select(m => m.OutletId)
                .ToListAsync(ct);

            // Idempotent: re-sending an outlet already in this territory is not an error, so a retry
            // after a dropped response does the right thing.
            foreach (var outletId in requested.Except(already))
            {
                db.TerritoryOutlets.Add(TerritoryOutlet.Create(id, outletId));
            }

            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }).RequirePermission(OrgPermissions.TerritoryWrite);

        territories.MapDelete("/{id:guid}/outlets/{outletId:guid}", async (
            Guid id, Guid outletId, OrgDbContext db, CancellationToken ct) =>
        {
            var membership = await db.TerritoryOutlets
                .SingleOrDefaultAsync(m => m.TerritoryId == id && m.OutletId == outletId, ct);

            if (membership is null) return Results.NotFound();

            db.TerritoryOutlets.Remove(membership);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }).RequirePermission(OrgPermissions.TerritoryWrite);
    }

    /// <summary>Rejects a nameless territory, a duplicate name, or an org unit this tenant lacks.</summary>
    private static async Task<IResult?> ValidateAsync(
        TerritoryRequest request, OrgDbContext db, Guid? excluding, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { error = "A territory needs a name." });
        }

        if (!await db.OrgUnits.AnyAsync(unit => unit.Id == request.OrgUnitId, ct))
        {
            return Results.BadRequest(new { error = "The org unit does not exist." });
        }

        var taken = await db.Territories.AnyAsync(
            territory => territory.Name == request.Name && (excluding == null || territory.Id != excluding), ct);

        return taken
            ? Results.Conflict(new { error = $"A territory named '{request.Name}' already exists." })
            : null;
    }
}
