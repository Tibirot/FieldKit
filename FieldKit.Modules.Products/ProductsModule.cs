using FieldKit.Infrastructure;
using FieldKit.Modules.Products.Contracts;
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
/// It stopped being that proof in W6. The module now owns the catalogue and its classification,
/// assortments, price lists and prices, promotions with their targets, tiers and scope, tax rates,
/// and the three resolvers behind <c>PRD-04</c>, <c>PRD-06</c> and <c>PRD-07</c> — spread across the
/// endpoint files beside this one.
/// </para>
/// <para>
/// <b>One public contract, and it waited for a caller.</b> <c>IProductCatalog</c>,
/// <c>IAssortmentService</c> and <c>IPricingService</c> are all still absent, deliberately: their
/// consumer is Order, which is Phase 3, and W6 built the things they would wrap without ever finding
/// a caller to design them against. <c>IProductChangeFeed</c> found one in W8 slice 8c — Sync has to
/// page the catalogue to a device — so the <c>.Contracts</c> assembly landed then and not before.
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

        // Sync pages the catalogue to devices through this rather than reading the products schema
        // (W8 slice 8c) — the module's first public contract, and it waited for a caller.
        services.AddScoped<IProductChangeFeed, ProductChangeFeed>();
        services.AddScoped<IAssortmentChangeFeed, AssortmentChangeFeed>();
        services.AddScoped<IPriceChangeFeed, PriceChangeFeed>();
        services.AddScoped<IPromotionChangeFeed, PromotionChangeFeed>();
        services.AddScoped<ITaxRateChangeFeed, TaxRateChangeFeed>();

        // What an order costs, for Order — the module that cannot reach the resolvers directly
        // (AT-1) and must not reimplement them. W11 slice 2c.
        services.AddScoped<IPricingService, PricingService>();

        // …and Order asks this one whether a line may be sold here at all (W11 slice 4b).
        services.AddScoped<IAssortmentService, AssortmentService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Categories first: `/api/products/categories` must be registered before the `/{id}` shapes
        // this group will grow in slice 2, or "categories" would be routed as a product id.
        endpoints.MapCategoryEndpoints();
        endpoints.MapBrandEndpoints();
        endpoints.MapTaxClassEndpoints();
        endpoints.MapProductEndpoints();
        endpoints.MapAssortmentEndpoints();
        endpoints.MapPriceListEndpoints();
        endpoints.MapPriceListAssignmentEndpoints();
        endpoints.MapPromotionEndpoints();
        endpoints.MapPromotionAssignmentEndpoints();

        // `/api/products/outlets/...` — a literal segment, which routing prefers over the `/{id:guid}`
        // above it regardless of registration order, and which the guid constraint would reject
        // anyway. Ordered last because it reads as the last thing Products learned to do, not because
        // it has to be.
        endpoints.MapPriceResolutionEndpoints();
        endpoints.MapPromotionResolutionEndpoints();
        endpoints.MapTaxEndpoints();
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



