using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>A brand.</summary>
public sealed record BrandResponse(Guid Id, string Name);

/// <summary>Create or rename a brand.</summary>
public sealed record BrandRequest(string Name);

/// <summary>
/// The brands a tenant sells (<c>PRD-01</c>).
/// </summary>
/// <remarks>
/// Structurally identical to <see cref="TaxClassEndpoints"/>, and to <c>ChannelEndpoints</c> in
/// Outlets. Written out rather than abstracted: the shared shape is four handlers of five lines
/// each, and a generic "named vocabulary" helper would hide the two things that actually differ
/// between them — the permission each requires and the refusal codes each emits — behind type
/// parameters. Worth revisiting if a third vocabulary lands in this module; two is not a pattern.
/// </remarks>
internal static class BrandEndpoints
{
    public static void MapBrandEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var brands = endpoints.MapGroup("/api/products/brands").WithTags("Products");

        brands.MapGet("/", async (ProductsDbContext db, CancellationToken ct) =>
                await db.Brands
                    .OrderBy(brand => brand.Name)
                    .Select(brand => new BrandResponse(brand.Id, brand.Name))
                    .ToListAsync(ct))
            .RequirePermission(ProductsPermissions.Read);

        brands.MapPost("/", async (BrandRequest request, ProductsDbContext db, CancellationToken ct) =>
        {
            if (NameProblem(request.Name) is { } problem) return problem;
            if (await TakenProblem(db, request.Name, excluding: null, ct) is { } taken) return taken;

            var created = Brand.Create(request.Name);
            db.Brands.Add(created);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/products/brands/{created.Id}", new BrandResponse(created.Id, created.Name));
        }).RequirePermission(ProductsPermissions.Write);

        brands.MapPut("/{id:guid}", async (
            Guid id, BrandRequest request, ProductsDbContext db, IClock clock, CancellationToken ct) =>
        {
            if (NameProblem(request.Name) is { } problem) return problem;

            var brand = await db.Brands.SingleOrDefaultAsync(b => b.Id == id, ct);
            if (brand is null) return Results.NotFound();

            if (await TakenProblem(db, request.Name, excluding: id, ct) is { } taken) return taken;

            // Renaming is safe in a way deleting is not: everything that scopes to a brand scopes to
            // its id, so the label can change without a promotion silently stopping matching.
            brand.Rename(request.Name, clock);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new BrandResponse(brand.Id, brand.Name));
        }).RequirePermission(ProductsPermissions.Write);

        brands.MapDelete("/{id:guid}", async (Guid id, ProductsDbContext db, CancellationToken ct) =>
        {
            var brand = await db.Brands.SingleOrDefaultAsync(b => b.Id == id, ct);
            if (brand is null) return Results.NotFound();

            // No in-use check, because nothing can be using it yet: `Product` gains `BrandId` in
            // slice 2. That slice must add the guard here — and the FK it adds will refuse the
            // delete anyway, so the failure mode if it is forgotten is a raw constraint violation
            // rather than lost data.
            db.Brands.Remove(brand);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }).RequirePermission(ProductsPermissions.Write);
    }

    private static IResult? NameProblem(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? Problems.BadRequest("name", "A brand needs a name.", "product.brand.nameRequired")
            : null;

    private static async Task<IResult?> TakenProblem(
        ProductsDbContext db, string name, Guid? excluding, CancellationToken ct)
    {
        var taken = await db.Brands.AnyAsync(
            brand => brand.Name.ToLower() == name.ToLower() && (excluding == null || brand.Id != excluding),
            ct);

        return taken
            ? Problems.Conflict(
                "name",
                $"A brand named '{name}' already exists.",
                "product.brand.nameTaken",
                new Dictionary<string, string> { ["name"] = name })
            : null;
    }
}
