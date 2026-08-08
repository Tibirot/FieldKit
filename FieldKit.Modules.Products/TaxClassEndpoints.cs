using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>A tax class — the classification, not a rate.</summary>
public sealed record TaxClassResponse(Guid Id, string Name);

/// <summary>Create or rename a tax class.</summary>
public sealed record TaxClassRequest(string Name);

/// <summary>
/// The tax classifications a tenant's products fall into (<c>PRD-01</c>).
/// </summary>
/// <remarks>
/// Deliberately no rate. See <see cref="TaxClass"/> for why the percentage belongs to
/// <c>(tax class, country)</c> rather than to the class, and why it lands with tax computation in
/// slice 9 (<c>PRD-07</c>) rather than here.
/// </remarks>
internal static class TaxClassEndpoints
{
    public static void MapTaxClassEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var taxClasses = endpoints.MapGroup("/api/products/tax-classes").WithTags("Products");

        taxClasses.MapGet("/", async (ProductsDbContext db, CancellationToken ct) =>
                await db.TaxClasses
                    .OrderBy(taxClass => taxClass.Name)
                    .Select(taxClass => new TaxClassResponse(taxClass.Id, taxClass.Name))
                    .ToListAsync(ct))
            .RequirePermission(ProductsPermissions.Read);

        taxClasses.MapPost("/", async (TaxClassRequest request, ProductsDbContext db, CancellationToken ct) =>
        {
            if (NameProblem(request.Name) is { } problem) return problem;
            if (await TakenProblem(db, request.Name, excluding: null, ct) is { } taken) return taken;

            var created = TaxClass.Create(request.Name);
            db.TaxClasses.Add(created);
            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/products/tax-classes/{created.Id}", new TaxClassResponse(created.Id, created.Name));
        }).RequirePermission(ProductsPermissions.Write);

        taxClasses.MapPut("/{id:guid}", async (
            Guid id, TaxClassRequest request, ProductsDbContext db, IClock clock, CancellationToken ct) =>
        {
            if (NameProblem(request.Name) is { } problem) return problem;

            var taxClass = await db.TaxClasses.SingleOrDefaultAsync(t => t.Id == id, ct);
            if (taxClass is null) return Results.NotFound();

            if (await TakenProblem(db, request.Name, excluding: id, ct) is { } taken) return taken;

            taxClass.Rename(request.Name, clock);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new TaxClassResponse(taxClass.Id, taxClass.Name));
        }).RequirePermission(ProductsPermissions.Write);

        taxClasses.MapDelete("/{id:guid}", async (Guid id, ProductsDbContext db, CancellationToken ct) =>
        {
            var taxClass = await db.TaxClasses.SingleOrDefaultAsync(t => t.Id == id, ct);
            if (taxClass is null) return Results.NotFound();

            // As with brands, now that `Product` can reference one.
            var inUse = await db.Products.CountAsync(p => p.TaxClassId == id, ct);
            if (inUse > 0)
            {
                return Problems.Conflict(
                    field: null,
                    $"{inUse} product(s) are taxed as '{taxClass.Name}'. Reclassify them first.",
                    "product.taxClass.inUse",
                    new Dictionary<string, string>
                    {
                        ["name"] = taxClass.Name,
                        ["count"] = inUse.ToString(),
                    });
            }

            db.TaxClasses.Remove(taxClass);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }).RequirePermission(ProductsPermissions.Write);
    }

    private static IResult? NameProblem(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Problems.BadRequest("name", "A tax class needs a name.", "product.taxClass.nameRequired");
        }

        // The column, so a 121-character name is a 400 rather than a 500 from the write.
        return TextLimits.TooLong("name", name, 120, "product.taxClass.nameTooLong") is { } tooLong
            ? Problems.BadRequest([tooLong])
            : null;
    }

    private static async Task<IResult?> TakenProblem(
        ProductsDbContext db, string name, Guid? excluding, CancellationToken ct)
    {
        var taken = await db.TaxClasses.AnyAsync(
            taxClass => taxClass.Name.ToLower() == name.ToLower()
                && (excluding == null || taxClass.Id != excluding),
            ct);

        return taken
            ? Problems.Conflict(
                "name",
                $"A tax class named '{name}' already exists.",
                "product.taxClass.nameTaken",
                new Dictionary<string, string> { ["name"] = name })
            : null;
    }
}
