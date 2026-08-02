using FieldKit.Infrastructure;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Modules.Catalog;

/// <summary>The Catalog module: registers its context and maps its slice of <c>/api</c>.</summary>
public sealed class CatalogModule : IModule
{
    public string Name => "Catalog";

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("fieldkitdb")
            ?? throw new InvalidOperationException("Connection string 'fieldkitdb' is not configured.");

        services.AddModuleDbContext<CatalogDbContext>(connectionString, CatalogDbContext.SchemaName);
        services.AddHostedService<ModuleMigrator<CatalogDbContext>>(); // applies this module's EF migrations on startup
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var products = endpoints.MapGroup("/api/products").WithTags("Catalog");

        products.MapPost("/", async (
            CreateProductRequest request, CatalogDbContext db, IClock clock, CancellationToken cancellationToken) =>
        {
            var product = Product.Create(request.Sku, request.Name, clock);
            db.Add(product);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created(
                $"/api/products/{product.Id}",
                new ProductResponse(product.Id, product.Sku, product.Name));
        });

        products.MapGet("/", async (CatalogDbContext db, CancellationToken cancellationToken) =>
        {
            var all = await db.Products
                .OrderBy(p => p.Sku)
                .Select(p => new ProductResponse(p.Id, p.Sku, p.Name))
                .ToListAsync(cancellationToken);
            return Results.Ok(all);
        });
    }
}

public sealed record CreateProductRequest(string Sku, string Name);
public sealed record ProductResponse(Guid Id, string Sku, string Name);
