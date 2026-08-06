using FieldKit.Infrastructure;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Modules.Products;

/// <summary>The Products &amp; Pricing module: registers its context and maps its slice of <c>/api</c>.</summary>
/// <remarks>
/// This module began life as <c>Catalog</c> — the W1 proof that the modular monolith runs, written to
/// replace the Aspire template's <c>WeatherForecast</c>. It was always scaffolding, and it was
/// standing on the ground the registry reserves for Products &amp; Pricing: same <c>/api/products</c>
/// route, same <c>product:read</c> / <c>product:write</c> permissions. Two <see cref="IModule"/>s
/// cannot declare the same permission, so W6 would have collided with it on day one. Renamed ahead
/// of that work rather than during it — see
/// <c>docs/architecture/10-module-boundaries.md</c> §7.
/// <para>
/// What is here today is still that proof and nothing more: a product is an SKU and a name.
/// Assortments, price lists, promotions and the <c>IProductCatalog</c> / <c>IAssortmentService</c> /
/// <c>IPricingService</c> contracts arrive in W6 (<c>PRD-*</c>, <c>PRC-*</c>).
/// </para>
/// </remarks>
public sealed class ProductsModule : IModule
{
    public string Name => "Products";

    public IReadOnlyList<PermissionDefinition> Permissions =>
    [
        new(ProductsPermissions.Read, "View products and their details."),
        new(ProductsPermissions.Write, "Create and modify products."),
    ];

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("fieldkitdb")
            ?? throw new InvalidOperationException("Connection string 'fieldkitdb' is not configured.");

        services.AddModuleDbContext<ProductsDbContext>(connectionString, ProductsDbContext.SchemaName);
        services.AddHostedService<ModuleMigrator<ProductsDbContext>>(); // applies this module's EF migrations on startup
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Categories first: `/api/products/categories` must be registered before the `/{id}` shapes
        // this group will grow in slice 2, or "categories" would be routed as a product id.
        endpoints.MapCategoryEndpoints();

        var products = endpoints.MapGroup("/api/products").WithTags("Products");

        products.MapPost("/", async (
            CreateProductRequest request, ProductsDbContext db, IClock clock, CancellationToken cancellationToken) =>
        {
            var product = Product.Create(request.Sku, request.Name, clock);
            db.Add(product);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created(
                $"/api/products/{product.Id}",
                new ProductResponse(product.Id, product.Sku, product.Name));
        }).RequirePermission(ProductsPermissions.Write);

        products.MapGet("/", async (ProductsDbContext db, CancellationToken cancellationToken) =>
        {
            var all = await db.Products
                .OrderBy(p => p.Sku)
                .Select(p => new ProductResponse(p.Id, p.Sku, p.Name))
                .ToListAsync(cancellationToken);
            return Results.Ok(all);
        }).RequirePermission(ProductsPermissions.Read);
    }
}

/// <summary>
/// The permissions this module owns, as <c>resource:action</c> strings.
/// </summary>
/// <remarks>
/// Constants rather than literals so a rename is a compile error rather than a silently open
/// endpoint. The strings themselves are <b>deliberately unchanged</b> by the Catalog retirement:
/// they are named after the resource, not the module that happened to introduce them, and they are
/// already spelled out in <c>SystemRoleTemplates</c>, in both dev realms, and in whatever roles a
/// tenant has since composed. Renaming the class is free; renaming the permission is a migration.
/// </remarks>
public static class ProductsPermissions
{
    public const string Read = "product:read";
    public const string Write = "product:write";
}

public sealed record CreateProductRequest(string Sku, string Name);
public sealed record ProductResponse(Guid Id, string Sku, string Name);
