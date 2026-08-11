using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Configuration;

/// <summary>The Configuration module's context — owns the <c>config</c> schema (ADR-0005, ADR-0009 §0).</summary>
public sealed class ConfigurationDbContext(
    DbContextOptions<ConfigurationDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext)
{
    public const string SchemaName = "config";

    protected override string Schema => SchemaName;

    /// <summary>VisitWorkflow is <c>ISyncTracked</c>, so this schema owns a row-version counter (ADR-0013).</summary>
    protected override bool TracksSyncChanges => true;

    public DbSet<FieldDefinition> FieldDefinitions => Set<FieldDefinition>();

    public DbSet<VisitWorkflow> VisitWorkflows => Set<VisitWorkflow>();

    public DbSet<ScoreWeightSet> ScoreWeightSets => Set<ScoreWeightSet>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<FieldDefinition>(definition =>
        {
            definition.ToTable("field_definition");
            definition.HasKey(d => d.Id);

            // Both stored as names, not ordinals: an enum's numeric value is a position in a source
            // file, and reordering the members would silently reinterpret every row.
            definition.Property(d => d.Entity).HasConversion<string>().HasMaxLength(30).IsRequired();
            definition.Property(d => d.Type).HasConversion<string>().HasMaxLength(20).IsRequired();

            definition.Property(d => d.Key).HasMaxLength(60).IsRequired();
            definition.Property(d => d.Label).HasMaxLength(200).IsRequired();

            // One definition per key per entity. Two would make "which one validates this value?" a
            // question with no answer, and the loser would be whichever the query returned second.
            definition.HasIndex(d => new { d.TenantId, d.Entity, d.Key }).IsUnique();

            // A value list, not an entity: the options exist only inside this definition, and a
            // table would invite something else to reference one.
            definition.PrimitiveCollection(d => d.Options).HasColumnName("options").IsRequired();

            // The catalogue's one hot query: "every field for this entity", asked on every write of
            // every outlet.
            definition.HasIndex(d => new { d.TenantId, d.Entity });
        });

        modelBuilder.Entity<VisitWorkflow>(workflow =>
        {
            workflow.HasKey(w => w.Id);

            // One workflow per channel. Two would be two answers to "how is a visit worked here",
            // and check-in would take whichever the database returned first.
            workflow.HasIndex(w => new { w.TenantId, w.ChannelId }).IsUnique();

            // No foreign key to the channel: it lives in Outlets' schema (AT-1). An id that no
            // longer resolves simply answers the default, which is what an unconfigured channel
            // does — see VisitWorkflowCatalog.
            workflow.HasMany(w => w.Steps)
                .WithOne()
                .HasForeignKey(step => new { step.TenantId, step.VisitWorkflowId })
                .HasPrincipalKey(w => new { w.TenantId, w.Id })
                .OnDelete(DeleteBehavior.Cascade);

            workflow.Navigation(w => w.Steps).HasField("_steps").UsePropertyAccessMode(PropertyAccessMode.Field);

            workflow.ToTable("visit_workflow");
        });

        modelBuilder.Entity<VisitWorkflowStep>(step =>
        {
            step.HasKey(s => s.Id);

            // By name, never as an ordinal — a step type inserted in the middle of the enum would
            // silently re-interpret every stored workflow rather than breaking a build.
            step.Property(s => s.Type).HasConversion<string>().HasMaxLength(20).IsRequired();

            step.Property(s => s.Label).HasMaxLength(VisitWorkflowStep.MaximumLabelLength).IsRequired();

            // The sequence is the point of the thing, so two steps cannot claim one position. The
            // domain assigns them contiguously; this is what holds if anything else writes the table.
            step.HasIndex(s => new { s.TenantId, s.VisitWorkflowId, s.Order }).IsUnique();

            step.ToTable("visit_workflow_step", table => table.HasCheckConstraint(
                "ck_visit_workflow_step_order", @"""Order"" >= 1"));
        });

        modelBuilder.Entity<ScoreWeightSet>(set =>
        {
            set.HasKey(s => s.Id);

            // One set per version per tenant, and the domain assigns the number. This is what holds
            // if two administrators draft at the same moment: the second insert loses rather than
            // producing two "version 4"s, one of which every sealed audit would then ambiguously
            // point at.
            set.HasIndex(s => new { s.TenantId, s.Version }).IsUnique();

            // The one hot read: "the tenant's published sets, newest first" — asked by the score and
            // by the pull feed.
            set.HasIndex(s => new { s.TenantId, s.PublishedAtUtc });

            set.HasMany(s => s.Weights)
                .WithOne()
                .HasForeignKey(weight => new { weight.TenantId, weight.ScoreWeightSetId })
                .HasPrincipalKey(s => new { s.TenantId, s.Id })
                .OnDelete(DeleteBehavior.Cascade);

            set.Navigation(s => s.Weights).HasField("_weights").UsePropertyAccessMode(PropertyAccessMode.Field);

            set.ToTable("score_weight_set", table => table.HasCheckConstraint(
                "ck_score_weight_set_version", @"""Version"" >= 1"));
        });

        modelBuilder.Entity<ScoreWeight>(weight =>
        {
            weight.HasKey(w => w.Id);

            // By name, for the reason every other enum here is: an ordinal is a position in a source
            // file, and a pillar inserted in the middle would re-interpret every stored weight.
            weight.Property(w => w.Pillar).HasConversion<string>().HasMaxLength(30).IsRequired();

            /*
             * `numeric(5,2)`: percentages to two decimal places, which is as fine as an administrator
             * can express and as fine as the score needs.
             *
             * Explicit because the default for `decimal` on Npgsql is unconstrained `numeric`, and an
             * unconstrained column would happily store a weight with more precision than the UI can
             * show — so a set that summed to 100 on screen would fail its own check on reload.
             */
            weight.Property(w => w.Percentage).HasPrecision(5, 2);

            // One weight per pillar per set. The aggregate refuses a duplicate; this is what holds
            // if anything else writes the table, and it is the invariant the sum depends on.
            weight.HasIndex(w => new { w.TenantId, w.ScoreWeightSetId, w.Pillar }).IsUnique();

            weight.ToTable("score_weight", table => table.HasCheckConstraint(
                "ck_score_weight_percentage", @"""Percentage"" >= 0 AND ""Percentage"" <= 100"));
        });
    }
}
