using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Catalog;

namespace FieldKit.Server.Tests;

/// <summary>
/// End-to-end: drives the Catalog module over HTTP through the real host — proving the modular
/// monolith actually runs and answers <c>/api</c> on Postgres. Closes Phase 0.
/// </summary>
/// <remarks>
/// Shares <see cref="ServerFixture"/> with the authentication tests so the containers start once
/// for the whole suite rather than per class.
/// </remarks>
[Collection(ServerCollection.Name)]
public class CatalogApiTests(ServerFixture fixture)
{
    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Create_then_list_returns_the_product()
    {
        var create = await Client.PostAsJsonAsync(
            "/api/products", new CreateProductRequest("VRD-STL-050", "Veridian Still 0.5L"));

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<ProductResponse>();
        Assert.NotNull(created);
        Assert.Equal("VRD-STL-050", created!.Sku);

        var list = await Client.GetFromJsonAsync<List<ProductResponse>>("/api/products");

        Assert.NotNull(list);
        Assert.Contains(list!, p => p.Id == created.Id && p.Sku == "VRD-STL-050");
    }

    [Fact]
    public async Task Liveness_endpoint_responds()
    {
        // /alive checks only "live"-tagged checks (the app itself), not readiness deps like Redis.
        var response = await Client.GetAsync("/alive");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
