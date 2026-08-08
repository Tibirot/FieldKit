using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Visit;

/// <summary>The Visit module's context — owns the <c>visit</c> schema (schema-per-module).</summary>
public sealed class VisitDbContext(DbContextOptions<VisitDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext)
{
    public const string SchemaName = "visit";

    protected override string Schema => SchemaName;

    public DbSet<Visit> Visits => Set<Visit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Visit>(visit =>
        {
            visit.HasKey(v => v.Id);

            visit.Property(v => v.UserId).HasMaxLength(64).IsRequired();

            // By name, never as an ordinal — a state inserted in the middle of the enum would
            // silently re-interpret every stored visit rather than breaking a build.
            visit.Property(v => v.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

            visit.Property(v => v.GeofenceOverrideReason)
                .HasMaxLength(Visit.MaximumOverrideReasonLength);

            // "What has this rep been doing", which is the query both the supervisor's screen and,
            // later, Sync will make.
            visit.HasIndex(v => new { v.TenantId, v.UserId, v.CheckedInAtUtc });

            // "Who has been to this shop" — the outlet's own history.
            visit.HasIndex(v => new { v.TenantId, v.OutletId, v.CheckedInAtUtc });

            // No foreign keys to the outlet or the planned visit: both live in other modules'
            // schemas (AT-1). The outlet is checked through IOutletGeofence on the way in.

            visit.ToTable("visit", table =>
            {
                // BR-VIS-2 in SQL: a visit outside the geofence carries a reason, and one inside
                // does not. Both directions, so a stale reason cannot survive a correction — the
                // same shape planned_visit's not-visited constraint takes.
                table.HasCheckConstraint(
                    "ck_visit_override_reason",
                    @"""WasInsideGeofence"" = false OR ""GeofenceOverrideReason"" IS NULL");

                // A latitude without a longitude is not a position. The same rule the outlet's own
                // coordinates carry, and for the same reason: half a point is worse than none,
                // because it looks like data.
                table.HasCheckConstraint(
                    "ck_visit_checkin_point",
                    @"(""CheckInLatitude"" IS NULL) = (""CheckInLongitude"" IS NULL)");
            });
        });
    }
}
