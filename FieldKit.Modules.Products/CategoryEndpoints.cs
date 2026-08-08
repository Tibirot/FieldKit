using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Products;

/// <summary>A category, flat. The tree is expressed by <paramref name="ParentId"/>, not by nesting.</summary>
/// <remarks>
/// Flat because a nested response makes the client's job harder, not easier: a table wants rows, and
/// a tree view can build its own nesting from parent pointers in one pass. It also keeps the shape
/// stable as the tree deepens.
/// </remarks>
public sealed record CategoryResponse(Guid Id, string Name, Guid? ParentId);

/// <summary>Create or update a category. <paramref name="ParentId"/> null means a root.</summary>
public sealed record CategoryRequest(string Name, Guid? ParentId = null);

/// <summary>
/// The product classification tree a tenant works with (<c>PRD-01</c>).
/// </summary>
/// <remarks>
/// Guarded by <c>product:read</c>/<c>product:write</c> rather than its own permission pair. A
/// category is not a thing anyone administers separately from the products in it — whoever may
/// reshape the catalogue may reshape how it is grouped, and a `category:write` nobody ever grants
/// independently is a permission that exists to be listed.
/// <para>
/// <b>These are the first refusals in the codebase to carry ADR-0012 codes</b>, so the naming
/// convention starts here: <c>product.category.&lt;what&gt;</c> — resource path first, condition
/// last, mirroring the <c>resource:action</c> shape of permission strings.
/// </para>
/// </remarks>
internal static class CategoryEndpoints
{
    public static void MapCategoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var categories = endpoints.MapGroup("/api/products/categories").WithTags("Products");

        categories.MapGet("/", async (ProductsDbContext db, CancellationToken ct) =>
                await db.Categories
                    .OrderBy(category => category.Name)
                    .Select(category => new CategoryResponse(category.Id, category.Name, category.ParentId))
                    .ToListAsync(ct))
            .RequirePermission(ProductsPermissions.Read);

        categories.MapPost("/", async (CategoryRequest request, ProductsDbContext db, CancellationToken ct) =>
        {
            if (NameProblem(request.Name) is { } nameProblem) return nameProblem;
            if (await ParentProblem(db, request.ParentId, ct) is { } parentProblem) return parentProblem;
            if (await NameTakenProblem(db, request, excluding: null, ct) is { } taken) return taken;

            var created = Category.Create(request.Name, request.ParentId);
            db.Categories.Add(created);
            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/products/categories/{created.Id}",
                new CategoryResponse(created.Id, created.Name, created.ParentId));
        }).RequirePermission(ProductsPermissions.Write);

        categories.MapPut("/{id:guid}", async (
            Guid id, CategoryRequest request, ProductsDbContext db, IClock clock, CancellationToken ct) =>
        {
            if (NameProblem(request.Name) is { } nameProblem) return nameProblem;

            var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == id, ct);
            if (category is null) return Results.NotFound();

            // Checked before the general cycle walk because it is the one case the walk cannot see:
            // a category pointed at itself never enters the ancestor loop.
            if (request.ParentId == id)
            {
                return Problems.Conflict(
                    "parentId",
                    "A category cannot be its own parent.",
                    "product.category.ownParent");
            }

            if (await ParentProblem(db, request.ParentId, ct) is { } parentProblem) return parentProblem;
            if (await NameTakenProblem(db, request, excluding: id, ct) is { } taken) return taken;

            if (category.ParentId != request.ParentId
                && await CycleProblem(db, id, request.ParentId, ct) is { } cycle)
            {
                return cycle;
            }

            category.Rename(request.Name, clock);
            category.MoveTo(request.ParentId, clock);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new CategoryResponse(category.Id, category.Name, category.ParentId));
        }).RequirePermission(ProductsPermissions.Write);

        categories.MapDelete("/{id:guid}", async (Guid id, ProductsDbContext db, CancellationToken ct) =>
        {
            var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == id, ct);
            if (category is null) return Results.NotFound();

            // Nothing cascades here on purpose. Deleting a branch of a classification tree is a
            // large, quiet act — every product under it loses its grouping — so the answer is an
            // explanation naming the count, not a silent recursive delete.
            var children = await db.Categories.CountAsync(c => c.ParentId == id, ct);
            if (children > 0)
            {
                return Problems.Conflict(
                    // No one field is at fault — the request was just an id — so `field` is null and
                    // a form shows this at the top rather than beside a control.
                    field: null,
                    $"'{category.Name}' still has {children} child categor" + (children == 1 ? "y." : "ies."),
                    "product.category.hasChildren",
                    new Dictionary<string, string>
                    {
                        ["name"] = category.Name,
                        ["count"] = children.ToString(),
                    });
            }

            // And now that products can be filed under a category, the same refusal for them.
            // Checked after children so the message names the more structural obstacle first: an
            // admin clearing a branch has to deal with the sub-categories regardless, and being told
            // about products under a category they cannot delete yet is noise.
            var filed = await db.Products.CountAsync(p => p.CategoryId == id, ct);
            if (filed > 0)
            {
                return Problems.Conflict(
                    field: null,
                    $"{filed} product(s) are filed under '{category.Name}'. Reclassify them first.",
                    "product.category.inUse",
                    new Dictionary<string, string>
                    {
                        ["name"] = category.Name,
                        ["count"] = filed.ToString(),
                    });
            }

            db.Categories.Remove(category);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }).RequirePermission(ProductsPermissions.Write);
    }

    private static IResult? NameProblem(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Problems.BadRequest("name", "A category needs a name.", "product.category.nameRequired");
        }

        // The column, so a 121-character name is a 400 rather than a 500 from the write.
        return TextLimits.TooLong("name", name, 120, "product.category.nameTooLong") is { } tooLong
            ? Problems.BadRequest([tooLong])
            : null;
    }

    /// <summary>Rejects a parent that does not exist.</summary>
    /// <remarks>
    /// The query is tenant-filtered like every other, so a parent id belonging to another tenant
    /// reads as "does not exist" — which is the honest answer to give, and the only one that does
    /// not confirm the id is real somewhere else.
    /// </remarks>
    private static async Task<IResult?> ParentProblem(
        ProductsDbContext db, Guid? parentId, CancellationToken ct)
    {
        if (parentId is not { } id) return null;

        return await db.Categories.AnyAsync(category => category.Id == id, ct)
            ? null
            : Problems.BadRequest(
                "parentId", "The parent category does not exist.", "product.category.parentMissing");
    }

    /// <summary>Rejects a name already taken by a sibling.</summary>
    /// <remarks>
    /// Checked in code as well as by the unique index, because the index cannot cover roots: Postgres
    /// treats NULLs as distinct, so two roots named "Beverages" would slip past it. This makes the
    /// rule uniform whatever the depth, and turns what would otherwise surface as a raw constraint
    /// violation into an answer an admin can act on.
    /// </remarks>
    private static async Task<IResult?> NameTakenProblem(
        ProductsDbContext db, CategoryRequest request, Guid? excluding, CancellationToken ct)
    {
        var taken = await db.Categories.AnyAsync(
            category => category.ParentId == request.ParentId
                && category.Name.ToLower() == request.Name.ToLower()
                && (excluding == null || category.Id != excluding),
            ct);

        return taken
            ? Problems.Conflict(
                "name",
                $"A sibling category is already named '{request.Name}'.",
                "product.category.nameTaken",
                new Dictionary<string, string> { ["name"] = request.Name })
            : null;
    }

    /// <summary>Rejects a move that would put a category inside its own subtree.</summary>
    /// <remarks>
    /// Loads the tenant's parent pointers rather than walking the tree one query at a time: the set
    /// is small — a classification tree is authored by hand and read constantly — and the
    /// alternative is a round trip per level of depth to answer one question.
    /// </remarks>
    private static async Task<IResult?> CycleProblem(
        ProductsDbContext db, Guid id, Guid? newParentId, CancellationToken ct)
    {
        var parentOf = await db.Categories.ToDictionaryAsync(c => c.Id, c => c.ParentId, ct);

        return CategoryHierarchy.WouldCreateCycle(id, newParentId, parentOf)
            ? Problems.Conflict(
                "parentId",
                "That move would put the category inside its own branch.",
                "product.category.cycle")
            : null;
    }
}
