using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Org;

/// <summary>An org unit, flat. The tree is expressed by <paramref name="ParentId"/>, not by nesting.</summary>
/// <remarks>
/// Returned flat on purpose: a nested payload forces every consumer to walk it to find one node, and
/// re-serializes an entire branch to move a leaf. The client builds whatever shape it renders.
/// </remarks>
public sealed record OrgUnitResponse(Guid Id, string Name, Guid? ParentId);

/// <summary>Create or update an org unit. <paramref name="ParentId"/> null means a root.</summary>
public sealed record OrgUnitRequest(string Name, Guid? ParentId);

/// <summary>
/// The sales hierarchy (<c>ORG-01</c>) — configurable depth, tenant-chosen labels.
/// </summary>
internal static class OrgUnitEndpoints
{
    public static void MapOrgUnitEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var units = endpoints.MapGroup("/api/org/units").WithTags("Organization");

        // Every unit for the tenant, in one call. An org tree is tens of nodes — paging it, or
        // exposing a "children of" endpoint the client would call once per level, would turn one
        // query into a waterfall to save nothing.
        units.MapGet("/", async (OrgDbContext db, CancellationToken ct) =>
                await db.OrgUnits
                    .OrderBy(unit => unit.Name)
                    .Select(unit => new OrgUnitResponse(unit.Id, unit.Name, unit.ParentId))
                    .ToListAsync(ct))
            .RequirePermission(OrgPermissions.OrgUnitRead);

        units.MapPost("/", async (OrgUnitRequest request, OrgDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "An org unit needs a name." });
            }

            if (await ParentProblem(db, request.ParentId, ct) is { } parentProblem) return parentProblem;
            if (await NameTakenProblem(db, request, excluding: null, ct) is { } nameProblem) return nameProblem;

            var created = OrgUnit.Create(request.Name, request.ParentId);
            db.OrgUnits.Add(created);
            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/org/units/{created.Id}",
                new OrgUnitResponse(created.Id, created.Name, created.ParentId));
        }).RequirePermission(OrgPermissions.OrgUnitWrite);

        // Rename and reparent in one call, because they are one edit on one screen. Splitting them
        // would make "rename this team and move it under the new region" two requests that can
        // half-succeed.
        units.MapPut("/{id:guid}", async (
            Guid id, OrgUnitRequest request, OrgDbContext db, IClock clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "An org unit needs a name." });
            }

            var unit = await db.OrgUnits.SingleOrDefaultAsync(u => u.Id == id, ct);
            if (unit is null) return Results.NotFound();

            if (request.ParentId == id)
            {
                return Results.BadRequest(new { error = "An org unit cannot be its own parent." });
            }

            if (await ParentProblem(db, request.ParentId, ct) is { } parentProblem) return parentProblem;
            if (await NameTakenProblem(db, request, excluding: id, ct) is { } nameProblem) return nameProblem;

            if (unit.ParentId != request.ParentId && await CycleProblem(db, id, request.ParentId, ct) is { } cycle)
            {
                return cycle;
            }

            unit.Rename(request.Name, clock);
            unit.MoveTo(request.ParentId, clock);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new OrgUnitResponse(unit.Id, unit.Name, unit.ParentId));
        }).RequirePermission(OrgPermissions.OrgUnitWrite);

        units.MapDelete("/{id:guid}", async (Guid id, OrgDbContext db, CancellationToken ct) =>
        {
            var unit = await db.OrgUnits.SingleOrDefaultAsync(u => u.Id == id, ct);
            if (unit is null) return Results.NotFound();

            // Refused rather than cascaded. Deleting a region should not silently take its areas and
            // teams with it — and once positions and territories hang off these units, a cascade
            // would take those too. The admin says what happens to the children.
            var children = await db.OrgUnits.CountAsync(u => u.ParentId == id, ct);
            if (children > 0)
            {
                return Results.Conflict(new
                {
                    error = $"'{unit.Name}' still has {children} child unit(s). Move or delete them first.",
                });
            }

            // Same reasoning one level down: deleting the unit someone occupies would silently
            // remove them from the org chart. The foreign key would refuse this anyway — this is
            // what turns that into an answer an admin can act on.
            var staffed = await db.Positions.CountAsync(position => position.OrgUnitId == id, ct);
            if (staffed > 0)
            {
                return Results.Conflict(new
                {
                    error = $"{staffed} person(s) still hold positions in '{unit.Name}'. Move them first.",
                });
            }

            db.OrgUnits.Remove(unit);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }).RequirePermission(OrgPermissions.OrgUnitWrite);
    }

    /// <summary>
    /// Rejects a parent that does not exist <i>for this tenant</i>.
    /// </summary>
    /// <remarks>
    /// The query filter is doing the tenant half: another tenant's unit id simply does not resolve,
    /// so this returns the same "unknown parent" as a typo would. That is the right answer — telling
    /// the caller the id exists elsewhere would confirm it exists.
    /// </remarks>
    private static async Task<IResult?> ParentProblem(OrgDbContext db, Guid? parentId, CancellationToken ct)
    {
        if (parentId is not { } id) return null;

        return await db.OrgUnits.AnyAsync(unit => unit.Id == id, ct)
            ? null
            : Results.BadRequest(new { error = "The parent org unit does not exist." });
    }

    /// <summary>Rejects a name already taken by a sibling — see <see cref="OrgDbContext"/> for why siblings.</summary>
    private static async Task<IResult?> NameTakenProblem(
        OrgDbContext db, OrgUnitRequest request, Guid? excluding, CancellationToken ct)
    {
        var taken = await db.OrgUnits.AnyAsync(
            unit => unit.ParentId == request.ParentId
                && unit.Name == request.Name
                && (excluding == null || unit.Id != excluding),
            ct);

        return taken
            ? Results.Conflict(new { error = $"A sibling unit is already named '{request.Name}'." })
            : null;
    }

    /// <summary>Rejects a move that would put a unit inside its own subtree.</summary>
    /// <remarks>
    /// Loads the tenant's parent pointers rather than walking the tree one query at a time: the set
    /// is small, and the alternative is a round trip per level of depth to answer one question.
    /// </remarks>
    private static async Task<IResult?> CycleProblem(
        OrgDbContext db, Guid id, Guid? newParentId, CancellationToken ct)
    {
        var parentOf = await db.OrgUnits.ToDictionaryAsync(unit => unit.Id, unit => unit.ParentId, ct);

        return OrgHierarchy.WouldCreateCycle(id, newParentId, parentOf)
            ? Results.Conflict(new { error = "That move would put the unit inside its own branch." })
            : null;
    }
}
