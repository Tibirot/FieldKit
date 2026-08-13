using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Audit;

/// <summary>The Audit module's context — owns the <c>audit</c> schema (ADR-0005).</summary>
public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext)
{
    public const string SchemaName = "audit";

    protected override string Schema => SchemaName;

    /// <summary>
    /// Nothing here is <c>ISyncTracked</c>, so this schema owns no row-version counter.
    /// </summary>
    /// <remarks>
    /// Audits travel <b>up</b> — device to server, through the outbox — and nothing pulls them down.
    /// A row-version counter exists to answer "what changed since", which is a question only a device
    /// holding a copy asks, and no device holds a copy of an audit it did not write.
    /// </remarks>
    protected override bool TracksSyncChanges => false;

    public DbSet<Audit> Audits => Set<Audit>();

    /// <summary>
    /// Photo references on their own, for confirming an upload by key (W11 slice 13a).
    /// </summary>
    /// <remarks>
    /// The only section exposed outside its aggregate, and only because a confirmation arrives naming
    /// a key rather than an audit — loading the audits to reach them would fetch five collections to
    /// write one timestamp. Still tenant-filtered: <see cref="PhotoEntry"/> is
    /// <see cref="ITenantOwned"/>, so the global filter applies here exactly as it does everywhere.
    /// </remarks>
    public DbSet<PhotoEntry> Photos => Set<PhotoEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Audit>(audit =>
        {
            audit.HasKey(a => a.Id);

            audit.Property(a => a.UserId).HasMaxLength(64).IsRequired();

            // One audit per visit — the invariant every reader depends on. Here as well as in the
            // aggregate, because "this shop's availability last Tuesday" having two answers is not
            // something a later reader can be expected to resolve.
            audit.HasIndex(a => new { a.TenantId, a.VisitId }).IsUnique();

            // "How has this shop been trending" — the one read AUD-09 makes, and the reason the
            // outlet id is copied onto the audit rather than reached through the visit.
            audit.HasIndex(a => new { a.TenantId, a.OutletId, a.CapturedAtUtc });

            // No foreign key to the visit, the outlet or any product: all three live in other
            // modules' schemas (AT-1). The visit is checked through IVisitContext on the way in.

            audit.HasMany(a => a.Availability)
                .WithOne()
                .HasForeignKey(entry => new { entry.TenantId, entry.AuditId })
                .HasPrincipalKey(a => new { a.TenantId, a.Id })
                .OnDelete(DeleteBehavior.Cascade);

            audit.HasMany(a => a.Facings)
                .WithOne()
                .HasForeignKey(entry => new { entry.TenantId, entry.AuditId })
                .HasPrincipalKey(a => new { a.TenantId, a.Id })
                .OnDelete(DeleteBehavior.Cascade);

            audit.HasMany(a => a.Prices)
                .WithOne()
                .HasForeignKey(entry => new { entry.TenantId, entry.AuditId })
                .HasPrincipalKey(a => new { a.TenantId, a.Id })
                .OnDelete(DeleteBehavior.Cascade);

            audit.HasMany(a => a.Answers)
                .WithOne()
                .HasForeignKey(entry => new { entry.TenantId, entry.AuditId })
                .HasPrincipalKey(a => new { a.TenantId, a.Id })
                .OnDelete(DeleteBehavior.Cascade);

            audit.HasMany(a => a.Photos)
                .WithOne()
                .HasForeignKey(entry => new { entry.TenantId, entry.AuditId })
                .HasPrincipalKey(a => new { a.TenantId, a.Id })
                .OnDelete(DeleteBehavior.Cascade);

            audit.Navigation(a => a.Availability).HasField("_availability")
                .UsePropertyAccessMode(PropertyAccessMode.Field);
            audit.Navigation(a => a.Facings).HasField("_facings")
                .UsePropertyAccessMode(PropertyAccessMode.Field);
            audit.Navigation(a => a.Prices).HasField("_prices")
                .UsePropertyAccessMode(PropertyAccessMode.Field);
            audit.Navigation(a => a.Answers).HasField("_answers")
                .UsePropertyAccessMode(PropertyAccessMode.Field);
            audit.HasMany(a => a.ScoredPillars)
                .WithOne()
                .HasForeignKey(entry => new { entry.TenantId, entry.AuditId })
                .HasPrincipalKey(a => new { a.TenantId, a.Id })
                .OnDelete(DeleteBehavior.Cascade);

            audit.Navigation(a => a.Photos).HasField("_photos")
                .UsePropertyAccessMode(PropertyAccessMode.Field);
            audit.Navigation(a => a.ScoredPillars).HasField("_scoredPillars")
                .UsePropertyAccessMode(PropertyAccessMode.Field);

            /*
             * `numeric(5,2)`: 0–100 to two places, the scale the scorer rounds to.
             *
             * Explicit because Npgsql's default for `decimal` is unconstrained `numeric`, which would
             * store a score at whatever precision happened to arrive — and a column that can hold
             * more precision than the arithmetic produces is a column somebody eventually writes an
             * unrounded value into. The same call `score_weight.Percentage` makes.
             */
            audit.Property(a => a.Score).HasPrecision(5, 2);

            audit.ToTable("audit", table => table.HasCheckConstraint(
                // The denominator is a count or it is absent; a negative one is not a shelf.
                // Nullable on purpose — see Audit.CategoryFacings for why zero is not the default.
                "ck_audit_category_facings", @"""CategoryFacings"" IS NULL OR ""CategoryFacings"" >= 0"));
        });

        modelBuilder.Entity<AvailabilityEntry>(entry =>
        {
            entry.HasKey(e => e.Id);

            // By name, never as an ordinal — a status inserted in the middle of the enum would
            // silently re-interpret every stored audit rather than breaking a build. This one is
            // load-bearing: Absent and OutOfStock are one position apart and mean opposite things.
            entry.Property(e => e.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

            // One verdict per product per audit. Two would leave the availability pillar counting a
            // shelf twice, with no rule for which reading wins.
            entry.HasIndex(e => new { e.TenantId, e.AuditId, e.ProductId }).IsUnique();

            entry.ToTable("audit_availability");
        });

        modelBuilder.Entity<FacingsEntry>(entry =>
        {
            entry.HasKey(e => e.Id);

            entry.HasIndex(e => new { e.TenantId, e.AuditId, e.ProductId }).IsUnique();

            entry.ToTable("audit_facings", table => table.HasCheckConstraint(
                // Zero facings is a real count — the product is listed and the shelf is bare.
                // Negative is not a count at all.
                "ck_audit_facings_count", @"""Facings"" >= 0"));
        });

        modelBuilder.Entity<PriceEntry>(entry =>
        {
            entry.HasKey(e => e.Id);

            entry.Property(e => e.Currency)
                .HasMaxLength(PriceEntry.CurrencyLength)
                .IsFixedLength()
                .IsRequired();

            // Derived from the two amounts, so there is no column to map — and saying so stops EF
            // looking for one. Same call `Visit.TimeOnSite` makes.
            entry.Ignore(e => e.DeltaMinorUnits);

            entry.HasIndex(e => new { e.TenantId, e.AuditId, e.ProductId }).IsUnique();

            entry.ToTable("audit_price", table => table.HasCheckConstraint(
                // A price is not negative. A shelf edge reading below zero is a typo on a phone, and
                // storing it would produce a compliance delta with a sign nobody can explain.
                "ck_audit_price_amounts",
                @"""ObservedMinorUnits"" >= 0 AND (""ExpectedMinorUnits"" IS NULL OR ""ExpectedMinorUnits"" >= 0)"));
        });

        modelBuilder.Entity<SurveyAnswerEntry>(entry =>
        {
            entry.HasKey(e => e.Id);

            entry.Property(e => e.QuestionKey)
                .HasMaxLength(SurveyAnswerEntry.MaximumKeyLength).IsRequired();
            entry.Property(e => e.QuestionText)
                .HasMaxLength(SurveyAnswerEntry.MaximumTextLength).IsRequired();
            entry.Property(e => e.Value)
                .HasMaxLength(SurveyAnswerEntry.MaximumValueLength).IsRequired();

            // One answer per question, and the sequence the rep was asked in. Both unique for the
            // same reason: the key is what an answer is filed under, and two under one key is two
            // answers with one name.
            entry.HasIndex(e => new { e.TenantId, e.AuditId, e.QuestionKey }).IsUnique();
            entry.HasIndex(e => new { e.TenantId, e.AuditId, e.Order }).IsUnique();

            entry.ToTable("audit_survey_answer", table => table.HasCheckConstraint(
                "ck_audit_survey_answer_order", @"""Order"" >= 1"));
        });

        modelBuilder.Entity<PhotoEntry>(entry =>
        {
            entry.HasKey(e => e.Id);

            // By name, never as an ordinal — a section inserted in the middle of the enum would
            // silently re-file every stored photo.
            entry.Property(e => e.Section).HasConversion<string>().HasMaxLength(20).IsRequired();

            entry.Property(e => e.ObjectKey)
                .HasMaxLength(PhotoEntry.MaximumObjectKeyLength).IsRequired();

            // One reference per object within an audit. The same image under two sections would be
            // one photo counted twice, with no way to say which the rep meant. Deliberately scoped to
            // the audit rather than the tenant: nothing stops two audits referencing one object, and
            // a tenant-wide constraint would be a rule about object storage this schema cannot keep.
            entry.HasIndex(e => new { e.TenantId, e.AuditId, e.ObjectKey }).IsUnique();

            entry.ToTable("audit_photo");
        });

        modelBuilder.Entity<ScoredPillar>(entry =>
        {
            entry.HasKey(e => e.Id);

            // By name, never as an ordinal — a pillar inserted in the middle of the enum would
            // silently re-interpret every stored breakdown, which is the same reason `score_weight`
            // stores it this way and the reason the two must agree.
            entry.Property(e => e.Pillar).HasConversion<string>().HasMaxLength(30).IsRequired();

            entry.Property(e => e.Percentage).HasPrecision(5, 2);
            entry.Property(e => e.Weight).HasPrecision(5, 2);

            // One row per pillar per audit. Two would leave the breakdown summing to something the
            // total cannot be, which is the one property a breakdown has to have.
            entry.HasIndex(e => new { e.TenantId, e.AuditId, e.Pillar }).IsUnique();

            entry.ToTable("audit_scored_pillar", table => table.HasCheckConstraint(
                // Nullable on purpose — null is *skipped*, which is not zero (W10 slice 0).
                "ck_audit_scored_pillar_range",
                @"(""Percentage"" IS NULL OR (""Percentage"" >= 0 AND ""Percentage"" <= 100)) AND ""Weight"" >= 0"));
        });
    }
}
