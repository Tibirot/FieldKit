using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FieldKit.Modules.Products;

/// <summary>
/// Lets <c>dotnet ef</c> build the model to generate migrations. The connection string is a
/// placeholder — <c>migrations add</c> does not connect to a database.
/// </summary>
public sealed class ProductsDbContextFactory : IDesignTimeDbContextFactory<ProductsDbContext>
{
    public ProductsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ProductsDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=fieldkit_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", ProductsDbContext.SchemaName))
            .Options;

        return new ProductsDbContext(options, new DesignTimeTenantContext());
    }

    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public TenantId TenantId => default;
        public string UserId => "design-time";
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public bool Has(string permission) => false;
    }
}
