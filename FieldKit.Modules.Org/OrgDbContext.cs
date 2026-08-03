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
    }
}
