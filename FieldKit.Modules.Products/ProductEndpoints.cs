using System.Text.Json.Serialization;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>A product, how it is classified, and what it is. Null classification means "not classified".</summary>
public sealed record ProductResponse(
    Guid Id,
    string Sku,
    string Name,
    Guid? BrandId,
    Guid? CategoryId,
    Guid? TaxClassId,
    string? UnitOfMeasure,
    int? PackSize,
    // Serialized as its name, matching how OutletStatus and the Configuration enums cross the wire.
    // A bare enum would go out as 0 and 1, which is a number a client has to keep a private table
    // for — and which silently changes meaning if a member is ever inserted rather than appended.
    [property: JsonConverter(typeof(JsonStringEnumConverter<ProductStatus>))] ProductStatus Status);

/// <summary>Create a product.</summary>
/// <remarks>
/// The three classification ids default to null, which says in the signature what the aggregate says
/// in prose: classification is optional, and a tenant can create a product before it has built a
/// brand list or a category tree. It also keeps every existing caller — the `.http` requests, the
/// tests, any client sending `{ sku, name }` — compiling and working unchanged, since a JSON body
/// that omits them deserializes to null either way.
/// </remarks>
public sealed record CreateProductRequest(
    string Sku,
    string Name,
    Guid? BrandId = null,
    Guid? CategoryId = null,
    Guid? TaxClassId = null,
    string? UnitOfMeasure = null,
    int? PackSize = null,
    [property: JsonConverter(typeof(JsonStringEnumConverter<ProductStatus>))]
    ProductStatus Status = ProductStatus.Active);

/// <summary>Rename and reclassify a product. The SKU is not editable — see the endpoint.</summary>
/// <remarks>
/// Note that omitting a classification id here <b>clears</b> it rather than leaving it alone: this
/// is a PUT, and it replaces the product's classification with what the request describes. A form
/// that renders the current values and posts them all back — which is what the back-office screens
/// do — gets that right without thinking about it.
/// </remarks>
public sealed record UpdateProductRequest(
    string Name,
    Guid? BrandId = null,
    Guid? CategoryId = null,
    Guid? TaxClassId = null,
    string? UnitOfMeasure = null,
    int? PackSize = null,
    [property: JsonConverter(typeof(JsonStringEnumConverter<ProductStatus>))]
    ProductStatus Status = ProductStatus.Active);

/// <summary>
/// The catalogue itself (<c>PRD-01</c>).
/// </summary>
/// <remarks>
/// Moved out of <c>ProductsModule</c> when it grew validation. The module is a composition root; the
/// four endpoint files beside it are where the rules live.
/// </remarks>
internal static class ProductEndpoints
{
    public static void MapProductEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var products = endpoints.MapGroup("/api/products").WithTags("Products");

        products.MapGet("/", async (ProductsDbContext db, CancellationToken ct) =>
                await db.Products
                    .OrderBy(p => p.Sku)
                    .Select(p => new ProductResponse(
                        p.Id, p.Sku, p.Name, p.BrandId, p.CategoryId, p.TaxClassId,
                        p.UnitOfMeasure, p.PackSize, p.Status))
                    .ToListAsync(ct))
            .RequirePermission(ProductsPermissions.Read);

        products.MapPost("/", async (
            CreateProductRequest request, ProductsDbContext db, IClock clock, CancellationToken ct) =>
        {
            if (await RequestProblem(db, request.Sku, request.Name, Classification(request), Attributes(request), ct) is { } problem)
            {
                return problem;
            }

            var product = Product.Create(
                request.Sku, request.Name, Classification(request), Attributes(request), clock);
            db.Products.Add(product);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/products/{product.Id}", Respond(product));
        }).RequirePermission(ProductsPermissions.Write);

        products.MapPut("/{id:guid}", async (
            Guid id, UpdateProductRequest request, ProductsDbContext db, IClock clock, CancellationToken ct) =>
        {
            var product = await db.Products.SingleOrDefaultAsync(p => p.Id == id, ct);
            if (product is null) return Results.NotFound();

            // The SKU is deliberately not editable. It is how a tenant's own systems, its price
            // files and its trading partners identify the product — changing it is not a rename, it
            // is a different product, and doing it in place would silently rewrite the identity that
            // every order line already placed against it refers to.
            if (await RequestProblem(db, null, request.Name, Classification(request), Attributes(request), ct) is { } problem)
            {
                return problem;
            }

            product.Update(request.Name, Classification(request), Attributes(request), clock);
            await db.SaveChangesAsync(ct);

            return Results.Ok(Respond(product));
        }).RequirePermission(ProductsPermissions.Write);
    }

    private static ProductClassification Classification(CreateProductRequest request) =>
        new(request.BrandId, request.CategoryId, request.TaxClassId);

    private static ProductClassification Classification(UpdateProductRequest request) =>
        new(request.BrandId, request.CategoryId, request.TaxClassId);

    private static ProductAttributes Attributes(CreateProductRequest request) =>
        new(request.UnitOfMeasure, request.PackSize, request.Status);

    private static ProductAttributes Attributes(UpdateProductRequest request) =>
        new(request.UnitOfMeasure, request.PackSize, request.Status);

    private static ProductResponse Respond(Product product) =>
        new(
            product.Id, product.Sku, product.Name, product.BrandId, product.CategoryId,
            product.TaxClassId, product.UnitOfMeasure, product.PackSize, product.Status);

    /// <summary>
    /// Everything wrong with the request, or null.
    /// </summary>
    /// <remarks>
    /// All of the problems, not the first — a form with a bad SKU and two unknown classification ids
    /// should be able to fix them in one pass rather than three round trips
    /// (<see cref="Problems.BadRequest(IReadOnlyList{FieldProblem})"/>).
    /// <para>
    /// <paramref name="sku"/> is null on update, where the SKU is not editable and therefore not
    /// validated.
    /// </para>
    /// </remarks>
    private static async Task<IResult?> RequestProblem(
        ProductsDbContext db,
        string? sku,
        string name,
        ProductClassification classification,
        ProductAttributes attributes,
        CancellationToken ct)
    {
        var problems = new List<FieldProblem>();

        if (sku is not null && string.IsNullOrWhiteSpace(sku))
        {
            problems.Add(new FieldProblem("sku", "A product needs an SKU.", "product.sku.required"));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add(new FieldProblem("name", "A product needs a name.", "product.name.required"));
        }

        if (sku is not null && !string.IsNullOrWhiteSpace(sku)
            && await db.Products.AnyAsync(p => p.Sku.ToLower() == sku.ToLower(), ct))
        {
            problems.Add(new FieldProblem(
                "sku",
                $"A product with SKU '{sku}' already exists.",
                "product.sku.taken",
                new Dictionary<string, string> { ["sku"] = sku }));
        }

        // Each of the three checked against its own table, tenant-filtered — so an id belonging to
        // another tenant reads as "does not exist", which is the only answer that does not confirm
        // it is real somewhere else. The composite foreign keys enforce the same rule at the table;
        // these exist to say which field was wrong and why.
        if (classification.BrandId is { } brandId
            && !await db.Brands.AnyAsync(b => b.Id == brandId, ct))
        {
            problems.Add(new FieldProblem("brandId", "That brand does not exist.", "product.brand.missing"));
        }

        if (classification.CategoryId is { } categoryId
            && !await db.Categories.AnyAsync(c => c.Id == categoryId, ct))
        {
            problems.Add(new FieldProblem("categoryId", "That category does not exist.", "product.category.missing"));
        }

        if (classification.TaxClassId is { } taxClassId
            && !await db.TaxClasses.AnyAsync(t => t.Id == taxClassId, ct))
        {
            problems.Add(new FieldProblem("taxClassId", "That tax class does not exist.", "product.taxClass.missing"));
        }

        // Zero and negatives are refused rather than normalised to null. A pack of zero is not "no
        // pack size", it is a number someone got wrong — and quietly turning it into null would let
        // a bad import look like a deliberate omission.
        if (attributes.PackSize is { } packSize && packSize < 1)
        {
            problems.Add(new FieldProblem(
                "packSize",
                "A pack size is at least 1, or absent.",
                "product.packSize.notPositive",
                new Dictionary<string, string> { ["packSize"] = packSize.ToString() }));
        }

        if (attributes.UnitOfMeasure is { Length: > 16 })
        {
            problems.Add(new FieldProblem(
                "unitOfMeasure",
                "A unit of measure is at most 16 characters.",
                "product.unitOfMeasure.tooLong"));
        }

        return problems.Count > 0 ? Problems.BadRequest(problems) : null;
    }
}
