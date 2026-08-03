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

                options.Events = new JwtBearerEvents { OnTokenValidated = RequireUsableTenantClaim };
            });

        builder.Services.AddAuthorization();

        return builder;
    }

    /// <summary>
    /// Rejects an otherwise-valid token that carries no usable <c>tenant</c> claim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enforced here rather than where the claim is read, so the guarantee holds for *every* endpoint
    /// instead of only the ones that remember to check. By the time any handler runs, an
    /// authenticated request is known to have a tenant.
    /// </para>
    /// <para>
    /// Failing is the only safe option. A token that authenticates but cannot be attributed to a
    /// tenant is more dangerous than an anonymous one: the request would reach the data layer, where
    /// the global query filter compares against *some* tenant id — so the failure mode is not "access
    /// denied" but "reads and writes attributed to the wrong tenant".
    /// </para>
    /// <para>
    /// <see cref="Guid.TryParse(string, out Guid)"/> rather than <c>TenantId.Parse</c> because this
    /// runs on attacker-supplied input; the parse must not throw. <c>TenantId</c> wraps a
    /// <see cref="Guid"/>, so this is the same acceptance test without the exception.
    /// </para>
    /// </remarks>
    private static Task RequireUsableTenantClaim(TokenValidatedContext context)
    {
        var tenant = context.Principal?.FindFirst("tenant")?.Value;

        if (!Guid.TryParse(tenant, out _))
        {
            context.Fail("The access token carries no usable 'tenant' claim.");
        }

        return Task.CompletedTask;
    }
}
