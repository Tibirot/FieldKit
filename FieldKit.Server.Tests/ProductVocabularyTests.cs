using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Products;

namespace FieldKit.Server.Tests;

/// <summary>
/// Brands and tax classes — the flat half of the product classification vocabulary
/// (<c>PRD-01</c>, W6 slice 1b).
/// </summary>
/// <remarks>
/// Both entities in one class because they are the same shape and the same rules; splitting them
/// into two near-identical files would double the reading for no extra coverage. Where they differ
/// — the route, and the refusal codes — the tests say so explicitly.
/// </remarks>
[Collection(ServerCollection.Name)]
public class ProductVocabularyTests(ServerFixture fixture)
{
    private const string Brands = "/api/products/brands";
    private const string TaxClasses = "/api/products/tax-classes";

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..20];

    private static async Task<Guid> CreateAsync(HttpClient client, string route, string name)
    {
        var response = await client.PostAsJsonAsync(route, new BrandRequest(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // BrandResponse and TaxClassResponse are the same shape on the wire, which is why one
        // helper serves both routes.
        return (await response.Content.ReadFromJsonAsync<BrandResponse>())!.Id;
    }

    [Theory]
    [InlineData(Brands)]
    [InlineData(TaxClasses)]
    public async Task A_vocabulary_entry_is_created_listed_and_renamed(string route)
    {
        using var client = fixture.CreateAuthenticatedClient();

        var name = Unique("Veridian");
        var id = await CreateAsync(client, route, name);

        var listed = await client.GetFromJsonAsync<List<BrandResponse>>(route);
        Assert.Contains(listed!, entry => entry.Id == id && entry.Name == name);

        var renamed = Unique("Renamed");
        var update = await client.PutAsJsonAsync($"{route}/{id}", new BrandRequest(renamed));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(renamed, (await update.Content.ReadFromJsonAsync<BrandResponse>())!.Name);
    }

    [Theory]
    [InlineData(Brands, "product.brand.nameRequired")]
    [InlineData(TaxClasses, "product.taxClass.nameRequired")]
    public async Task A_vocabulary_entry_needs_a_name(string route, string expectedCode)
    {
        using var client = fixture.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(route, new BrandRequest("   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("name", problem.Field);
        Assert.Equal(expectedCode, problem.Code);
    }

    [Theory]
    [InlineData(Brands, "product.brand.nameTaken")]
    [InlineData(TaxClasses, "product.taxClass.nameTaken")]
    public async Task Names_are_unique_within_a_tenant_regardless_of_case(string route, string expectedCode)
    {
        // Case-insensitive on purpose: "Veridian" and "veridian" are one brand typed twice, and
        // letting both exist means every rule keyed to a brand quietly covers half the products.
        using var client = fixture.CreateAuthenticatedClient();

        var name = Unique("Unique");
        await CreateAsync(client, route, name);

        var duplicate = await client.PostAsJsonAsync(route, new BrandRequest(name.ToUpperInvariant()));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(duplicate));
        Assert.Equal(expectedCode, problem.Code);
        Assert.Equal(name.ToUpperInvariant(), problem.Args?["name"]);
    }

    [Theory]
    [InlineData(Brands)]
    [InlineData(TaxClasses)]
    public async Task Renaming_an_entry_to_its_own_name_is_not_a_conflict(string route)
    {
        // The `excluding` argument earning its place. Without it, saving a form that changed
        // something other than the name — or nothing at all — would refuse with "already exists",
        // naming the entry against itself.
        using var client = fixture.CreateAuthenticatedClient();

        var name = Unique("Same");
        var id = await CreateAsync(client, route, name);

        var response = await client.PutAsJsonAsync($"{route}/{id}", new BrandRequest(name));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(Brands)]
    [InlineData(TaxClasses)]
    public async Task An_entry_deletes_and_a_missing_one_is_not_found(string route)
    {
        using var client = fixture.CreateAuthenticatedClient();

        var id = await CreateAsync(client, route, Unique("Doomed"));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{route}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{route}/{id}")).StatusCode);
    }

    [Theory]
    [InlineData(Brands)]
    [InlineData(TaxClasses)]
    public async Task One_tenants_vocabulary_is_invisible_to_another(string route)
    {
        using var tenantA = fixture.CreateAuthenticatedClient();
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var mine = await CreateAsync(tenantA, route, Unique("Private"));

        var theirs = await tenantB.GetFromJsonAsync<List<BrandResponse>>(route);
        Assert.DoesNotContain(theirs!, entry => entry.Id == mine);

        var byId = await tenantB.PutAsJsonAsync($"{route}/{mine}", new BrandRequest(Unique("Stolen")));
        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);
    }

    [Theory]
    [InlineData(Brands)]
    [InlineData(TaxClasses)]
    public async Task Two_tenants_may_use_the_same_name(string route)
    {
        // The flip side of tenant-scoped uniqueness, and the reason the index is keyed on TenantId
        // rather than on Name alone. Two distributors both carrying Veridian is the normal case, and
        // a globally-unique name would let whichever tenant registered it first block the other.
        using var tenantA = fixture.CreateAuthenticatedClient();
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var shared = Unique("Shared");
        await CreateAsync(tenantA, route, shared);

        var forB = await tenantB.PostAsJsonAsync(route, new BrandRequest(shared));
        Assert.Equal(HttpStatusCode.Created, forB.StatusCode);
    }

    [Theory]
    [InlineData(Brands)]
    [InlineData(TaxClasses)]
    public async Task Reading_the_vocabulary_and_editing_it_are_different_capabilities(string route)
    {
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(route)).StatusCode);

        var write = await viewer.PostAsJsonAsync(route, new BrandRequest(Unique("Nope")));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }
}
