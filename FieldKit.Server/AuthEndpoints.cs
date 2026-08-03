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
    /// Maps <c>GET /api/auth/whoami</c> — the one endpoint that currently requires a valid token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists so authentication is *demonstrable* rather than merely configured: it returns 401
    /// without a bearer token and echoes the claims with one, which is what the integration tests
    /// assert against a real Keycloak.
    /// </para>
    /// <para>
    /// It reads <see cref="ClaimsPrincipal"/> directly and deliberately **not**
    /// <c>ITenantContext</c> — that still resolves to the temporary <c>DevTenantContext</c>, and
    /// wiring the token into it is the next slice. Reading claims here keeps the two changes
    /// separable: this proves the token is *validated*, the next proves it is *authoritative*.
    /// </para>
    /// <para>
    /// <c>/api/products</c> stays anonymous for the same reason. Protecting it before the tenant
    /// context is token-derived would mean requests carrying a real tenant claim still writing rows
    /// stamped with the dev tenant — authenticated and wrong, which is worse than open.
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
