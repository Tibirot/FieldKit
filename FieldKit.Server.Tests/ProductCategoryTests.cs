using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Products;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// The product classification tree (<c>PRD-01</c>) — W6 slice 1.
/// </summary>
/// <remarks>
/// These are also the first tests in the suite to assert an <c>ADR-0012</c> refusal <c>code</c>.
/// Asserting the code rather than the prose is the point of the ADR: the sentence is an English
/// fallback that a translator may reword, while the code is API surface a client branches on.
/// </remarks>
[Collection(ServerCollection.Name)]
public class ProductCategoryTests(ServerFixture fixture)
{
    private const string Categories = "/api/products/categories";

    private static CategoryRequest Named(string name, Guid? parent = null) => new(name, parent);

    /// <summary>Creates a category and returns it, failing loudly if the API refused.</summary>
    private static async Task<CategoryResponse> CreateAsync(HttpClient client, string name, Guid? parent = null)
    {
        var response = await client.PostAsJsonAsync(Categories, Named(name, parent));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CategoryResponse>())!;
    }

    [Fact]
    public async Task A_tree_is_built_from_parent_pointers()
    {
        using var client = fixture.CreateAuthenticatedClient();

        var beverages = await CreateAsync(client, $"Beverages {Guid.NewGuid():N}");
        var water = await CreateAsync(client, "Water", beverages.Id);

        Assert.Null(beverages.ParentId);
        Assert.Equal(beverages.Id, water.ParentId);

        var all = await client.GetFromJsonAsync<List<CategoryResponse>>(Categories);
        Assert.Contains(all!, c => c.Id == water.Id && c.ParentId == beverages.Id);
    }

    [Fact]
    public async Task A_category_needs_a_name()
    {
        using var client = fixture.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(Categories, Named("   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("name", problem.Field);
        Assert.Equal("product.category.nameRequired", problem.Code);
    }

    [Fact]
    public async Task A_parent_that_does_not_exist_is_refused_rather_than_silently_creating_a_root()
    {
        // The quiet failure this prevents: ignoring an unknown parent would create the category at
        // the root, which looks like success and puts it in the wrong place in every grouping.
        using var client = fixture.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(Categories, Named("Orphan", Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("parentId", problem.Field);
        Assert.Equal("product.category.parentMissing", problem.Code);
    }

    [Fact]
    public async Task Two_siblings_cannot_share_a_name_but_two_cousins_can()
    {
        using var client = fixture.CreateAuthenticatedClient();

        var beverages = await CreateAsync(client, $"Beverages {Guid.NewGuid():N}");
        var cleaning = await CreateAsync(client, $"Cleaning {Guid.NewGuid():N}");
        await CreateAsync(client, "Water", beverages.Id);

        // The same name under a different parent is a different thing, and both are correct. A
        // tenant-wide unique name would refuse this and force a naming convention on a tree that
        // already disambiguates by position.
        var cousin = await client.PostAsJsonAsync(Categories, Named("Water", cleaning.Id));
        Assert.Equal(HttpStatusCode.Created, cousin.StatusCode);

        var sibling = await client.PostAsJsonAsync(Categories, Named("water", beverages.Id));
        Assert.Equal(HttpStatusCode.Conflict, sibling.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(sibling));
        Assert.Equal("product.category.nameTaken", problem.Code);
        Assert.Equal("water", problem.Args?["name"]);
    }

    [Fact]
    public async Task Two_roots_cannot_share_a_name_either()
    {
        // The case the unique index cannot catch: Postgres treats NULLs as distinct, so
        // (TenantId, null, "Beverages") twice does not violate it. Without the in-code check this
        // silently succeeds — which is why the check exists rather than leaning on the constraint.
        using var client = fixture.CreateAuthenticatedClient();

        var name = $"Root {Guid.NewGuid():N}";
        await CreateAsync(client, name);

        var duplicate = await client.PostAsJsonAsync(Categories, Named(name));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("product.category.nameTaken", (await Refusals.ProblemsOf(duplicate))[0].Code);
    }

    [Fact]
    public async Task A_category_cannot_be_its_own_parent()
    {
        using var client = fixture.CreateAuthenticatedClient();

        var category = await CreateAsync(client, $"Self {Guid.NewGuid():N}");

        var response = await client.PutAsJsonAsync(
            $"{Categories}/{category.Id}", new CategoryRequest(category.Name, category.Id));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("product.category.ownParent", (await Refusals.ProblemsOf(response))[0].Code);
    }

    [Fact]
    public async Task A_category_cannot_be_moved_inside_its_own_branch()
    {
        // The case a self-parent check misses entirely: the cycle is two levels deep, so nothing
        // about the request looks wrong until the ancestor chain is walked.
        using var client = fixture.CreateAuthenticatedClient();

        var root = await CreateAsync(client, $"Root {Guid.NewGuid():N}");
        var child = await CreateAsync(client, "Child", root.Id);
        var grandchild = await CreateAsync(client, "Grandchild", child.Id);

        var response = await client.PutAsJsonAsync(
            $"{Categories}/{root.Id}", new CategoryRequest(root.Name, grandchild.Id));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("parentId", problem.Field);
        Assert.Equal("product.category.cycle", problem.Code);
    }

    [Fact]
    public async Task Deleting_a_category_with_children_is_refused_with_the_count()
    {
        // Nothing cascades. Deleting a branch would strip the grouping from every product beneath
        // it, so the answer names how much is in the way rather than doing it quietly.
        using var client = fixture.CreateAuthenticatedClient();

        var root = await CreateAsync(client, $"Branch {Guid.NewGuid():N}");
        await CreateAsync(client, "Leaf A", root.Id);
        await CreateAsync(client, "Leaf B", root.Id);

        var response = await client.DeleteAsync($"{Categories}/{root.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Null(problem.Field); // about the request as a whole, not one field
        Assert.Equal("product.category.hasChildren", problem.Code);
        Assert.Equal("2", problem.Args?["count"]);
    }

    [Fact]
    public async Task A_leaf_deletes_cleanly()
    {
        using var client = fixture.CreateAuthenticatedClient();

        var root = await CreateAsync(client, $"Branch {Guid.NewGuid():N}");
        var leaf = await CreateAsync(client, "Leaf", root.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Categories}/{leaf.Id}")).StatusCode);

        // And the parent can now go too — the refusal was about the children, not the category.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Categories}/{root.Id}")).StatusCode);
    }

    [Fact]
    public async Task The_database_refuses_a_parent_that_does_not_exist()
    {
        // The endpoint checks this too, and that check is the one users meet. This is about the
        // window the endpoint cannot close: it reads that a parent exists, then writes, and between
        // the two another request can delete that parent. Both pass their own checks and commit,
        // leaving a category whose parent is gone — an orphan invisible to any tree built from
        // parent pointers, because its root points nowhere.
        //
        // Written as raw SQL on purpose: going through the API would just hit the endpoint check
        // again and prove nothing about the table. Test projects are exempt from the raw-SQL ban
        // for exactly this reason — see Directory.Build.props.
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();

        var orphan = await Record.ExceptionAsync(() => db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO products.category
                ("Id", "Name", "ParentId", "TenantId", "CreatedAtUtc")
            VALUES ({Guid.CreateVersion7()}, 'Orphan', {Guid.NewGuid()}, {Guid.NewGuid()}, now())
            """));

        Assert.NotNull(orphan);
        Assert.Contains("FK_category_category_TenantId_ParentId", orphan.ToString());
    }

    [Fact]
    public async Task The_database_refuses_a_parent_belonging_to_another_tenant()
    {
        // The reason the foreign key is keyed on `(TenantId, ParentId)` rather than `ParentId`
        // alone. A plain self-FK is tenant-agnostic — it is satisfied by *any* tenant's category, so
        // it would accept this row without complaint, and the only thing refusing a cross-tenant
        // parent would be the endpoint's tenant-filtered lookup. That leaves the module's strongest
        // isolation guarantee resting entirely on application code.
        //
        // The parent below is real. Only the tenant is wrong, which is precisely the case a
        // single-column key cannot see.
        using var client = fixture.CreateAuthenticatedClient();
        var realParent = await CreateAsync(client, $"Real {Guid.NewGuid():N}");

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();

        var stolen = await Record.ExceptionAsync(() => db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO products.category
                ("Id", "Name", "ParentId", "TenantId", "CreatedAtUtc")
            VALUES ({Guid.CreateVersion7()}, 'Trespasser', {realParent.Id}, {Guid.NewGuid()}, now())
            """));

        Assert.NotNull(stolen);
        Assert.Contains("FK_category_category_TenantId_ParentId", stolen.ToString());
    }

    [Fact]
    public async Task A_root_is_exempt_from_the_parent_constraint()
    {
        // Postgres uses MATCH SIMPLE: a composite foreign key with any NULL column is not checked.
        // `ParentId` is null exactly for roots, so they skip it — which is what should happen, since
        // a root has no parent to verify. Asserted because it is the one behaviour of a composite
        // key that differs from the single-column version, and getting it wrong would make every
        // root un-creatable.
        using var client = fixture.CreateAuthenticatedClient();

        var root = await CreateAsync(client, $"Root {Guid.NewGuid():N}");

        Assert.Null(root.ParentId);
    }

    [Fact]
    public async Task One_tenants_categories_are_invisible_to_another()
    {
        // Tenant isolation, on a genuinely different issuer rather than a swapped claim.
        using var tenantA = fixture.CreateAuthenticatedClient();
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var mine = await CreateAsync(tenantA, $"Private {Guid.NewGuid():N}");

        var theirs = await tenantB.GetFromJsonAsync<List<CategoryResponse>>(Categories);
        Assert.DoesNotContain(theirs!, c => c.Id == mine.Id);

        // And it cannot be reached by id either — not-found, never someone else's data.
        var byId = await tenantB.PutAsJsonAsync($"{Categories}/{mine.Id}", Named("Stolen"));
        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);
    }

    [Fact]
    public async Task Reading_the_tree_and_reshaping_it_are_different_capabilities()
    {
        // `viewer` holds product:read but not product:write. 403 rather than 401: the caller is
        // known, and what they lack is the permission.
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Categories)).StatusCode);

        var write = await viewer.PostAsJsonAsync(Categories, Named("Not allowed"));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }
}
