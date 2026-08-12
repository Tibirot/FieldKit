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

    /// <summary>
    /// People this tenant should already know about, by the subject their realm issues.
    /// </summary>
    /// <remarks>
    /// <b>A convenience for development, and it only works because the realm files pin user ids.</b>
    /// Keycloak generates a subject on import, and the dev Keycloak has no data volume by design —
    /// so before those ids were pinned, every restart produced a different subject and left the
    /// previous run's user rows orphaned. Seeding by subject would have been impossible and the
    /// database accumulated debris instead.
    /// </remarks>
    public SeedUser[] Users { get; init; } = [];
}

/// <summary>One person to ensure the tenant knows about, from configuration.</summary>
public sealed class SeedUser
{
    /// <summary>The realm's subject — must match the pinned <c>id</c> in the realm file.</summary>
    public string SubjectId { get; init; } = "";
    public string Email { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Locale { get; init; } = "en-GB";
    public string TimeZone { get; init; } = "Europe/Bucharest";

    /// <summary>The system role template to hold, by name — <c>BR-IAM-3</c> wants at least one.</summary>
    public string Role { get; init; } = "";
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

            // Saved before the users, because they are matched to roles by name and the templates
            // above may be the rows being matched against.
            await db.SaveChangesAsync(cancellationToken);

            await SeedUsersAsync(db, seed, clock, cancellationToken);

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

    /// <summary>
    /// Ensures the configured people exist, so a dev environment has somebody to be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Idempotent by subject, and it never touches a user it finds.</b> A profile edited in the
    /// app must survive the next start, and re-seeding a display name from a config file would be a
    /// silent overwrite of somebody's work — the same rule the tenant rows above follow.
    /// </para>
    /// <para>
    /// <b>Why this exists at all.</b> A rep signs in and the field app has nothing for them: the
    /// device pull is scoped by territory, territory membership hangs off a *user row*, and nothing
    /// creates one for a realm account. So the first run of a fresh database needed three API calls
    /// before the app could show a round — which was discovered the slow way, during W11's browser
    /// verification, after the wrong conclusion had already been drawn about the realm roles.
    /// </para>
    /// <para>
    /// A missing role name is logged and skipped rather than throwing: `BR-IAM-3` wants every user
    /// to hold at least one role, and a startup that refuses to boot over a typo in a dev
    /// convenience is worse than one that says what it could not do.
    /// </para>
    /// </remarks>
    private async Task SeedUsersAsync(
        IamDbContext db, SeedTenant seed, IClock clock, CancellationToken cancellationToken)
    {
        foreach (var user in seed.Users)
        {
            if (string.IsNullOrWhiteSpace(user.SubjectId) || string.IsNullOrWhiteSpace(user.Role))
            {
                logger.LogError(
                    "Ignoring malformed seed user '{Email}' on realm '{Realm}'.", user.Email, seed.Realm);

                continue;
            }

            if (await db.Users.AnyAsync(row => row.SubjectId == user.SubjectId, cancellationToken))
            {
                continue;
            }

            var role = await db.Roles.FirstOrDefaultAsync(
                candidate => candidate.Name == user.Role, cancellationToken);

            if (role is null)
            {
                logger.LogWarning(
                    "Seed user '{Email}' names role '{Role}', which this tenant does not have.",
                    user.Email,
                    user.Role);

                continue;
            }

            var created = User.Create(
                user.SubjectId, user.Email, user.DisplayName, user.Locale, user.TimeZone);

            created.SetRoles([role.Id], clock);

            db.Users.Add(created);

            logger.LogInformation(
                "Seeded user '{Email}' as '{Role}' on realm '{Realm}'.",
                user.Email,
                user.Role,
                seed.Realm);
        }
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
