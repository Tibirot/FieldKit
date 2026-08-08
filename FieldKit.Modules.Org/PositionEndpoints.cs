using FieldKit.Modules.Iam.Contracts;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Org;

/// <summary>Someone's place in the hierarchy. <paramref name="Title"/> is a label, not a capability.</summary>
public sealed record PositionResponse(
    Guid Id, string UserId, string? DisplayName, Guid OrgUnitId, string Title);

/// <summary>Attach a user to an org unit, or retitle an existing attachment.</summary>
public sealed record PositionRequest(string UserId, Guid OrgUnitId, string Title);

/// <summary>
/// What the hierarchy means for one person (<c>ORG-02</c>).
/// </summary>
/// <param name="Positions">Where they sit. More than one is ordinary — covering two areas.</param>
/// <param name="ManagementLine">
/// The units above them, nearest first. Who they report up through, for roll-up reporting.
/// </param>
/// <param name="VisibleUnitIds">
/// Their units and everything beneath — the visibility scope BR-ORG-4 describes. Returned as data,
/// not enforced here: this is what a screen shows, and enforcement lands with `ORG-09`.
/// </param>
public sealed record UserScopeResponse(
    IReadOnlyList<PositionResponse> Positions,
    IReadOnlyList<Guid> ManagementLine,
    IReadOnlyList<Guid> VisibleUnitIds);

/// <summary>
/// Positions and the management line derived from them (<c>ORG-02</c>).
/// </summary>
internal static class PositionEndpoints
{
    public static void MapPositionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var positions = endpoints.MapGroup("/api/org/positions").WithTags("Organization");

        positions.MapGet("/", async (
            Guid? orgUnitId, OrgDbContext db, IUserDirectory users, CancellationToken ct) =>
        {
            var rows = await db.Positions
                .Where(position => orgUnitId == null || position.OrgUnitId == orgUnitId)
                .OrderBy(position => position.Title)
                .ToListAsync(ct);

            return await WithDisplayNamesAsync(rows, users, ct);
        }).RequirePermission(OrgPermissions.PositionRead);

        positions.MapPost("/", async (
            PositionRequest request, OrgDbContext db, IUserDirectory users, CancellationToken ct) =>
        {
            if (await ValidateAsync(request, db, users, ct) is { } problem) return problem;

            var duplicate = await db.Positions.AnyAsync(
                position => position.UserId == request.UserId && position.OrgUnitId == request.OrgUnitId, ct);

            if (duplicate)
            {
                return Problems.Conflict("userId", "That user already holds a position in this unit.");
            }

            var created = Position.Create(request.UserId, request.OrgUnitId, request.Title);
            db.Positions.Add(created);
            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/org/positions/{created.Id}",
                new PositionResponse(created.Id, created.UserId, null, created.OrgUnitId, created.Title));
        }).RequirePermission(OrgPermissions.PositionWrite);

        // Retitle only. Moving a person to another unit is a different act — it changes who they
        // report through and what they can see — so it is a delete and a create, and the audit
        // columns show both rather than one row quietly meaning something new.
        positions.MapPut("/{id:guid}", async (
            Guid id, PositionRequest request, OrgDbContext db, IClock clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return Problems.BadRequest("title", "A position needs a title.");
            }

            // Repeated rather than shared with ValidateAsync: this path deliberately does not run it
            // (a retitle resolves no user and no unit), so the one field it does accept is checked here.
            if (TextLimits.TooLong("title", request.Title, 100, "position.title.tooLong") is { } tooLong)
            {
                return Problems.BadRequest([tooLong]);
            }

            var position = await db.Positions.SingleOrDefaultAsync(p => p.Id == id, ct);
            if (position is null) return Results.NotFound();

            if (position.OrgUnitId != request.OrgUnitId || position.UserId != request.UserId)
            {
                return Problems.BadRequest(
                    "orgUnitId", "A position cannot be moved. Remove it and create the new one.");
            }

            position.Retitle(request.Title, clock);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new PositionResponse(
                position.Id, position.UserId, null, position.OrgUnitId, position.Title));
        }).RequirePermission(OrgPermissions.PositionWrite);

        positions.MapDelete("/{id:guid}", async (Guid id, OrgDbContext db, CancellationToken ct) =>
        {
            var position = await db.Positions.SingleOrDefaultAsync(p => p.Id == id, ct);
            if (position is null) return Results.NotFound();

            db.Positions.Remove(position);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }).RequirePermission(OrgPermissions.PositionWrite);

        // The derivation ORG-02 actually asks for. Both directions in one response because they are
        // one question about one person — and because the tree is loaded once to answer either.
        endpoints.MapGet("/api/org/users/{userId}/scope", async (
            string userId, OrgDbContext db, IUserDirectory users, CancellationToken ct) =>
        {
            var held = await db.Positions.Where(position => position.UserId == userId).ToListAsync(ct);
            var parentOf = await db.OrgUnits.ToDictionaryAsync(unit => unit.Id, unit => unit.ParentId, ct);

            var roots = held.Select(position => position.OrgUnitId).Distinct().ToList();

            // Nearest-first, deduplicated across positions: two positions in the same branch share
            // ancestors, and reporting through the same unit twice is not a longer line.
            var line = roots
                .SelectMany(root => OrgHierarchy.AncestorsOf(root, parentOf))
                .Distinct()
                .ToList();

            return Results.Ok(new UserScopeResponse(
                await WithDisplayNamesAsync(held, users, ct),
                line,
                [.. OrgHierarchy.ScopeOf(roots, parentOf)]));
        }).WithTags("Organization").RequirePermission(OrgPermissions.PositionRead);
    }

    /// <summary>
    /// Rejects a position that names a unit or a user this tenant does not have.
    /// </summary>
    /// <remarks>
    /// The user check goes through <see cref="IUserDirectory"/> rather than IAM's tables — that
    /// contract is the whole reason schema-per-module survives contact with a feature like this
    /// (ADR-0005). It is also tenant-scoped by IAM's own query filter, so another tenant's user is
    /// simply unknown here.
    /// </remarks>
    private static async Task<IResult?> ValidateAsync(
        PositionRequest request, OrgDbContext db, IUserDirectory users, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
        {
            return Problems.BadRequest("userId", "A position needs a user.");
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return Problems.BadRequest("title", "A position needs a title.");
        }

        // The column. `userId` needs no such check: it is resolved against the user directory below,
        // so one too long for the column simply matches nobody and is refused by name.
        if (TextLimits.TooLong("title", request.Title, 100, "position.title.tooLong") is { } tooLong)
        {
            return Problems.BadRequest([tooLong]);
        }

        if (!await db.OrgUnits.AnyAsync(unit => unit.Id == request.OrgUnitId, ct))
        {
            return Problems.BadRequest("orgUnitId", "The org unit does not exist.");
        }

        // Deactivated users resolve — IUserDirectory returns them on purpose, because work they did
        // keeps its author. Attaching one to the org chart is still refused: a position is a
        // statement about the present.
        var user = await users.FindAsync(request.UserId, ct);

        return user switch
        {
            null => Problems.BadRequest("userId", "No such user in this tenant."),
            { IsActive: false } => Problems.BadRequest("userId", "That user is deactivated."),
            _ => null,
        };
    }

    /// <summary>
    /// Adds display names in one batch.
    /// </summary>
    /// <remarks>
    /// One call for the whole page rather than one per row — the N+1 that a per-position lookup would
    /// produce is exactly what <see cref="IUserDirectory.FindManyAsync"/> exists to prevent. A user
    /// who cannot be resolved keeps a null name rather than disappearing: the position is real and
    /// hiding it would make the org chart lie.
    /// </remarks>
    private static async Task<IReadOnlyList<PositionResponse>> WithDisplayNamesAsync(
        IReadOnlyList<Position> positions, IUserDirectory users, CancellationToken ct)
    {
        if (positions.Count == 0) return [];

        var names = (await users.FindManyAsync([.. positions.Select(p => p.UserId).Distinct()], ct))
            .ToDictionary(user => user.UserId, user => user.DisplayName);

        return
        [
            .. positions.Select(position => new PositionResponse(
                position.Id,
                position.UserId,
                names.GetValueOrDefault(position.UserId),
                position.OrgUnitId,
                position.Title)),
        ];
    }
}
