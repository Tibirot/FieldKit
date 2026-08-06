using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>The Products module's context — owns the <c>products</c> schema (schema-per-module).</summary>
public sealed class ProductsDbContext(DbContextOptions<ProductsDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext)
{
    public const string SchemaName = "products";

    protected override string Schema => SchemaName;

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Category> Categories => Set<Category>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Product>(product =>
        {
            product.ToTable("product");
            product.HasKey(p => p.Id);
            product.Property(p => p.Sku).HasMaxLength(64).IsRequired();
            product.Property(p => p.Name).HasMaxLength(200).IsRequired();
            product.HasIndex(p => new { p.TenantId, p.Sku }).IsUnique(); // SKU unique within a tenant
        });

        modelBuilder.Entity<Category>(category =>
        {
            category.ToTable("category");
            category.HasKey(c => c.Id);
            category.Property(c => c.Name).HasMaxLength(120).IsRequired();

            // Unique among siblings, not tenant-wide. "Water" under Beverages and "Water" under
            // Cleaning are two different things and both are correct; a tenant-wide constraint would
            // refuse the second and force a naming convention on a tree that already disambiguates
            // by position. `TenantId` leads because the filter is always by tenant first.
            //
            // Postgres treats NULLs as distinct in a unique index, so this does NOT constrain roots
            // (ParentId is null there) — two roots may share a name. The endpoint checks that case
            // in code; see NameTakenProblem.
            category.HasIndex(c => new { c.TenantId, c.ParentId, c.Name }).IsUnique();

            // Self-referencing, restricted, and keyed on the tenant as well as the id.
            //
            // The endpoint already checks that a parent exists and that a category with children is
            // not deleted, so this looks redundant. It is not: those checks read and then write, and
            // between the two the world can change. Create a child under X while another request
            // deletes X and both pass their checks, leaving a category whose parent is gone — an
            // orphan invisible to any tree built from parent pointers, because its root points
            // nowhere. Only the database can close that window.
            //
            // **The tenant belongs in the key.** A plain `ParentId -> Id` foreign key is
            // tenant-agnostic: it is satisfied by *any* tenant's category, so it would happily
            // accept a parent belonging to someone else. The tenant-filtered check in the endpoint
            // is what refuses that today, which means the strongest isolation guarantee in the
            // module would rest entirely on application code. Keying the relationship
            // `(TenantId, ParentId) -> (TenantId, Id)` puts it in the table, where a bug in a future
            // code path cannot get around it. Organization keys org units on the id alone; this goes
            // one better, and the same is worth doing there.
            //
            // Postgres uses MATCH SIMPLE, so a composite foreign key with any NULL column is not
            // checked at all. `ParentId` is null exactly for roots and `TenantId` never is, so roots
            // skip the constraint — which is what should happen, since a root has no parent to
            // verify.
            //
            // No navigation property: the constraint is what is wanted, not a traversal. An
            // `ICollection<Category> Children` invites callers to walk the tree entity-by-entity and
            // makes the aggregate's boundary a suggestion.
            //
            // Restrict rather than Cascade, deliberately. A cascade would delete an entire branch —
            // and every product's grouping under it — because someone removed the node above it.
            // The endpoint refuses first and says how many children are in the way; this is the
            // backstop for when something reaches the table another way.
            category.HasOne<Category>()
                .WithMany()
                .HasForeignKey(c => new { c.TenantId, c.ParentId })
                .HasPrincipalKey(c => new { c.TenantId, c.Id })
                .OnDelete(DeleteBehavior.Restrict);

            // The list endpoint reads every category for a tenant, and the delete path asks whether
            // one has children. Both are parent lookups.
            category.HasIndex(c => new { c.TenantId, c.ParentId });
        });
    }
}
