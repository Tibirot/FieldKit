using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace FieldKit.Server;

/// <summary>
/// JWT bearer validation against Keycloak (ADR-0008, <c>IAM-01</c>).
/// </summary>
public static class AuthenticationExtensions
{
    /// <summary>
    /// Validates the Keycloak-issued access token on every request that asks for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The authority is not configured here: <c>AddKeycloakJwtBearer</c> resolves it from the
    /// <c>keycloak</c> resource the AppHost references, so the URL exists in exactly one place —
    /// the composition root — rather than being duplicated into settings per environment.
    /// </para>
    /// <para>
    /// <b>Single issuer, deliberately.</b> ADR-0008 chose realm-per-tenant, which means each tenant's
    /// tokens arrive from a *different* issuer and JWKS endpoint, so the finished system resolves the
    /// issuer per request against a registry of tenant realms. That registry needs a source of
    /// tenants, and IAM — which owns it — has not landed. Building it now would mean inventing a
    /// tenant list to drive it. Until then this validates the one dev realm, which is the only realm
    /// that exists.
    /// </para>
    /// </remarks>
    public static IHostApplicationBuilder AddKeycloakAuthentication(this IHostApplicationBuilder builder)
    {
        var realm = builder.Configuration["Keycloak:Realm"]
            ?? throw new InvalidOperationException("Keycloak:Realm is not configured.");
        var audience = builder.Configuration["Keycloak:Audience"]
            ?? throw new InvalidOperationException("Keycloak:Audience is not configured.");

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddKeycloakJwtBearer("keycloak", realm, options =>
            {
                // Keycloak's default access-token audience is `account`; the realm adds an explicit
                // `fieldkit-api` audience mapper so this check is meaningful. Without validating it,
                // a token minted for any client in the realm would be accepted here.
                options.Audience = audience;

                // Keep the token's own claim names. The default mapping rewrites `sub` to the long
                // WS-Federation URI, which would leave module code matching on a name the token
                // never contained — and `tenant`/`permissions` are custom claims that must survive
                // verbatim for the tenant context to read them.
                options.MapInboundClaims = false;

                // The dev container serves its discovery document over plain HTTP inside the Aspire
                // network. Anywhere else, refusing non-HTTPS metadata is not negotiable — a
                // spoofable JWKS endpoint defeats signature validation entirely.
                options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            });

        builder.Services.AddAuthorization();

        return builder;
    }
}
