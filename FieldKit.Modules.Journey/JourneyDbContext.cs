using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Journey;

/// <summary>The Journey module's context — owns the <c>journey</c> schema (schema-per-module).</summary>
public sealed class JourneyDbContext(DbContextOptions<JourneyDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext)
{
    public const string SchemaName = "journey";

    /// <summary>
    /// The frequency invariants, as SQL.
    /// </summary>
    /// <remarks>
    /// <see cref="CallFrequency.TryCreate"/> already refuses both, and this is not a second opinion —
    /// it is the one that holds against anything that writes the table: a migration, a fix-up
    /// script, the importer that does not exist yet. The same reasoning <c>rep_assignment</c>'s
    /// period constraint carries. Quoted because Postgres folds an unquoted identifier to lower
    /// case and these columns are <c>PascalCase</c>.
    /// </remarks>
    private const string VisitsArePositive = @"""VisitsPerCycle"" >= 1";

    /// <summary>Built from the constant rather than repeating 365, so the two cannot drift.</summary>
    private static readonly string CycleIsInRange =
        $@"""CycleLengthDays"" >= 1 AND ""CycleLengthDays"" <= {CallFrequency.MaximumCycleLengthDays}";

    protected override string Schema => SchemaName;

    public DbSet<SegmentFrequency> SegmentFrequencies => Set<SegmentFrequency>();

    public DbSet<OutletFrequency> OutletFrequencies => Set<OutletFrequency>();

    public DbSet<WorkingCalendar> WorkingCalendars => Set<WorkingCalendar>();

    public DbSet<Holiday> Holidays => Set<Holiday>();

    public DbSet<JourneyPlan> JourneyPlans => Set<JourneyPlan>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SegmentFrequency>(frequency =>
        {
            frequency.HasKey(rule => rule.Id);

            frequency.Property(rule => rule.Segment)
                .HasMaxLength(SegmentFrequency.MaximumSegmentLength)
                .IsRequired();

            // One rule per segment per tenant. Without it a tenant ends up with two answers for the
            // same grade and resolution takes whichever the database returned first — a plan that
            // changes between runs with nothing having been edited.
            frequency.HasIndex(rule => new { rule.TenantId, rule.Segment }).IsUnique();

            frequency.ToTable("segment_frequency", table =>
            {
                table.HasCheckConstraint("ck_segment_frequency_visits", VisitsArePositive);
                table.HasCheckConstraint("ck_segment_frequency_cycle", CycleIsInRange);
            });
        });

        modelBuilder.Entity<OutletFrequency>(frequency =>
        {
            frequency.HasKey(rule => rule.Id);

            // One override per outlet per tenant, for the same reason.
            frequency.HasIndex(rule => new { rule.TenantId, rule.OutletId }).IsUnique();

            // No foreign key to the outlet: it lives in another module's schema (AT-1), and the id
            // is checked through IOutletCatalog on the way in instead.

            frequency.ToTable("outlet_frequency", table =>
            {
                table.HasCheckConstraint("ck_outlet_frequency_visits", VisitsArePositive);
                table.HasCheckConstraint("ck_outlet_frequency_cycle", CycleIsInRange);
            });
        });

        modelBuilder.Entity<WorkingCalendar>(calendar =>
        {
            calendar.HasKey(row => row.Id);

            calendar.Property(row => row.UserId).HasMaxLength(64).IsRequired();

            // The days as an `integer[]`, the same shape a field definition's options take. Readable
            // in the database and, unlike a bitmask, it says what it holds without a lookup table.
            calendar.PrimitiveCollection(row => row.WorkingDays)
                .HasColumnName("working_days")
                .IsRequired();

            // One calendar per rep per tenant. Two would be two answers to "when does this rep
            // work", and generation would take whichever the database returned first.
            calendar.HasIndex(row => new { row.TenantId, row.UserId }).IsUnique();

            calendar.ToTable("working_calendar", table => table.HasCheckConstraint(
                "ck_working_calendar_capacity",
                $@"""VisitsPerDay"" >= 1 AND ""VisitsPerDay"" <= {WorkingCalendar.MaximumVisitsPerDay}"));
        });

        modelBuilder.Entity<Holiday>(holiday =>
        {
            holiday.HasKey(row => row.Id);

            holiday.Property(row => row.Name)
                .HasMaxLength(Holiday.MaximumNameLength)
                .IsRequired();

            // One entry per date. A tenant importing a year twice should end up with one Christmas,
            // and the second import should say so rather than double it.
            holiday.HasIndex(row => new { row.TenantId, row.Date }).IsUnique();

            holiday.ToTable("holiday");
        });

        modelBuilder.Entity<JourneyPlan>(plan =>
        {
            plan.HasKey(row => row.Id);

            plan.Property(row => row.UserId).HasMaxLength(64).IsRequired();
            plan.Property(row => row.FromDate).HasColumnName("from_date").IsRequired();
            plan.Property(row => row.ToDate).HasColumnName("to_date").IsRequired();

            // By name, never as an ordinal — a member inserted in the middle would silently
            // re-interpret every stored plan rather than breaking a build.
            plan.Property(row => row.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

            // "What has this rep got, and when" — the query every screen and, later, Sync makes.
            plan.HasIndex(row => new { row.TenantId, row.UserId, row.FromDate });

            // Regular entities rather than owned types, because `ModuleDbContext` applies the tenant
            // filter to every `ITenantOwned` type it finds — which makes them non-owned by the time
            // this runs, and EF refuses the contradiction. Being filtered in their own right is the
            // better outcome anyway: a query that reaches a visit without going through its plan is
            // still tenant-scoped.
            plan.HasMany(row => row.Visits)
                .WithOne()
                .HasForeignKey(visit => new { visit.TenantId, visit.JourneyPlanId })
                .HasPrincipalKey(row => new { row.TenantId, row.Id })
                .OnDelete(DeleteBehavior.Cascade);

            plan.HasMany(row => row.Shortfalls)
                .WithOne()
                .HasForeignKey(shortfall => new { shortfall.TenantId, shortfall.JourneyPlanId })
                .HasPrincipalKey(row => new { row.TenantId, row.Id })
                .OnDelete(DeleteBehavior.Cascade);

            plan.Navigation(row => row.Visits).HasField("_visits").UsePropertyAccessMode(PropertyAccessMode.Field);
            plan.Navigation(row => row.Shortfalls).HasField("_shortfalls").UsePropertyAccessMode(PropertyAccessMode.Field);

            plan.ToTable("journey_plan", table => table.HasCheckConstraint(
                "ck_journey_plan_window", @"""to_date"" >= ""from_date"""));
        });

        modelBuilder.Entity<PlannedVisit>(visit =>
        {
            visit.HasKey(row => row.Id);

            // Both by name, never as ordinals — a member inserted in the middle would silently
            // re-interpret every stored row rather than breaking a build.
            visit.Property(row => row.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            visit.Property(row => row.Source).HasConversion<string>().HasMaxLength(20).IsRequired();

            visit.Property(row => row.NotVisitedReason).HasMaxLength(PlannedVisit.MaximumReasonLength);

            // The rep's day: "what am I doing on the 4th" — the query the device makes every morning.
            visit.HasIndex(row => new { row.TenantId, row.JourneyPlanId, row.Date });

            // BR-JRN-2's other half, in SQL. A skipped call must say why, and a call still to do must
            // not carry a reason — without this, "not visited" could be recorded with no explanation
            // by anything that writes the table, and the compliance metric would inherit blanks.
            visit.ToTable("planned_visit", table => table.HasCheckConstraint(
                "ck_planned_visit_reason",
                @"(""Status"" = 'NotVisited') = (""NotVisitedReason"" IS NOT NULL)"));
        });

        modelBuilder.Entity<PlanShortfall>(shortfall =>
        {
            shortfall.HasKey(row => row.Id);

            // A shortfall that is not short is a contradiction, and the row would be noise on a
            // screen whose whole job is to list the real ones.
            shortfall.ToTable("plan_shortfall", table => table.HasCheckConstraint(
                "ck_plan_shortfall_is_short", @"""Planned"" < ""Required"""));
        });
    }
}
