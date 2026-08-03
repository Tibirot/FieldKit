using System.Globalization;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Iam;

/// <summary>A user as the back office sees them.</summary>
public sealed record UserResponse(
    Guid Id,
    string SubjectId,
    string Email,
    string DisplayName,
    string Locale,
    string TimeZone,
    bool IsActive,
    IReadOnlyList<Guid> RoleIds);

/// <summary>Create or update a user profile. Roles are set wholesale, not patched.</summary>
public sealed record UserRequest(
    string SubjectId,
    string Email,
    string DisplayName,
    string Locale,
    string TimeZone,
    IReadOnlyList<Guid> RoleIds);

/// <summary>
/// Users administration (<c>IAM-03</c>).
/// </summary>
/// <remarks>
/// <b>Profile only.</b> Creating a user here does not create the Keycloak account — spec F2 pairs the
/// two, but doing so means putting Keycloak admin credentials into the request path, which is a
/// blast radius that deserves its own decision. Realm and account provisioning is <c>IAM-10</c>
/// (Phase 2); until then an operator creates the Keycloak user and the profile is linked by
/// <c>SubjectId</c>.
/// </remarks>
internal static class UserEndpoints
{
    public static void MapUserEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var users = endpoints.MapGroup("/api/iam/users").WithTags("IAM");

        users.MapGet("/", async (IamDbContext db, CancellationToken ct) =>
                await Project(db.Users.OrderBy(user => user.DisplayName)).ToListAsync(ct))
            .RequirePermission(IamPermissions.UserRead);

        users.MapGet("/{id:guid}", async (Guid id, IamDbContext db, CancellationToken ct) =>
                await Project(db.Users.Where(user => user.Id == id)).SingleOrDefaultAsync(ct)
                    is { } user
                    ? Results.Ok(user)
                    : Results.NotFound())
            .RequirePermission(IamPermissions.UserRead);

        users.MapPost("/", async (
            UserRequest request, IamDbContext db, IClock clock, CancellationToken ct) =>
        {
            if (await ValidateAsync(request, db, null, ct) is { } problem) return problem;

            var user = User.Create(
                request.SubjectId, request.Email, request.DisplayName, request.Locale, request.TimeZone);
            user.SetRoles(request.RoleIds, clock);

            db.Users.Add(user);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/iam/users/{user.Id}", ToResponse(user));
        }).RequirePermission(IamPermissions.UserWrite);

        users.MapPut("/{id:guid}", async (
            Guid id, UserRequest request, IamDbContext db, IClock clock, CancellationToken ct) =>
        {
            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return Results.NotFound();

            if (await ValidateAsync(request, db, id, ct) is { } problem) return problem;

            user.UpdateProfile(request.Email, request.DisplayName, request.Locale, request.TimeZone, clock);
            user.SetRoles(request.RoleIds, clock);
            await db.SaveChangesAsync(ct);

            return Results.Ok(ToResponse(user));
        }).RequirePermission(IamPermissions.UserWrite);

        // Deactivation is a state change with a consequence elsewhere — it publishes UserDeactivated
        // so Sync releases the bound device (A8) — so it is its own verb rather than a PUT field
        // that could be flipped by an unrelated profile edit.
        users.MapPost("/{id:guid}/deactivate", async (
            Guid id, IamDbContext db, IClock clock, CancellationToken ct) =>
        {
            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return Results.NotFound();

            user.Deactivate(clock);
            await db.SaveChangesAsync(ct);

            return Results.Ok(ToResponse(user));
        }).RequirePermission(IamPermissions.UserWrite);

        users.MapPost("/{id:guid}/reactivate", async (
            Guid id, IamDbContext db, IClock clock, CancellationToken ct) =>
        {
            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return Results.NotFound();

            user.Reactivate(clock);
            await db.SaveChangesAsync(ct);

            return Results.Ok(ToResponse(user));
        }).RequirePermission(IamPermissions.UserWrite);
    }

    /// <summary>
    /// Rejects profiles the platform could not honour.
    /// </summary>
    /// <remarks>
    /// Locale and timezone are validated against the runtime rather than merely required. BR-IAM-5
    /// makes them mandatory because every amount and timestamp renders through them; a syntactically
    /// present but unknown zone is worse than a missing one, because it fails at render time in
    /// front of a rep rather than here in front of an admin who can fix it.
    /// </remarks>
    private static async Task<IResult?> ValidateAsync(
        UserRequest request, IamDbContext db, Guid? existingUserId, CancellationToken ct)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.SubjectId)) errors.Add("SubjectId is required.");
        if (string.IsNullOrWhiteSpace(request.Email)) errors.Add("Email is required.");
        if (string.IsNullOrWhiteSpace(request.DisplayName)) errors.Add("DisplayName is required.");

        if (!IsKnownLocale(request.Locale)) errors.Add($"'{request.Locale}' is not a known locale (BCP-47).");
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(request.TimeZone, out _))
        {
            errors.Add($"'{request.TimeZone}' is not a known IANA time zone.");
        }

        // BR-IAM-3 is enforced in the domain too; checking here turns a 500 into a 400 that says why.
        if (request.RoleIds.Count == 0) errors.Add("A user must hold at least one role (BR-IAM-3).");

        if (errors.Count > 0) return Results.BadRequest(new { errors });

        // Roles are tenant-scoped and so is this query — a role id from another tenant simply does
        // not resolve, which is the query filter doing the work rather than a hand-written check.
        var known = await db.Roles
            .Where(role => request.RoleIds.Contains(role.Id))
            .Select(role => role.Id)
            .ToListAsync(ct);

        var unknownRoles = request.RoleIds.Except(known).ToList();
        if (unknownRoles.Count > 0)
        {
            return Results.BadRequest(new { error = "Unknown roles for this tenant.", unknown = unknownRoles });
        }

        var subjectTaken = await db.Users.AnyAsync(
            user => user.SubjectId == request.SubjectId && user.Id != existingUserId, ct);
        if (subjectTaken) return Results.Conflict(new { error = "That subject already has a profile." });

        var emailTaken = await db.Users.AnyAsync(
            user => user.Email == request.Email && user.Id != existingUserId, ct);
        if (emailTaken) return Results.Conflict(new { error = $"'{request.Email}' is already in use." });

        return null;
    }

    private static bool IsKnownLocale(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return false;

        try
        {
            // `predefinedOnly: true` is the whole check. Without it, ICU happily manufactures a
            // culture for almost any well-formed-looking tag — "not-a-locale" included — so the
            // validation would pass and the user would render against an invariant culture at
            // runtime, which is the failure BR-IAM-5 exists to prevent.
            _ = CultureInfo.GetCultureInfo(locale, predefinedOnly: true);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private static IQueryable<UserResponse> Project(IQueryable<User> users) =>
        users.Select(user => new UserResponse(
            user.Id,
            user.SubjectId,
            user.Email,
            user.DisplayName,
            user.Locale,
            user.TimeZone,
            user.IsActive,
            user.Roles.Select(role => role.RoleId).ToList()));

    private static UserResponse ToResponse(User user) => new(
        user.Id,
        user.SubjectId,
        user.Email,
        user.DisplayName,
        user.Locale,
        user.TimeZone,
        user.IsActive,
        [.. user.Roles.Select(role => role.RoleId)]);
}
