using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Catalog;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace FieldKit.Server.Tests;

/// <summary>
/// Boots the real Server host once (WebApplicationFactory&lt;Program&gt;) against a real Postgres,
/// wiring the connection strings via environment variables (read by the default config builder).
/// </summary>
public sealed class CatalogApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private WebApplicationFactory<Program> _factory = null!;

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Aspire injects these at runtime; here we supply them before the host reads its config.
        Environment.SetEnvironmentVariable("ConnectionStrings__fieldkitdb", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("ConnectionStrings__cache", "localhost:6379,abortConnect=false");

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment(Environments.Development));

        Client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__fieldkitdb", null);
        Environment.SetEnvironmentVariable("ConnectionStrings__cache", null);
    }
}

/// <summary>
/// End-to-end: drives the Catalog module over HTTP through the real host — proving the modular
/// monolith actually runs and answers <c>/api</c> on Postgres. Closes Phase 0.
/// </summary>
public class CatalogApiTests(CatalogApiFixture fixture) : IClassFixture<CatalogApiFixture>
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
