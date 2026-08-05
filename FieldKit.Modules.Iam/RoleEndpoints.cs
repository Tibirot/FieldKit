using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Iam;

/// <summary>A role as the back office sees it.</summary>
public sealed record RoleResponse(Guid Id, string Name, bool IsSystemTemplate, IReadOnlyList<string> Permissions);

/// <summary>Create or replace a role. Permissions are set wholesale, not patched.</summary>
public sealed record RoleRequest(string Name, IReadOnlyList<string> Permissions);

/// <summary>One entry in the permission catalogue, for an admin composing a role.</summary>
public sealed record PermissionResponse(string Name, string Description);

/// <summary>
/// Roles administration (<c>IAM-04</c>).
/// </summary>
internal static class RoleEndpoints
{
    public static void MapRoleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var roles = endpoints.MapGroup("/api/iam/roles").WithTags("IAM");

        // The catalogue an admin picks from. Read-only and derived from code — there is no endpoint
        // to add a permission, because a permission nothing enforces is not a permission.
        endpoints.MapGet("/api/iam/permissions", (IPermissionCatalog catalog) =>
                catalog.All.Select(p => new PermissionResponse(p.Name, p.Description)))
            .WithTags("IAM")
            .RequirePermission(IamPermissions.RoleRead);

        roles.MapGet("/", async (IamDbContext db, CancellationToken ct) =>
                await db.Roles
                    .OrderBy(role => role.Name)
                    .Select(role => new RoleResponse(role.Id, role.Name, role.IsSystemTemplate, role.Permissions))
                    .ToListAsync(ct))
            .RequirePermission(IamPermissions.RoleRead);

        roles.MapPost("/", async (
            RoleRequest request, IamDbContext db, IPermissionCatalog catalog, CancellationToken ct) =>
        {
            if (Validate(request, catalog) is { } problem) return problem;

            if (await db.Roles.AnyAsync(role => role.Name == request.Name, ct))
            {
                return Problems.Conflict("name", $"A role named '{request.Name}' already exists.");
            }

            var created = Role.Create(request.Name, request.Permissions);
            db.Roles.Add(created);
            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/iam/roles/{created.Id}",
                new RoleResponse(created.Id, created.Name, created.IsSystemTemplate, created.Permissions));
        }).RequirePermission(IamPermissions.RoleWrite);

        roles.MapPut("/{id:guid}", async (
            Guid id, RoleRequest request, IamDbContext db, IPermissionCatalog catalog,
            IClock clock, CancellationToken ct) =>
        {
            if (Validate(request, catalog) is { } problem) return problem;

            var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == id, ct);
            if (role is null) return Results.NotFound();

            if (await db.Roles.AnyAsync(r => r.Name == request.Name && r.Id != id, ct))
            {
                return Problems.Conflict("name", $"A role named '{request.Name}' already exists.");
            }

            role.Rename(request.Name, clock);
            role.SetPermissions(request.Permissions);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new RoleResponse(role.Id, role.Name, role.IsSystemTemplate, role.Permissions));
        }).RequirePermission(IamPermissions.RoleWrite);

        roles.MapDelete("/{id:guid}", async (Guid id, IamDbContext db, CancellationToken ct) =>
        {
            var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == id, ct);
            if (role is null) return Results.NotFound();

            // A system template is the way back to a working set of roles (IAM-06). An admin may
            // recompose one; deleting it would let a tenant strand itself with no template to
            // re-seed from.
            if (role.IsSystemTemplate)
            {
                return Problems.Conflict("A system role template cannot be deleted. Edit it instead.");
            }

            // BR-IAM-3: a user must hold at least one role, so deleting a role that is still assigned
            // would leave users in a state the domain forbids. Refusing is the honest answer —
            // silently reassigning would be inventing an admin decision.
            var assigned = await db.Users.CountAsync(user => user.Roles.Any(r => r.RoleId == id), ct);
            if (assigned > 0)
            {
                return Problems.Conflict(
                    $"{assigned} user(s) still hold this role. Reassign them before deleting it.");
            }

            db.Roles.Remove(role);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }).RequirePermission(IamPermissions.RoleWrite);
    }

    /// <summary>
    /// Rejects role definitions the system could not honour.
    /// </summary>
    /// <remarks>
    /// The permission check is the point of the catalogue: a role naming <c>prodcut:read</c> grants
    /// nothing, and without validation the failure surfaces months later as "the rep says the button
    /// does not work". Returning the unknown names lets the caller see the typo.
    /// </remarks>
    private static IResult? Validate(RoleRequest request, IPermissionCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Problems.BadRequest("name", "A role needs a name.");
        }

        var unknown = request.Permissions
            .Where(permission => !catalog.Contains(permission))
            .ToList();

        return unknown.Count == 0
            ? null
            : Problems.BadRequest(
                "permissions",
                $"Unknown permissions — no module enforces these: {string.Join(", ", unknown)}.");
    }
}
