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

            audit.Navigation(a => a.Availability).HasField("_availability")
                .UsePropertyAccessMode(PropertyAccessMode.Field);
            audit.Navigation(a => a.Facings).HasField("_facings")
                .UsePropertyAccessMode(PropertyAccessMode.Field);
            audit.Navigation(a => a.Prices).HasField("_prices")
                .UsePropertyAccessMode(PropertyAccessMode.Field);

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
    }
}
