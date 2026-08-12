using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FieldKit.Modules.Org;

/// <summary>One rep to put on a territory at startup, from configuration.</summary>
public sealed class SeedRepAssignment
{
    /// <summary>The tenant whose territory this is — matches an <c>Iam:SeedTenants</c> id.</summary>
    public string TenantId { get; init; } = "";

    /// <summary>The realm subject, the same one <c>Iam:SeedTenants[].Users</c> creates a row for.</summary>
    public string UserId { get; init; } = "";

    /// <summary>The territory, by name. A human writes this file, and names are what they know.</summary>
    public string Territory { get; init; } = "";
}

/// <summary>
/// Puts the seeded rep on a territory, so a dev environment has a round to work.
/// </summary>
/// <remarks>
/// <para>
/// <b>Territory membership is what a device pull is scoped by</b>
/// (<c>BR-ORG-3</c>), so a rep with no assignment signs in to an empty app — no shops, no journey,
/// no order screen worth opening. Nothing in the product creates that assignment for a realm account,
/// and nothing should: giving a rep a territory is an administrator's decision. What a *development*
/// environment needs is for that decision to have already been made.
/// </para>
/// <para>
/// <b>Found the slow way.</b> W11's browser verification could not reach the order screen at all, and
/// the first conclusion drawn was the wrong one — that the realm's <c>rep</c> was missing permissions.
/// It was not; the realms README says so explicitly, and the whole flow ran as a user holding no
/// product permissions at all. The blocker was this row, and only this row.
/// </para>
/// <para>
/// <b>Separate from IAM's seeder rather than folded into it.</b> A user is IAM's and a territory
/// assignment is Organization's, and a seeder reaching across that line would be the first code in
/// the system to do it (AT-1). They coordinate the honest way instead: through the subject id, which
/// both read from configuration and neither owns.
/// </para>
/// <para>
/// <b>It never invents a territory.</b> A missing one is logged and skipped — on a fresh database
/// there are no territories, no outlets and no products, and a seeder that quietly created an empty
/// territory would leave a rep with a round of nowhere and an explanation nobody could find.
/// </para>
/// </remarks>
public sealed class RepAssignmentSeeder(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<RepAssignmentSeeder> logger) : IHostedService
{
    public const string ConfigurationSection = "Org:SeedRepAssignments";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seeds = configuration.GetSection(ConfigurationSection).Get<SeedRepAssignment[]>() ?? [];
        if (seeds.Length == 0) return;

        using var scope = services.CreateScope();

        var connectionString = configuration.GetConnectionString("fieldkitdb")
            ?? throw new InvalidOperationException("Connection string 'fieldkitdb' is not configured.");

        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        foreach (var seed in seeds)
        {
            if (!Guid.TryParse(seed.TenantId, out var tenantId)
                || string.IsNullOrWhiteSpace(seed.UserId)
                || string.IsNullOrWhiteSpace(seed.Territory))
            {
                logger.LogError(
                    "Ignoring malformed seed rep assignment for territory '{Territory}'.", seed.Territory);

                continue;
            }

            // One context per tenant, bound to that tenant — the same argument `TenantSeeder` makes:
            // territories and assignments are tenant-owned, so the query filter has to be able to
            // see them and the stamping interceptor has to know what to write.
            var identity = new SeedingIdentity(new TenantId(tenantId));

            await using var db = new OrgDbContext(
                BuildOptions(connectionString, clock, identity), identity);

            var territory = await db.Territories
                .FirstOrDefaultAsync(row => row.Name == seed.Territory, cancellationToken);

            if (territory is null)
            {
                logger.LogInformation(
                    "No territory named '{Territory}' yet — the seeded rep has nothing to cover.",
                    seed.Territory);

                continue;
            }

            // Idempotent, and by *rep* rather than by row: a second assignment for the same rep on
            // the same territory would collide with `BR-ORG-2`'s overlapping-period refusal, and an
            // assignment somebody has since edited must survive the next start.
            var already = await db.RepAssignments.AnyAsync(
                row => row.TerritoryId == territory.Id && row.UserId == seed.UserId, cancellationToken);

            if (already) continue;

            /*
             * Open-ended, starting today.
             *
             * `to` is null because a development assignment has no reason to expire, and a fixed end
             * date is a trap that fires weeks later as an empty app on a Monday. Starting today
             * rather than at some epoch keeps the row honest: the rep covers this territory from
             * when it was seeded, which is what happened.
             */
            db.RepAssignments.Add(RepAssignment.Create(
                territory.Id,
                seed.UserId,
                new DateRange(DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), null),
                clock));

            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Seeded rep '{UserId}' onto territory '{Territory}'.", seed.UserId, seed.Territory);
        }
    }

    private static DbContextOptions<OrgDbContext> BuildOptions(
        string connectionString, IClock clock, ITenantContext identity) =>
        new DbContextOptionsBuilder<OrgDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", OrgDbContext.SchemaName))
            .AddInterceptors(new EntityStampingInterceptor(clock, identity))
            .Options;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The identity startup work runs under: the system, acting within one tenant.</summary>
    /// <remarks>
    /// Holds no permissions, for the reason <c>TenantSeeder</c>'s copy gives — it exists to satisfy
    /// the query filter and the audit stamp, not to grant startup code something an administrator
    /// could not do.
    /// </remarks>
    private sealed class SeedingIdentity(TenantId tenantId) : ITenantContext
    {
        public TenantId TenantId => tenantId;
        public string UserId => "system";
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public bool Has(string permission) => false;
    }
}
