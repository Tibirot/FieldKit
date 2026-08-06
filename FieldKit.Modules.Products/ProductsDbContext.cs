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

            // No navigation property and no configured FK. The parent pointer is a plain Guid?, so
            // EF has no relationship to cascade: deleting a category with children is refused by the
            // endpoint with an explanation, rather than silently orphaning or cascading them away.
            category.HasIndex(c => new { c.TenantId, c.ParentId });
        });
    }
}
