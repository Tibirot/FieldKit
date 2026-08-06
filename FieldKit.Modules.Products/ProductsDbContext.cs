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
    }
}
