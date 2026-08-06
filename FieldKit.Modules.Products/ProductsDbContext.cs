using System.Text.Json;
using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace FieldKit.Modules.Products;

/// <summary>The Products module's context — owns the <c>products</c> schema (schema-per-module).</summary>
public sealed class ProductsDbContext(DbContextOptions<ProductsDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext)
{
    public const string SchemaName = "products";

    protected override string Schema => SchemaName;

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Brand> Brands => Set<Brand>();

    public DbSet<TaxClass> TaxClasses => Set<TaxClass>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Product>(product =>
        {
            product.ToTable("product");
            product.HasKey(p => p.Id);
            product.Property(p => p.Sku).HasMaxLength(64).IsRequired();
            product.Property(p => p.Name).HasMaxLength(200).IsRequired();
            product.Property(p => p.UnitOfMeasure).HasMaxLength(16);

            // Stored as its integer value, not its name: a string column would turn renaming an enum
            // member into a data migration. The wire is the other way round — `ProductResponse`
            // carries a `JsonStringEnumConverter`, so clients see "Active" rather than 0 and never
            // depend on the ordinal this column keeps.
            //
            // Which means the ordinals are now storage, and members must be *appended* rather than
            // inserted. Adding a status between Active and Discontinued would renumber every stored
            // row's meaning without touching a single one of them.
            product.Property(p => p.Status).HasConversion<int>();

            // jsonb, and stored as a dictionary of raw JSON elements — what is inside is the
            // tenant's business, described by the Configuration catalogue rather than by this model.
            // The same shape Outlets uses, deliberately: two entities carrying tenant-defined fields
            // should store them the same way, or the sync engine has two problems instead of one.
            product.Property(p => p.CustomFields)
                .HasColumnName("custom_fields")
                .HasColumnType("jsonb")
                .HasConversion(
                    fields => JsonSerializer.Serialize(fields, (JsonSerializerOptions?)null),
                    json => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, (JsonSerializerOptions?)null)!,
                    new ValueComparer<IReadOnlyDictionary<string, JsonElement>>(
                        // Compared by serialized form: JsonElement has no value equality, so without
                        // this EF would compare references and miss every edit to a custom field.
                        (left, right) => JsonSerializer.Serialize(left, (JsonSerializerOptions?)null)
                            == JsonSerializer.Serialize(right, (JsonSerializerOptions?)null),
                        fields => JsonSerializer.Serialize(fields, (JsonSerializerOptions?)null).GetHashCode(),
                        fields => fields));
            product.HasIndex(p => new { p.TenantId, p.Sku }).IsUnique(); // SKU unique within a tenant

            // The three classification pointers, each keyed on the tenant as well as the id — the
            // pattern established for Category's parent and since applied to OrgUnit. A plain
            // `BrandId -> Id` key is tenant-agnostic and would accept another tenant's brand; with
            // the tenant in the key the rule is in the table rather than only in the endpoint.
            //
            // Restrict, so a vocabulary entry cannot be deleted out from under the products using
            // it. The endpoints refuse first with a count and a code; this is what catches anything
            // that reaches the table another way.
            //
            // Postgres MATCH SIMPLE means a composite key with any NULL column is not checked, so an
            // unclassified product — all three null — skips all three constraints. That is the
            // behaviour the optional classification needs, and it falls out rather than being
            // arranged.
            product.HasOne<Brand>()
                .WithMany()
                .HasForeignKey(p => new { p.TenantId, p.BrandId })
                .HasPrincipalKey(b => new { b.TenantId, b.Id })
                .OnDelete(DeleteBehavior.Restrict);

            product.HasOne<Category>()
                .WithMany()
                .HasForeignKey(p => new { p.TenantId, p.CategoryId })
                .HasPrincipalKey(c => new { c.TenantId, c.Id })
                .OnDelete(DeleteBehavior.Restrict);

            product.HasOne<TaxClass>()
                .WithMany()
                .HasForeignKey(p => new { p.TenantId, p.TaxClassId })
                .HasPrincipalKey(t => new { t.TenantId, t.Id })
                .OnDelete(DeleteBehavior.Restrict);
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

        // Brand and TaxClass are flat named vocabularies with the same shape, so they are configured
        // the same way: unique on (TenantId, Name), which — unlike Category's sibling rule — has no
        // nullable column in it and therefore needs no in-code companion check. Postgres's
        // NULL-distinctness only bites when a key column can be null.
        modelBuilder.Entity<Brand>(brand =>
        {
            brand.ToTable("brand");
            brand.HasKey(b => b.Id);
            brand.Property(b => b.Name).HasMaxLength(120).IsRequired();
            brand.HasIndex(b => new { b.TenantId, b.Name }).IsUnique();
        });

        modelBuilder.Entity<TaxClass>(taxClass =>
        {
            taxClass.ToTable("tax_class");
            taxClass.HasKey(t => t.Id);
            taxClass.Property(t => t.Name).HasMaxLength(120).IsRequired();
            taxClass.HasIndex(t => new { t.TenantId, t.Name }).IsUnique();
        });
    }
}
