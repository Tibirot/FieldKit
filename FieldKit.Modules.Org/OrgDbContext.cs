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
    }
}
