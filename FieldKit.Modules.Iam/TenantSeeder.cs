using FieldKit.Infrastructure;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FieldKit.Modules.Iam;

/// <summary>One tenant to ensure exists at startup, from configuration.</summary>
public sealed class SeedTenant
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Realm { get; init; } = "";
}

/// <summary>
/// Ensures the configured tenants exist, so their realms are trusted issuers.
/// </summary>
/// <remarks>
/// <para>
/// Multi-issuer validation resolves an issuer against the tenant table: a realm no row claims is a
/// realm the API rejects. That is the right default and it makes the platform unusable from a fresh
/// database — including the dev realm the AppHost imports — so something has to put the first row
/// there. Until tenant provisioning exists (<c>IAM-10</c>), that something is configuration.
/// </para>
/// <para>
/// The tenant <b>id must match the realm's hardcoded <c>tenant</c> claim</b>. They are two halves of
/// one fact: the realm asserts which tenant its tokens belong to, and this row is what makes the API
/// agree. A mismatch produces tokens that authenticate and are then rejected for claiming a tenant
/// their issuer does not own — which is the multi-issuer binding working, but for the wrong reason.
/// </para>
/// <para>
/// Idempotent by realm, and it never overwrites. Re-seeding a live tenant's name from a stale config
/// file would be a silent edit to production data on every deploy.
/// </para>
/// </remarks>
public sealed class TenantSeeder(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<TenantSeeder> logger) : IHostedService
{
    public const string ConfigurationSection = "Iam:SeedTenants";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seeds = configuration.GetSection(ConfigurationSection).Get<SeedTenant[]>() ?? [];
        if (seeds.Length == 0) return;

        using var scope = services.CreateScope();

        // A context built for the system, not the DI-registered one.
        //
        // The registered context — and, less obviously, the stamping interceptor inside its options —
        // both take the request's ITenantContext, which refuses to resolve outside a request. That
        // refusal is correct: a background service quietly inheriting "some tenant" is how
        // cross-tenant writes happen. Reusing the registered DbContextOptions is not enough, because
        // the interceptor captured the request-scoped context when the options were built; the audit
        // stamp on save is what trips it.
        //
        // Seeding genuinely runs as the system, so it says so, and the audit columns record `system`
        // rather than an invented user.
        var connectionString = configuration.GetConnectionString("fieldkitdb")
            ?? throw new InvalidOperationException("Connection string 'fieldkitdb' is not configured.");

        var options = new DbContextOptionsBuilder<IamDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", IamDbContext.SchemaName))
            .AddInterceptors(new EntityStampingInterceptor(
                scope.ServiceProvider.GetRequiredService<IClock>(), SystemTenantContext.Instance))
            .Options;

        await using var db = new IamDbContext(options, SystemTenantContext.Instance);

        foreach (var seed in seeds)
        {
            if (!Guid.TryParse(seed.Id, out var id) || string.IsNullOrWhiteSpace(seed.Realm))
            {
                // Loud, because a malformed seed means a realm silently stops being trusted.
                logger.LogError("Ignoring malformed seed tenant '{Realm}' (id '{Id}').", seed.Realm, seed.Id);
                continue;
            }

            if (await db.Tenants.AnyAsync(tenant => tenant.Realm == seed.Realm, cancellationToken)) continue;

            db.Tenants.Add(Tenant.Create(new TenantId(id), seed.Name, seed.Realm));
            logger.LogInformation("Seeded tenant '{Name}' on realm '{Realm}'.", seed.Name, seed.Realm);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// The identity startup work runs under. Deliberately holds no permissions and no tenant: it
    /// exists to satisfy the auditing interceptor, not to grant anything, and a
    /// <see cref="Tenant"/> is not tenant-owned so nothing here needs a tenant to resolve.
    /// </summary>
    private sealed class SystemTenantContext : FieldKit.BuildingBlocks.ITenantContext
    {
        public static readonly SystemTenantContext Instance = new();

        public TenantId TenantId => default;
        public string UserId => "system";
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public bool Has(string permission) => false;
    }
}
