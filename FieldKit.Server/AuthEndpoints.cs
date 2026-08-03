using System.Security.Claims;

namespace FieldKit.Server;

/// <summary>
/// What the API made of the caller's token.
/// </summary>
/// <param name="Subject">Keycloak's <c>sub</c> — the stable user id.</param>
/// <param name="Tenant">The <c>tenant</c> claim: which tenant's realm issued this token.</param>
/// <param name="Permissions">The <c>permissions</c> claim — <c>resource:action</c> strings.</param>
public sealed record WhoAmIResponse(string? Subject, string? Tenant, IReadOnlyList<string> Permissions);

public static class AuthEndpoints
{
    /// <summary>
    /// Maps <c>GET /api/auth/whoami</c> — what the API made of the caller's token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists so authentication is *demonstrable* rather than merely configured: 401 without a
    /// bearer token, the claims echoed with one. The integration tests assert exactly that against a
    /// real Keycloak.
    /// </para>
    /// <para>
    /// It reads <see cref="ClaimsPrincipal"/> directly rather than <c>ITenantContext</c>, and that
    /// stays deliberate now the two agree: this endpoint reports what *the token* said, so it can
    /// still tell you something useful when the tenant context is the thing misbehaving. Everywhere
    /// else — anything touching tenant-owned data — goes through <c>ITenantContext</c>.
    /// </para>
    /// <para>
    /// It requires only a valid token, not a permission: every authenticated caller may ask who they
    /// are. Business endpoints require a <c>resource:action</c> permission instead.
    /// </para>
    /// </remarks>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/auth/whoami", (ClaimsPrincipal user) => new WhoAmIResponse(
                user.FindFirstValue("sub"),
                user.FindFirstValue("tenant"),
                [.. user.FindAll("permissions").Select(claim => claim.Value).Order()]))
            .RequireAuthorization()
            .WithTags("Auth")
            .WithSummary("Echoes the identity the API resolved from the bearer token.");

        return endpoints;
    }
}
