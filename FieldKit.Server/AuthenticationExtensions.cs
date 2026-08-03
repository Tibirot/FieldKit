using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace FieldKit.Server;

/// <summary>
/// JWT bearer validation against Keycloak, across every tenant realm (ADR-0008, <c>IAM-01</c>).
/// </summary>
public static class AuthenticationExtensions
{
    /// <summary>
    /// Validates the Keycloak-issued access token on every request that asks for one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Multi-issuer.</b> Realm-per-tenant means each tenant's tokens come from a different issuer
    /// and JWKS endpoint, so there is no single authority to point at. Issuer and signing keys are
    /// resolved per request from <see cref="TenantIssuerDirectory"/>, which is backed by the tenant
    /// registry — a realm nothing in the database claims is not a realm this API trusts.
    /// </para>
    /// <para>
    /// This replaced <c>AddKeycloakJwtBearer</c>, which configures exactly one realm. That was right
    /// while one realm existed and is unusable now: the second tenant would have been
    /// unauthenticable rather than merely inconvenient. Retiring it also removes the repo's second
    /// prerelease dependency.
    /// </para>
    /// </remarks>
    public static IHostApplicationBuilder AddKeycloakAuthentication(this IHostApplicationBuilder builder)
    {
        var audience = builder.Configuration["Keycloak:Audience"]
            ?? throw new InvalidOperationException("Keycloak:Audience is not configured.");
        var requireHttpsMetadata = !builder.Environment.IsDevelopment();

        builder.Services.AddSingleton<TenantIssuerDirectory>();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured through the options pipeline rather than the AddJwtBearer callback so the
        // directory can be injected: `IssuerValidator` and `IssuerSigningKeyResolver` are synchronous
        // delegates that receive no service provider, so the only way they can reach a singleton is
        // to close over it here.
        builder.Services
            .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<TenantIssuerDirectory>((options, directory) =>
            {
                // No Authority: it would pin one realm's metadata, which is the thing being replaced.
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = requireHttpsMetadata;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,

                    // Keycloak's default access-token audience is `account`; every realm adds an
                    // explicit `fieldkit-api` mapper so this check means something. Without it a
                    // token minted for any client in any trusted realm would be accepted.
                    ValidAudience = audience,

                    IssuerValidator = (issuer, _, _) => ValidateIssuer(directory, issuer),

                    // Resolving keys per issuer is what stops a token being checked against another
                    // realm's keys. Issuer validation alone would let a forged `iss` ride on a
                    // signature that is real — just from somewhere else.
                    IssuerSigningKeyResolver = (_, securityToken, _, _) =>
                        directory.SigningKeysFor(securityToken.Issuer),
                };

                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context => BindTokenToItsTenant(directory, context),
                };
            });

        builder.Services.AddAuthorization();

        return builder;
    }

    /// <summary>Accepts an issuer only if an active tenant owns that realm.</summary>
    private static string ValidateIssuer(TenantIssuerDirectory directory, string issuer)
    {
        // InvalidIssuer set explicitly: the bearer challenge reports it, and without it an operator
        // reads "The issuer '(null)' is invalid" for a token that plainly has one.
        var tenant = directory.Resolve(issuer)
            ?? throw new SecurityTokenInvalidIssuerException($"No tenant is registered for issuer '{issuer}'.")
            {
                InvalidIssuer = issuer,
            };

        // A suspended tenant keeps its data and loses its access. Rejecting here rather than deeper
        // in the request means nothing downstream has to remember to check.
        if (!tenant.IsActive)
        {
            throw new SecurityTokenInvalidIssuerException($"The tenant for issuer '{issuer}' is suspended.")
            {
                InvalidIssuer = issuer,
            };
        }

        return issuer;
    }

    /// <summary>
    /// Requires the token's <c>tenant</c> claim to match the tenant that owns the issuer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The check that makes multi-issuer safe.</b> Issuer validation proves a token came from a
    /// realm we trust; it says nothing about which tenant the token *claims* to be. Without this, a
    /// legitimately-signed token from tenant B carrying <c>tenant</c> = A's id would authenticate —
    /// and the tenant context reads that claim, so the query filter would hand B a complete view of
    /// A's data. Every isolation guarantee in the platform rests on this one comparison.
    /// </para>
    /// <para>
    /// It subsumes the earlier "is the claim parseable" check: a claim that does not match the
    /// issuer's tenant fails whether or not it parses.
    /// </para>
    /// </remarks>
    private static Task BindTokenToItsTenant(TenantIssuerDirectory directory, TokenValidatedContext context)
    {
        if (!Guid.TryParse(context.Principal?.FindFirst("tenant")?.Value, out var claimed))
        {
            context.Fail("The access token carries no usable 'tenant' claim.");
            return Task.CompletedTask;
        }

        var issuerTenant = directory.Resolve(context.SecurityToken.Issuer);

        if (issuerTenant is null || issuerTenant.TenantId.Value != claimed)
        {
            context.Fail("The token's 'tenant' claim does not match the tenant that owns its issuer.");
        }

        return Task.CompletedTask;
    }
}
