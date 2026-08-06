using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Products;

namespace FieldKit.Server.Tests;

/// <summary>
/// How a product is classified, and what a vocabulary entry cannot do once one is
/// (<c>PRD-01</c>, W6 slice 2a).
/// </summary>
[Collection(ServerCollection.Name)]
public class ProductClassificationTests(ServerFixture fixture)
{
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..20];

    private static async Task<Guid> CreateVocabularyAsync(HttpClient client, string route, string name)
    {
        var response = await client.PostAsJsonAsync(route, new BrandRequest(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BrandResponse>())!.Id;
    }

    private static async Task<ProductResponse> CreateProductAsync(
        HttpClient client, CreateProductRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/products", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!;
    }

    [Fact]
    public async Task A_product_can_be_created_before_the_tenant_has_any_vocabulary()
    {
        // The reason all three are optional. Requiring them would mean a new tenant cannot enter its
        // first product until it has authored a brand list, a category tree and a set of tax
        // classes — and the product is the thing people arrive wanting to enter.
        using var client = fixture.CreateAuthenticatedClient();

        var product = await CreateProductAsync(client, new CreateProductRequest(Unique("SKU"), "Unclassified"));

        Assert.Null(product.BrandId);
        Assert.Null(product.CategoryId);
        Assert.Null(product.TaxClassId);
    }

    [Fact]
    public async Task A_product_carries_the_classification_it_was_given()
    {
        using var client = fixture.CreateAuthenticatedClient();

        var brand = await CreateVocabularyAsync(client, "/api/products/brands", Unique("Veridian"));
        var category = await CreateVocabularyAsync(client, "/api/products/categories", Unique("Water"));
        var taxClass = await CreateVocabularyAsync(client, "/api/products/tax-classes", Unique("Reduced"));

        var product = await CreateProductAsync(
            client, new CreateProductRequest(Unique("SKU"), "Still 0.5L", brand, category, taxClass));

        Assert.Equal(brand, product.BrandId);
        Assert.Equal(category, product.CategoryId);
        Assert.Equal(taxClass, product.TaxClassId);

        var listed = await client.GetFromJsonAsync<List<ProductResponse>>("/api/products");
        Assert.Equal(brand, Assert.Single(listed!, p => p.Id == product.Id).BrandId);
    }

    [Fact]
    public async Task Every_unknown_classification_id_is_reported_at_once()
    {
        // All of them, not the first. A form with three bad ids should be fixable in one pass, and
        // the three problems name three different fields — which is what makes the shared envelope
        // worth having.
        using var client = fixture.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            "/api/products",
            new CreateProductRequest(Unique("SKU"), "Wrong", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problems = await Refusals.ProblemsOf(response);
        Assert.Equal(3, problems.Count);
        Assert.Equal(
            ["product.brand.missing", "product.category.missing", "product.taxClass.missing"],
            problems.Select(p => p.Code).Order());
        Assert.Equal(["brandId", "categoryId", "taxClassId"], problems.Select(p => p.Field).Order());
    }

    [Fact]
    public async Task A_product_cannot_borrow_another_tenants_brand()
    {
        // The classification checks are tenant-filtered, so another tenant's brand reads as "does
        // not exist" — the only answer that does not confirm the id is real somewhere else. The
        // composite foreign key enforces the same rule at the table.
        using var tenantA = fixture.CreateAuthenticatedClient();
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var brandOfA = await CreateVocabularyAsync(tenantA, "/api/products/brands", Unique("PrivateBrand"));

        var response = await tenantB.PostAsJsonAsync(
            "/api/products", new CreateProductRequest(Unique("SKU"), "Trespasser", brandOfA));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("product.brand.missing", Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task Updating_replaces_the_classification_rather_than_merging_it()
    {
        // PUT semantics, stated because the alternative is a plausible reading: omitting `brandId`
        // clears the brand, it does not leave it alone. A form that renders the current values and
        // posts them all back gets this right; a partial update would silently unclassify.
        using var client = fixture.CreateAuthenticatedClient();

        var brand = await CreateVocabularyAsync(client, "/api/products/brands", Unique("Before"));
        var product = await CreateProductAsync(
            client, new CreateProductRequest(Unique("SKU"), "Named", brand));

        var response = await client.PutAsJsonAsync(
            $"/api/products/{product.Id}", new UpdateProductRequest("Renamed"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<ProductResponse>();
        Assert.Equal("Renamed", updated!.Name);
        Assert.Null(updated.BrandId);
    }

    [Fact]
    public async Task An_sku_cannot_be_taken_twice()
    {
        using var client = fixture.CreateAuthenticatedClient();

        var sku = Unique("SKU");
        await CreateProductAsync(client, new CreateProductRequest(sku, "First"));

        var duplicate = await client.PostAsJsonAsync("/api/products", new CreateProductRequest(sku, "Second"));

        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(duplicate));
        Assert.Equal("product.sku.taken", problem.Code);
        Assert.Equal(sku, problem.Args?["sku"]);
    }

    [Theory]
    [InlineData("/api/products/brands", "product.brand.inUse")]
    [InlineData("/api/products/categories", "product.category.inUse")]
    [InlineData("/api/products/tax-classes", "product.taxClass.inUse")]
    public async Task A_vocabulary_entry_in_use_by_a_product_cannot_be_deleted(string route, string expectedCode)
    {
        // The guard slice 1b deferred, now that products can point at these. The foreign key would
        // refuse the delete regardless — what this adds is a count an admin can act on rather than a
        // constraint violation.
        using var client = fixture.CreateAuthenticatedClient();

        var entryId = await CreateVocabularyAsync(client, route, Unique("InUse"));

        var request = route switch
        {
            "/api/products/brands" => new CreateProductRequest(Unique("SKU"), "Classified", BrandId: entryId),
            "/api/products/categories" => new CreateProductRequest(Unique("SKU"), "Classified", CategoryId: entryId),
            _ => new CreateProductRequest(Unique("SKU"), "Classified", TaxClassId: entryId),
        };
        await CreateProductAsync(client, request);

        var response = await client.DeleteAsync($"{route}/{entryId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Null(problem.Field); // about the request as a whole
        Assert.Equal(expectedCode, problem.Code);
        Assert.Equal("1", problem.Args?["count"]);
    }

    [Theory]
    [InlineData("/api/products/brands")]
    [InlineData("/api/products/categories")]
    [InlineData("/api/products/tax-classes")]
    public async Task An_unused_vocabulary_entry_still_deletes(string route)
    {
        // What makes the refusal above about being in use, rather than deletion being broken.
        using var client = fixture.CreateAuthenticatedClient();

        var entryId = await CreateVocabularyAsync(client, route, Unique("Unused"));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{route}/{entryId}")).StatusCode);
    }
}
