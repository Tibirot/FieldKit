using Microsoft.AspNetCore.Builder;

namespace FieldKit.Web;

/// <summary>
/// Permission-based authorization for module endpoints (<c>IAM-05</c>, <c>BR-IAM-2</c>).
/// </summary>
public static class PermissionExtensions
{
    /// <summary>
    /// The token claim carrying the caller's permissions. Keycloak realm roles are flattened into it
    /// by a protocol mapper, so the API never sees role names — see the AppHost's realm.
    /// </summary>
    public const string PermissionsClaim = "permissions";

    /// <summary>
    /// Requires the caller to hold <paramref name="permission"/> (a <c>resource:action</c> string).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Modules check <b>permissions, never role names</b> (BR-IAM-2). A role is a bundle an admin
    /// can redefine per tenant; wiring code to one would make that customization a breaking change.
    /// </para>
    /// <para>
    /// The two failure modes stay distinct on purpose: an anonymous caller gets <b>401</b> (the
    /// authenticated-user requirement), an authenticated caller lacking the permission gets
    /// <b>403</b>. Collapsing them would tell a rep with the wrong role to log in again, which they
    /// already have.
    /// </para>
    /// </remarks>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
        => builder.RequireAuthorization(policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(PermissionsClaim, permission));
}
