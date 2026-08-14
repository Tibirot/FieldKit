using FieldKit.BuildingBlocks;
using FieldKit.Modules.Org.Contracts;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FieldKit.Modules.Journey;

/// <summary>One rep to give a round to at startup, from configuration.</summary>
public sealed class SeedRound
{
    /// <summary>The tenant — matches an <c>Iam:SeedTenants</c> id.</summary>
    public string TenantId { get; init; } = "";

    /// <summary>The realm subject, the same one the IAM and Org seeders name.</summary>
    public string UserId { get; init; } = "";

    /// <summary>How often each of the rep's shops is called. One a week reads like a real territory.</summary>
    public int VisitsPerCycle { get; init; } = 1;

    public int CycleLengthDays { get; init; } = 7;

    /// <summary>
    /// Days before today the window opens. <b>Zero, and that is a finding rather than a default.</b>
    /// </summary>
    /// <remarks>
    /// It was seven, so a rep would open the app onto a round already under way — and the seeded
    /// plan came back with **no calls at all**. `JourneyPlanner` reads coverage once, on the
    /// window's *first* day, which its own comment names as a known limitation; the rep assignment
    /// the Org seeder writes starts **today**. So a window opening last Monday asked "what did this
    /// rep cover a week ago", got nothing, and generated an empty plan that looked like a success in
    /// every log line.
    /// </remarks>
    public int DaysBefore { get; init; }

    /// <summary>Days after today it closes. Long enough that a call has somewhere to be moved to.</summary>
    public int DaysAfter { get; init; } = 21;
}

/// <summary>
/// Publishes a round covering today, so a dev environment has a day to work — W12.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asked for by both regression sweeps, and it cost both of them.</b> The rep-side pass of 13 Aug
/// could not reach check-in, the audit or order capture, because a rep with no published round has
/// nothing to tap; the 14 Aug pass recorded the same gap again and called a seeded plan "the single
/// change that would most improve the next pass". W12's browser verification of F2b then spent its
/// first twenty minutes generating and publishing one by hand.
/// </para>
/// <para>
/// <b>It runs the real generator.</b> A seeder that wrote calls straight into the table would be
/// cheaper and would never exercise `JRN-03`, so a break in generation would stay invisible to every
/// manual pass — which is exactly the class of gap these sweeps keep finding. Setting a frequency
/// and a working calendar first is not overhead; it is the seeder using the product.
/// </para>
/// <para>
/// <b>Per-outlet frequencies rather than a segment rule</b>, because `IOutletCatalog` does not carry
/// a segment and a seeder has no business inferring one. `OutletFrequency` is the supported
/// per-shop override (`JRN-01`) and needs only the ids <see cref="IRepScope"/> already returns.
/// </para>
/// <para>
/// <b>Idempotent by coverage of today, not by row existence.</b> Every other seeder asks "is it
/// there?"; this asks "does a published plan cover *today*?" — because a dev environment left
/// running drifts out of any fixed window, and a round that went stale three weeks ago is the same
/// empty app this exists to prevent. The cost is a new plan every time the window lapses, which on
/// a development database is the right trade.
/// </para>
/// </remarks>
public sealed class JourneyRoundSeeder(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<JourneyRoundSeeder> logger) : IHostedService
{
    public const string ConfigurationSection = "Journey:SeedRounds";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seeds = configuration.GetSection(ConfigurationSection).Get<SeedRound[]>() ?? [];
        if (seeds.Length == 0) return;

        foreach (var seed in seeds)
        {
            if (!Guid.TryParse(seed.TenantId, out var tenantId)
                || string.IsNullOrWhiteSpace(seed.UserId)
                || !CallFrequency.TryCreate(seed.VisitsPerCycle, seed.CycleLengthDays, out var frequency))
            {
                logger.LogError("Ignoring malformed seed round for user '{UserId}'.", seed.UserId);

                continue;
            }

            /*
             * A scope per seed, and the tenant named inside it.
             *
             * The services below are the ordinary registered ones — the planner, the rep scope, the
             * context — and they all read `ITenantContext`, which outside a request has nothing to
             * read. `TenantScope` is the seam that answers, and it grants nothing: the identity it
             * pushes holds no permissions at all.
             */
            using var scope = services.CreateScope();
            using var acting = TenantScope.For(new TenantId(tenantId), "system");

            try
            {
                await SeedAsync(scope.ServiceProvider, seed, frequency, cancellationToken);
            }
            catch (Exception exception)
            {
                /*
                 * Logged and swallowed, unlike the seeders before it.
                 *
                 * Those write one row each into their own module. This one runs generation across
                 * three modules, and a development database in any half-configured state — a rep on
                 * no territory, a territory with no shops — is a state it should report rather than
                 * refuse to boot on. A dev environment that will not start is worse than one with
                 * no round, because the second is what this fixes and the first is what it breaks.
                 */
                logger.LogError(
                    exception, "Could not seed a round for '{UserId}'.", seed.UserId);
            }
        }
    }

    private async Task SeedAsync(
        IServiceProvider provider,
        SeedRound seed,
        CallFrequency frequency,
        CancellationToken cancellationToken)
    {
        var db = provider.GetRequiredService<JourneyDbContext>();
        var clock = provider.GetRequiredService<IClock>();

        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        // The question this seeder is idempotent on. A draft does not count: a device pulls
        // published plans only, so an unpublished one leaves the rep exactly as empty-handed.
        var covered = await db.JourneyPlans.AnyAsync(
            plan => plan.UserId == seed.UserId
                && plan.Status == JourneyPlanStatus.Published
                && plan.FromDate <= today
                && plan.ToDate >= today,
            cancellationToken);

        if (covered) return;

        var coverage = await provider.GetRequiredService<IRepScope>()
            .ForRepAsync(seed.UserId, today, cancellationToken);

        if (coverage.OutletIds.Count == 0)
        {
            // Not an error: on a fresh database there are no outlets and no territories yet, and the
            // rep assignment seeder says the same thing about a territory that does not exist.
            logger.LogInformation(
                "'{UserId}' covers no shops today — nothing to build a round from.", seed.UserId);

            return;
        }

        await EnsureCalendarAsync(db, seed, clock, cancellationToken);
        await EnsureFrequenciesAsync(db, coverage, frequency, cancellationToken);

        var from = today.AddDays(-seed.DaysBefore);
        var to = today.AddDays(seed.DaysAfter);

        var generated = await provider.GetRequiredService<JourneyPlanner>()
            .GenerateAsync(seed.UserId, from, to, cancellationToken);

        /*
         * An empty plan is not published, and this guard is the one the slice earned.
         *
         * Generation can return nothing for reasons that all look like success from here — every
         * shop excluded, no working day in the window, or the window opening before the rep's
         * assignment did (see `DaysBefore`). Publishing that would satisfy this seeder's own
         * idempotence check forever: today would be "covered" by a plan with no calls on it, and
         * every restart would leave the rep exactly as stuck while reporting a round.
         */
        if (generated.Visits.Count == 0)
        {
            logger.LogWarning(
                "Generation produced no calls for '{UserId}' over {From}–{To}; not publishing an "
                + "empty round. Check the rep's territory, their working calendar and the shops' "
                + "frequencies.",
                seed.UserId,
                from,
                to);

            return;
        }

        var plan = JourneyPlan.Draft(seed.UserId, from, to, generated, clock);

        // Published in the same save as the draft. A plan that exists and is not published is the
        // state this seeder is *checking* for, so leaving one behind would make the next start find
        // work already done and do nothing.
        plan.TryPublish(clock);

        db.JourneyPlans.Add(plan);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Seeded a published round for '{UserId}': {Calls} call(s) from {From} to {To}.",
            seed.UserId,
            plan.Visits.Count,
            from,
            to);
    }

    /// <summary>A working week, if the rep has none. Never replaces one somebody has edited.</summary>
    private static async Task EnsureCalendarAsync(
        JourneyDbContext db, SeedRound seed, IClock clock, CancellationToken cancellationToken)
    {
        if (await db.WorkingCalendars.AnyAsync(row => row.UserId == seed.UserId, cancellationToken))
        {
            return;
        }

        // Monday to Friday, and a capacity comfortably above what a seeded territory holds — the
        // point is a round the rep can work, not a demonstration of `BR-JRN-3`'s cap.
        if (WorkingCalendar.TryCreate(
                seed.UserId,
                [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
                10,
                out var calendar))
        {
            db.WorkingCalendars.Add(calendar);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>One frequency per covered shop, for the ones that have none.</summary>
    /// <remarks>
    /// A shop with no frequency is *excluded* from generation rather than planned at some default
    /// (`BR-JRN-5`'s neighbour), so without this the generator returns an empty plan and the seeder
    /// looks like it worked.
    /// </remarks>
    private static async Task EnsureFrequenciesAsync(
        JourneyDbContext db,
        RepCoverage coverage,
        CallFrequency frequency,
        CancellationToken cancellationToken)
    {
        var already = await db.OutletFrequencies
            .Where(row => coverage.OutletIds.Contains(row.OutletId))
            .Select(row => row.OutletId)
            .ToListAsync(cancellationToken);

        var missing = coverage.OutletIds.Except(already).ToList();
        if (missing.Count == 0) return;

        db.OutletFrequencies.AddRange(
            missing.Select(outletId => OutletFrequency.Create(outletId, frequency)));

        await db.SaveChangesAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
