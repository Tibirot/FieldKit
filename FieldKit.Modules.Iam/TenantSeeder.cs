using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using FieldKit.SharedKernel;
using FieldKit.Web;
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
        using var scope = services.CreateScope();

        // Before the early return below, deliberately: a template naming a permission nothing
        // enforces is broken everywhere, not only where tenants happen to be seeded from config.
        SystemRoleTemplates.Validate(scope.ServiceProvider.GetRequiredService<IPermissionCatalog>());

        var seeds = configuration.GetSection(ConfigurationSection).Get<SeedTenant[]>() ?? [];
        if (seeds.Length == 0) return;

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

        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        foreach (var seed in seeds)
        {
            if (!Guid.TryParse(seed.Id, out var id) || string.IsNullOrWhiteSpace(seed.Realm))
            {
                // Loud, because a malformed seed means a realm silently stops being trusted.
                logger.LogError("Ignoring malformed seed tenant '{Realm}' (id '{Id}').", seed.Realm, seed.Id);
                continue;
            }

            // One context per tenant, bound to that tenant.
            //
            // Roles are tenant-owned, so both halves of isolation apply to them: the interceptor
            // stamps the ambient tenant on insert, and the query filter scopes the "does this tenant
            // already have roles" read. A single context running as "the system with no tenant" would
            // stamp every role with an empty tenant id and then be unable to see them — the query
            // filter working exactly as designed, against work that genuinely belongs to a tenant.
            // The alternative is IgnoreQueryFilters, which is banned at compile time and rightly so.
            var identity = new SeedingIdentity(new TenantId(id));

            await using var db = new IamDbContext(
                BuildOptions(connectionString, clock, identity), identity);

            var existing = await db.Tenants
                .FirstOrDefaultAsync(tenant => tenant.Realm == seed.Realm, cancellationToken);

            if (existing is null)
            {
                db.Tenants.Add(Tenant.Create(new TenantId(id), seed.Name, seed.Realm));
                logger.LogInformation("Seeded tenant '{Name}' on realm '{Realm}'.", seed.Name, seed.Realm);
            }

            await SeedRoleTemplatesAsync(db, seed, cancellationToken);

            // Per tenant, so one malformed seed cannot roll back the tenants before it.
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Gives a tenant the system role templates, if it has no roles at all (<c>IAM-06</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// "No roles at all" rather than "is new", so a tenant seeded before templates existed — every
    /// developer's database, and any environment upgraded across this change — is repaired rather
    /// than left permanently unusable. A tenant with zero roles has nobody who can administer it and
    /// no way to create the first role from inside the product, which is always a bug.
    /// </para>
    /// <para>
    /// Deliberately <b>not</b> a reconcile. Templates are starting points an admin may rename or
    /// recompose (<c>IAM-04</c>); restoring individual missing ones on every start would either undo
    /// that or duplicate roles they renamed, since nothing links a renamed role back to its template.
    /// </para>
    /// </remarks>
    private async Task SeedRoleTemplatesAsync(
        IamDbContext db, SeedTenant seed, CancellationToken cancellationToken)
    {
        if (await db.Roles.AnyAsync(cancellationToken)) return;

        db.Roles.AddRange(SystemRoleTemplates.Materialize());

        logger.LogInformation(
            "Seeded {Count} system role template(s) for tenant on realm '{Realm}'.",
            SystemRoleTemplates.All.Count,
            seed.Realm);
    }

    private static DbContextOptions<IamDbContext> BuildOptions(
        string connectionString, IClock clock, ITenantContext identity) =>
        new DbContextOptionsBuilder<IamDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", IamDbContext.SchemaName))
            .AddInterceptors(new EntityStampingInterceptor(clock, identity))
            .Options;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// The identity startup work runs under: the system, acting within one tenant.
    /// </summary>
    /// <remarks>
    /// Holds <b>no permissions</b>, deliberately. It exists to satisfy the query filter and the
    /// auditing interceptor, not to grant anything — seeding writes entities directly and never goes
    /// through an endpoint, so a permission here could only ever be a way for startup code to do
    /// something an administrator could not. The audit columns record <c>system</c> rather than an
    /// invented user, which is what actually happened.
    /// </remarks>
    private sealed class SeedingIdentity(TenantId tenantId) : ITenantContext
    {
        public TenantId TenantId => tenantId;
        public string UserId => "system";
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public bool Has(string permission) => false;
    }
}
