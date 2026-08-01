using System.Linq.Expressions;
using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Infrastructure;

/// <summary>
/// Base <see cref="DbContext"/> for a module. Pins the module's own Postgres schema
/// (schema-per-module, ADR-0005) and applies the tenant global query filter to every
/// <see cref="ITenantOwned"/> entity (ADR-0008) — so isolation is automatic, not per-query.
/// Each module derives exactly one context and maps only its own tables.
/// </summary>
public abstract class ModuleDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    protected ModuleDbContext(DbContextOptions options, ITenantContext tenantContext) : base(options)
        => _tenantContext = tenantContext;

    /// <summary>The Postgres schema this module owns (e.g. "outlets").</summary>
    protected abstract string Schema { get; }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Strongly-typed ids are stored as their underlying primitive across every module.
        configurationBuilder.Properties<TenantId>().HaveConversion<TenantIdValueConverter>();
        base.ConfigureConventions(configurationBuilder);
    }

    /// <summary>
    /// Referenced by the tenant query filter. EF Core parameterises DbContext-instance members in a
    /// filter, so the cached model still evaluates the <em>current</em> tenant per query.
    /// </summary>
    public TenantId CurrentTenantId => _tenantContext.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        base.OnModelCreating(modelBuilder);
        ApplyTenantQueryFilter(modelBuilder);
    }

    private void ApplyTenantQueryFilter(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
                continue;

            // Build: (TEntity e) => e.TenantId == this.CurrentTenantId
            var entity = Expression.Parameter(entityType.ClrType, "e");
            var entityTenant = Expression.Property(entity, nameof(ITenantOwned.TenantId));
            var currentTenant = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));
            var predicate = Expression.Lambda(Expression.Equal(entityTenant, currentTenant), entity);

            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(predicate);
        }
    }
}
