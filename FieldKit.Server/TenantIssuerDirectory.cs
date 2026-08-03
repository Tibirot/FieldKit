using System.Collections.Concurrent;
using FieldKit.Modules.Iam.Contracts;
using FieldKit.SharedKernel;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace FieldKit.Server;

/// <summary>A tenant resolved from the issuer that minted a token.</summary>
public sealed record IssuerTenant(TenantId TenantId, string Realm, bool IsActive);

/// <summary>
/// Resolves which tenant an issuer belongs to, and that realm's signing keys (ADR-0008,
/// multi-issuer validation).
/// </summary>
/// <remarks>
/// <para>
/// Realm-per-tenant means every tenant's tokens arrive from a <b>different issuer with a different
/// JWKS endpoint</b>. A single fixed authority can only ever validate one tenant, so this is not an
/// optimisation — without it the second tenant is simply unauthenticable.
/// </para>
/// <para>
/// Singleton, because token validation happens on every request and the resolution hooks
/// (<c>IssuerValidator</c>, <c>IssuerSigningKeyResolver</c>) are synchronous delegates with no
/// access to request services. The tenant list is reloaded on a TTL through a scope; signing keys
/// are held by one <see cref="ConfigurationManager{T}"/> per realm, which handles JWKS rotation and
/// caching itself.
/// </para>
/// </remarks>
public sealed class TenantIssuerDirectory(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    IHostEnvironment environment,
    IClock clock,
    ILogger<TenantIssuerDirectory> logger)
{
    /// <summary>
    /// How long a tenant list is trusted before reloading. Tenants change on provisioning, which is
    /// rare; a suspended tenant's tokens therefore keep validating for at most this long. That
    /// window is deliberate and matches BR-IAM-4's reasoning for user deactivation — bounded staleness
    /// beats putting a database read in front of every request in the platform.
    /// </summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _metadata = new();
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    private IReadOnlyDictionary<string, IssuerTenant> _byIssuer = new Dictionary<string, IssuerTenant>();
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    /// <summary>The Keycloak base address, supplied by Aspire's service discovery for the resource.</summary>
    private string BaseAddress =>
        configuration["services:keycloak:https:0"]
        ?? configuration["services:keycloak:http:0"]
        ?? throw new InvalidOperationException(
            "No Keycloak address in configuration. The AppHost's WithReference(keycloak) supplies "
            + "'services:keycloak:{https|http}:0'.");

    /// <summary>The issuer a realm's tokens carry, which is also where its metadata lives.</summary>
    public string IssuerFor(string realm) => $"{BaseAddress.TrimEnd('/')}/realms/{realm}";

    /// <summary>
    /// Resolves the tenant behind an issuer, or <c>null</c> if no tenant owns it.
    /// </summary>
    /// <remarks>
    /// A suspended tenant still resolves — the caller decides. Returning null for it would make
    /// "suspended" indistinguishable from "forged issuer" in the logs, and those want different
    /// responses from an operator.
    /// </remarks>
    public IssuerTenant? Resolve(string? issuer)
    {
        if (string.IsNullOrEmpty(issuer)) return null;

        EnsureLoaded();
        return _byIssuer.TryGetValue(issuer, out var tenant) ? tenant : null;
    }

    /// <summary>
    /// The signing keys for an issuer's realm, for <c>IssuerSigningKeyResolver</c>.
    /// </summary>
    /// <remarks>
    /// Blocking on an async fetch, deliberately: the resolver delegate is synchronous and there is no
    /// async equivalent. It is a cache hit in the ordinary case — <see cref="ConfigurationManager{T}"/>
    /// refreshes metadata on its own schedule — and ASP.NET Core has no synchronisation context, so
    /// this cannot deadlock. Returning nothing on failure yields a 401 rather than a 500, which is
    /// the right answer for a token whose keys we cannot obtain.
    /// </remarks>
    public IEnumerable<SecurityKey> SigningKeysFor(string? issuer)
    {
        var tenant = Resolve(issuer);
        if (tenant is null) return [];

        try
        {
            var manager = _metadata.GetOrAdd(tenant.Realm, CreateMetadataManager);
            return manager.GetConfigurationAsync(CancellationToken.None).GetAwaiter().GetResult().SigningKeys;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not fetch signing keys for realm {Realm}.", tenant.Realm);
            return [];
        }
    }

    private ConfigurationManager<OpenIdConnectConfiguration> CreateMetadataManager(string realm) =>
        new($"{IssuerFor(realm)}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever
            {
                // Same rule as the JWT handler's: the metadata document carries the signing keys, so
                // fetching it over a spoofable channel defeats signature validation entirely.
                RequireHttps = !environment.IsDevelopment(),
            });

    private void EnsureLoaded()
    {
        if (clock.UtcNow - _loadedAt < CacheLifetime) return;

        if (!_reloadLock.Wait(0)) return; // another request is already reloading; serve what we have

        try
        {
            if (clock.UtcNow - _loadedAt < CacheLifetime) return;

            using var scope = scopeFactory.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<ITenantRegistry>();
            var tenants = registry.GetAllAsync().GetAwaiter().GetResult();

            _byIssuer = tenants.ToDictionary(
                tenant => IssuerFor(tenant.Realm),
                tenant => new IssuerTenant(tenant.TenantId, tenant.Realm, tenant.IsActive),
                StringComparer.Ordinal);

            _loadedAt = clock.UtcNow;

            // The issuers themselves, not just the count: every rejected token is rejected by a
            // string comparison against this list, and "which issuers do we actually trust" is the
            // first question when one is refused.
            logger.LogInformation(
                "Loaded {Count} tenant issuer(s): {Issuers}.", _byIssuer.Count, string.Join(", ", _byIssuer.Keys));
        }
        catch (Exception ex)
        {
            // Keep serving the previous list rather than failing every request. An empty list would
            // reject every token in the platform because the database was briefly unreachable.
            logger.LogError(ex, "Could not reload the tenant issuer list; continuing with the cached one.");
        }
        finally
        {
            _reloadLock.Release();
        }
    }
}
