using FieldKit.BuildingBlocks;
using FieldKit.Modules.Iam.Contracts;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Org;

/// <summary>A rep's coverage of a territory over a period.</summary>
/// <param name="IsCurrent">
/// Whether today falls inside the period — resolved in the <i>calling</i> user's timezone, so this
/// is a view of the assignment rather than a property of it.
/// </param>
public sealed record RepAssignmentResponse(
    Guid Id,
    Guid TerritoryId,
    string UserId,
    string? DisplayName,
    DateOnly From,
    DateOnly? To,
    bool IsCurrent);

/// <summary>Assign a rep, or change an existing assignment. <paramref name="To"/> null means open-ended.</summary>
public sealed record RepAssignmentRequest(string UserId, DateOnly From, DateOnly? To);

/// <summary>
/// Rep–territory assignments (<c>ORG-04</c>).
/// </summary>
internal static class RepAssignmentEndpoints
{
    public static void MapRepAssignmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/org").WithTags("Organization");

        group.MapGet("/territories/{territoryId:guid}/assignments", async (
            Guid territoryId, OrgDbContext db, IUserDirectory users, ITenantContext caller,
            IClock clock, CancellationToken ct) =>
        {
            if (!await db.Territories.AnyAsync(t => t.Id == territoryId, ct)) return Results.NotFound();

            var assignments = await db.RepAssignments
                .Where(assignment => assignment.TerritoryId == territoryId)
                .OrderByDescending(assignment => assignment.FromDate)
                .ToListAsync(ct);

            return Results.Ok(await ProjectAsync(assignments, users, caller, clock, ct));
        }).RequirePermission(OrgPermissions.TerritoryRead);

        // "What does this rep cover" — BR-ORG-3's offline scope, from the other direction. Sync will
        // want this shape; exposing it now keeps the question answerable before that module exists.
        group.MapGet("/users/{userId}/assignments", async (
            string userId, OrgDbContext db, IUserDirectory users, ITenantContext caller,
            IClock clock, CancellationToken ct) =>
        {
            var assignments = await db.RepAssignments
                .Where(assignment => assignment.UserId == userId)
                .OrderByDescending(assignment => assignment.FromDate)
                .ToListAsync(ct);

            return Results.Ok(await ProjectAsync(assignments, users, caller, clock, ct));
        }).RequirePermission(OrgPermissions.TerritoryRead);

        group.MapPost("/territories/{territoryId:guid}/assignments", async (
            Guid territoryId, RepAssignmentRequest request, OrgDbContext db, IUserDirectory users,
            ITenantContext caller, IClock clock, CancellationToken ct) =>
        {
            if (!await db.Territories.AnyAsync(t => t.Id == territoryId, ct)) return Results.NotFound();

            var (problem, period) = await ValidateAsync(request, territoryId, excluding: null, db, users, ct);
            if (problem is not null) return problem;

            var created = RepAssignment.Create(territoryId, request.UserId, period, clock);
            db.RepAssignments.Add(created);
            await db.SaveChangesAsync(ct);

            var response = await ProjectAsync([created], users, caller, clock, ct);

            return Results.Created($"/api/org/assignments/{created.Id}", response[0]);
        }).RequirePermission(OrgPermissions.TerritoryWrite);

        group.MapPut("/assignments/{id:guid}", async (
            Guid id, RepAssignmentRequest request, OrgDbContext db, IUserDirectory users,
            ITenantContext caller, IClock clock, CancellationToken ct) =>
        {
            var assignment = await db.RepAssignments.SingleOrDefaultAsync(a => a.Id == id, ct);
            if (assignment is null) return Results.NotFound();

            var (problem, period) =
                await ValidateAsync(request, assignment.TerritoryId, excluding: id, db, users, ct);

            if (problem is not null) return problem;

            assignment.Update(request.UserId, period, clock);
            await db.SaveChangesAsync(ct);

            var response = await ProjectAsync([assignment], users, caller, clock, ct);

            return Results.Ok(response[0]);
        }).RequirePermission(OrgPermissions.TerritoryWrite);

        group.MapDelete("/assignments/{id:guid}", async (
            Guid id, OrgDbContext db, IClock clock, CancellationToken ct) =>
        {
            var assignment = await db.RepAssignments.SingleOrDefaultAsync(a => a.Id == id, ct);
            if (assignment is null) return Results.NotFound();

            // Announced before removal, while the entity still knows who held it. A consumer needs
            // to shrink that rep's device, and after the row is gone nothing could tell it whose.
            assignment.AnnounceRemoval(clock);
            db.RepAssignments.Remove(assignment);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }).RequirePermission(OrgPermissions.TerritoryWrite);
    }

    /// <summary>
    /// Rejects a period that is not one, a rep this tenant does not have, or an overlap (BR-ORG-2).
    /// </summary>
    /// <remarks>
    /// The overlap check loads the territory's other assignments rather than expressing the rule in
    /// SQL: Postgres could do it with an exclusion constraint over a date range, but that means a
    /// range column EF does not model and a constraint violation instead of a message naming the
    /// assignment in the way. A territory has a handful of assignments, not thousands.
    /// </remarks>
    private static async Task<(IResult? Problem, DateRange Period)> ValidateAsync(
        RepAssignmentRequest request,
        Guid territoryId,
        Guid? excluding,
        OrgDbContext db,
        IUserDirectory users,
        CancellationToken ct)
    {
        if (!DateRange.TryCreate(request.From, request.To, out var period))
        {
            return (Problems.BadRequest("to", "An assignment cannot end before it starts."), default);
        }

        if (string.IsNullOrWhiteSpace(request.UserId))
        {
            return (Problems.BadRequest("userId", "An assignment needs a rep."), default);
        }

        // Through IAM's contract, never its tables. A deactivated user still resolves — their past
        // work keeps its author — but assigning one is refused: an assignment is a statement about
        // who will be covering the territory.
        var user = await users.FindAsync(request.UserId, ct);

        if (user is null)
        {
            return (Problems.BadRequest("userId", "No such user in this tenant."), default);
        }

        if (!user.IsActive)
        {
            return (Problems.BadRequest("userId", "That user is deactivated."), default);
        }

        var others = await db.RepAssignments
            .Where(assignment => assignment.TerritoryId == territoryId
                && (excluding == null || assignment.Id != excluding))
            .ToListAsync(ct);

        // BR-ORG-2. Overlap is checked in memory because the rule is interval arithmetic, and
        // DateRange is where that arithmetic is defined and tested — reimplementing it as a SQL
        // predicate would be a second copy free to disagree with the first.
        var clash = others.FirstOrDefault(assignment => assignment.Period.Overlaps(period));

        return clash is null
            ? (null, period)
            // Named for `from`, since moving the start is the usual way out of an overlap — and the
            // clashing period is in the message rather than a side-channel object, so a client that
            // only renders messages still tells someone what they collided with.
            : (Problems.Conflict(
                "from",
                $"Another rep is already assigned to this territory from {clash.FromDate:yyyy-MM-dd} "
                    + $"to {(clash.ToDate is { } to ? to.ToString("yyyy-MM-dd") : "further notice")}."),
                default);
    }

    /// <summary>
    /// Adds display names and resolves "is this current" in the calling user's timezone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Whose today.</b> An assignment's dates are business days, so "is it current" needs a
    /// timezone, and there is no single right one on the record itself: a territory spans outlets
    /// that may sit in different zones, so there is no such thing as "territory time". The caller's
    /// own zone is the answer that makes a back-office screen agree with the person reading it.
    /// </para>
    /// <para>
    /// It falls back to UTC when the caller has no FieldKit profile — which is every user until
    /// account provisioning (<c>IAM-10</c>) links Keycloak accounts to profiles, so today that
    /// fallback is the common path rather than the edge case.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<RepAssignmentResponse>> ProjectAsync(
        IReadOnlyList<RepAssignment> assignments,
        IUserDirectory users,
        ITenantContext caller,
        IClock clock,
        CancellationToken ct)
    {
        if (assignments.Count == 0) return [];

        var ids = assignments.Select(assignment => assignment.UserId).Append(caller.UserId).Distinct().ToList();
        var known = (await users.FindManyAsync(ids, ct)).ToDictionary(user => user.UserId);

        var zone = known.TryGetValue(caller.UserId, out var me)
            && TimeZoneInfo.TryFindSystemTimeZoneById(me.TimeZone, out var found)
                ? found
                : TimeZoneInfo.Utc;

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, zone).DateTime);

        return
        [
            .. assignments.Select(assignment => new RepAssignmentResponse(
                assignment.Id,
                assignment.TerritoryId,
                assignment.UserId,
                known.TryGetValue(assignment.UserId, out var user) ? user.DisplayName : null,
                assignment.FromDate,
                assignment.ToDate,
                assignment.Period.Contains(today))),
        ];
    }
}
