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
    }
}
