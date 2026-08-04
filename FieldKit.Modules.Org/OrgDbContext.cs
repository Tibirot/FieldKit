using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Org;

/// <summary>The Organization module's context — owns the <c>org</c> schema (schema-per-module, ADR-0005).</summary>
public sealed class OrgDbContext(DbContextOptions<OrgDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext)
{
    public const string SchemaName = "org";

    protected override string Schema => SchemaName;

    public DbSet<OrgUnit> OrgUnits => Set<OrgUnit>();

    public DbSet<Position> Positions => Set<Position>();

    public DbSet<Territory> Territories => Set<Territory>();

    public DbSet<TerritoryOutlet> TerritoryOutlets => Set<TerritoryOutlet>();

    public DbSet<RepAssignment> RepAssignments => Set<RepAssignment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<OrgUnit>(unit =>
        {
            unit.ToTable("org_unit");
            unit.HasKey(u => u.Id);
            unit.Property(u => u.Name).HasMaxLength(200).IsRequired();

            // Unique among siblings, not tenant-wide. "North" is a perfectly good name for a team
            // under Romania and another under Poland, and a tenant-wide constraint would force
            // every leaf to carry its ancestry in its own name.
            //
            // Postgres treats NULLs as distinct in a unique index, so this does not constrain roots
            // — two roots may share a name. Accepted: the alternative is a filtered index plus a
            // second one for roots, to protect a case (many same-named roots) that is already
            // visible on the one screen that lists them.
            unit.HasIndex(u => new { u.TenantId, u.ParentId, u.Name }).IsUnique();

            // Self-referencing, restricted. A cascade here would delete a whole branch because
            // someone removed the node above it; the endpoint refuses instead and says why.
            unit.HasOne<OrgUnit>()
                .WithMany()
                .HasForeignKey(u => u.ParentId)
                .OnDelete(DeleteBehavior.Restrict);

            // The tree endpoint reads every unit for a tenant, and the delete path asks whether a
            // unit has children. Both are parent lookups.
            unit.HasIndex(u => new { u.TenantId, u.ParentId });
        });

        modelBuilder.Entity<Position>(position =>
        {
            position.ToTable("position");
            position.HasKey(p => p.Id);
            position.Property(p => p.UserId).HasMaxLength(64).IsRequired();   // Keycloak `sub`
            position.Property(p => p.Title).HasMaxLength(100).IsRequired();

            // One position per user per unit. Holding two places in the same unit says nothing the
            // title cannot, and it would double every unit in that user's scope.
            //
            // Across units is deliberately allowed: covering two areas is an ordinary arrangement,
            // and the scope calculation is a set union precisely so it stays correct when it happens.
            position.HasIndex(p => new { p.TenantId, p.UserId, p.OrgUnitId }).IsUnique();

            // Restricted, like the unit's own parent link. The endpoint checks first and refuses
            // with "N people still hold positions here" — this is what makes that a guarantee rather
            // than a convention: a path that forgets to check gets a constraint violation instead of
            // an orphaned position pointing at a unit that no longer exists.
            position.HasOne<OrgUnit>()
                .WithMany()
                .HasForeignKey(p => p.OrgUnitId)
                .OnDelete(DeleteBehavior.Restrict);

            // "Who is in this unit", which the delete check and the unit screen both ask.
            position.HasIndex(p => new { p.TenantId, p.OrgUnitId });
        });

        modelBuilder.Entity<Territory>(territory =>
        {
            territory.ToTable("territory");
            territory.HasKey(t => t.Id);
            territory.Property(t => t.Name).HasMaxLength(200).IsRequired();
            territory.HasIndex(t => new { t.TenantId, t.Name }).IsUnique();

            // Restricted like everything else that hangs off a unit: deleting a region should not
            // take its territories — and the outlets in them — with it.
            territory.HasOne<OrgUnit>()
                .WithMany()
                .HasForeignKey(t => t.OrgUnitId)
                .OnDelete(DeleteBehavior.Restrict);

            territory.HasIndex(t => new { t.TenantId, t.OrgUnitId });
        });

        modelBuilder.Entity<TerritoryOutlet>(membership =>
        {
            membership.ToTable("territory_outlet");
            membership.HasKey(m => m.Id);

            // BR-ORG-1 and ORG-05, as a fact about the table rather than a rule on every write path:
            // one row per outlet means an outlet cannot be in two territories, including via a bulk
            // import that forgets to check.
            membership.HasIndex(m => new { m.TenantId, m.OutletId }).IsUnique();

            // Cascade only from the territory — the membership has no meaning without it, and
            // deleting a territory is already refused while it has outlets, so this is the safety
            // net rather than the path. No foreign key to the outlet: different schema, different
            // module (ADR-0005), validated through IOutletCatalog instead.
            membership.HasOne<Territory>()
                .WithMany()
                .HasForeignKey(m => m.TerritoryId)
                .OnDelete(DeleteBehavior.Cascade);

            membership.HasIndex(m => new { m.TenantId, m.TerritoryId });
        });

        modelBuilder.Entity<RepAssignment>(assignment =>
        {
            assignment.ToTable("rep_assignment");
            assignment.HasKey(a => a.Id);
            assignment.Property(a => a.UserId).HasMaxLength(64).IsRequired(); // Keycloak `sub`
            assignment.Property(a => a.FromDate).HasColumnName("from_date").IsRequired();
            assignment.Property(a => a.ToDate).HasColumnName("to_date");

            // Composed by the entity, not mapped — see RepAssignment.Period.
            assignment.Ignore(a => a.Period);

            // The half of the range invariant a database can hold. BR-ORG-2's no-overlap rule cannot
            // be expressed this way without an exclusion constraint over a range type, so it is
            // enforced in the endpoint and tested there; this at least makes a backwards range
            // impossible for anything that writes the table.
            assignment.ToTable(table => table.HasCheckConstraint(
                "ck_rep_assignment_period", @"""to_date"" IS NULL OR ""to_date"" >= ""from_date"""));

            assignment.HasOne<Territory>()
                .WithMany()
                .HasForeignKey(a => a.TerritoryId)
                .OnDelete(DeleteBehavior.Restrict);

            // "What covers this territory, and when" — the overlap check's query.
            assignment.HasIndex(a => new { a.TenantId, a.TerritoryId, a.FromDate });

            // "What does this rep cover" — BR-ORG-3's offline scope, which Sync will ask on every pull.
            assignment.HasIndex(a => new { a.TenantId, a.UserId });
        });
    }
}
