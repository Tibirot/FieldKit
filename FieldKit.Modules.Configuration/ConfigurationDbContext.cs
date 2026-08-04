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

    public DbSet<FieldDefinition> FieldDefinitions => Set<FieldDefinition>();

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
    }
}
