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

    public DbSet<VisitStep> VisitSteps => Set<VisitStep>();

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

        modelBuilder.Entity<VisitStep>(step =>
        {
            step.HasKey(s => s.Id);

            step.Property(s => s.Label).HasMaxLength(120).IsRequired();
            step.Property(s => s.Notes).HasMaxLength(VisitStep.MaximumNotesLength);

            // Both by name, both for the same reason the visit's own status is: a member inserted
            // in the middle of an enum would silently re-interpret every stored row. VisitStepType
            // is Configuration's, which makes this the one column in this schema whose vocabulary
            // another module owns — renaming a member there is a data migration here.
            step.Property(s => s.Type).HasConversion<string>().HasMaxLength(20).IsRequired();
            step.Property(s => s.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

            // The visit is the aggregate: its steps are created with it, read with it and go with
            // it, so the only query that ever reaches this table is "the steps of this visit". The
            // foreign key's own index answers it, and a second one on (TenantId, VisitId) would be
            // a write cost for a read nobody makes.
            step.HasOne<Visit>()
                .WithMany(v => v.Steps)
                .HasForeignKey(s => s.VisitId)
                .OnDelete(DeleteBehavior.Cascade);

            step.ToTable("visit_step", table =>
            {
                // A completed step has the moment it was completed at, and a pending one has none.
                // Time-on-step is the reporting fact BR-VIS-5 is about, and a completed step with
                // no timestamp would quietly drop out of it rather than look wrong.
                table.HasCheckConstraint(
                    "ck_visit_step_completed_at",
                    @"(""Status"" = 'Completed') = (""CompletedAtUtc"" IS NOT NULL)");

                // A note step is its text. One completed with nothing written is not a note that
                // says nothing — it is a step that was ticked, which is the thing VIS-06 is for.
                table.HasCheckConstraint(
                    "ck_visit_step_note_text",
                    @"""Type"" <> 'Note' OR ""Status"" <> 'Completed' OR ""Notes"" IS NOT NULL");
            });
        });
    }
}
