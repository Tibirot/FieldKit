using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Products;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// End-to-end: drives the Products module over HTTP through the real host — proving the modular
/// monolith actually runs and answers <c>/api</c> on Postgres. Closes Phase 0.
/// </summary>
/// <remarks>
/// Shares <see cref="ServerFixture"/> with the authentication tests so the containers start once
/// for the whole suite rather than per class.
/// </remarks>
[Collection(ServerCollection.Name)]
public class ProductsApiTests(ServerFixture fixture)
{
    [Fact]
    public async Task Create_then_list_returns_the_product()
    {
        // Products require product:write / product:read, so this drives the API as an
        // authenticated caller rather than anonymously.
        using var Client = fixture.CreateAuthenticatedClient();

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
    public async Task The_product_table_lives_in_the_products_schema_and_only_there()
    {
        // Retiring Catalog moved this table from the `catalog` schema to `products`, via a
        // regenerated initial migration. Asserted against the real database rather than against
        // ProductsDbContext.SchemaName, because the constant is the thing that could be right while
        // the migration that actually builds the schema is wrong — exactly the gap a rename opens.
        //
        // The "only there" half is the one that would catch a half-done move: a leftover
        // `catalog.product` alongside `products.product` gives two tables claiming one aggregate,
        // and reads would keep working from whichever the context happened to be pointed at.
        //
        // Scoped to what this fixture can actually see: a Postgres container built fresh for the
        // run, migrated from empty. It does not and cannot observe the `catalog` schema left behind
        // in a developer's AppHost volume — that one is only visible with the app running, and the
        // failure message says what to do about it if it ever turns up here.
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();

        var schemas = await db.Database
            .SqlQuery<string>(
                // Aliased "Value": that is the column name EF's scalar SqlQuery binds to.
                $"""SELECT table_schema AS "Value" FROM information_schema.tables WHERE table_name = 'product'""")
            .ToListAsync();

        Assert.True(
            schemas is ["products"],
            $"""
            Expected the `product` table in the `products` schema and nowhere else,
            but found it in: {string.Join(", ", schemas)}.

            If `catalog` is in that list, the database predates the Catalog → Products rename and
            still carries the old schema. It is inert — nothing reads it — but it is not empty, so
            clear it deliberately rather than by accident:

                DROP SCHEMA catalog CASCADE;
            """);
    }

    [Fact]
    public async Task Liveness_endpoint_responds()
    {
        // /alive checks only "live"-tagged checks (the app itself), not readiness deps like Postgres.
        // Anonymous on purpose: an orchestrator probing liveness has no token, and requiring one
        // would make the app look dead whenever Keycloak was.
        var response = await fixture.Client.GetAsync("/alive");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
