using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Products;

namespace FieldKit.Server.Tests;

/// <summary>
/// What a product <i>is</i>, as opposed to how it is classified (<c>PRD-01</c>, W6 slice 2b).
/// </summary>
[Collection(ServerCollection.Name)]
public class ProductAttributeTests(ServerFixture fixture)
{
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..20];

    private static async Task<ProductResponse> CreateAsync(HttpClient client, CreateProductRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/products", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!;
    }

    [Fact]
    public async Task A_product_carries_its_measure_pack_and_status()
    {
        using var client = fixture.CreateAuthenticatedClient();

        var product = await CreateAsync(client, new CreateProductRequest(
            Unique("SKU"), "Veridian Still 0.5L", UnitOfMeasure: "CS", PackSize: 24));

        Assert.Equal("CS", product.UnitOfMeasure);
        Assert.Equal(24, product.PackSize);
        Assert.Equal(ProductStatus.Active, product.Status);
    }

    [Fact]
    public async Task A_new_product_is_active_without_being_told_to_be()
    {
        // The default that matters: a catalogue where products arrive discontinued, or with no
        // status at all, is one where every read has to special-case the absence.
        using var client = fixture.CreateAuthenticatedClient();

        var product = await CreateAsync(client, new CreateProductRequest(Unique("SKU"), "Implicit"));

        Assert.Equal(ProductStatus.Active, product.Status);
    }

    [Fact]
    public async Task Loose_goods_may_have_no_pack_size_or_measure()
    {
        // Null is a real answer here, not a missing one: "how many are in it" has no answer for
        // something sold by weight.
        using var client = fixture.CreateAuthenticatedClient();

        var product = await CreateAsync(client, new CreateProductRequest(Unique("SKU"), "Sold loose"));

        Assert.Null(product.UnitOfMeasure);
        Assert.Null(product.PackSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_pack_size_below_one_is_refused_rather_than_quietly_dropped(int packSize)
    {
        // Refused, not normalised to null. A pack of zero is not "no pack size", it is a number
        // someone got wrong — and turning it into null would let a bad import look like a deliberate
        // omission, which is the kind of thing nobody finds until an order is short.
        using var client = fixture.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            "/api/products", new CreateProductRequest(Unique("SKU"), "Impossible", PackSize: packSize));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("packSize", problem.Field);
        Assert.Equal("product.packSize.notPositive", problem.Code);
        Assert.Equal(packSize.ToString(), problem.Args?["packSize"]);
    }

    [Fact]
    public async Task An_overlong_unit_of_measure_is_refused()
    {
        using var client = fixture.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            "/api/products",
            new CreateProductRequest(Unique("SKU"), "Verbose", UnitOfMeasure: new string('x', 17)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.unitOfMeasure.tooLong",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task A_blank_unit_of_measure_is_stored_as_absent()
    {
        // Whitespace is how a form sends "I cleared this field". Storing it would make `= ''` and
        // `IS NULL` two different ways of asking the same question, and only one of them right.
        using var client = fixture.CreateAuthenticatedClient();

        var product = await CreateAsync(
            client, new CreateProductRequest(Unique("SKU"), "Blanked", UnitOfMeasure: "   "));

        Assert.Null(product.UnitOfMeasure);
    }

    [Fact]
    public async Task A_product_can_be_discontinued_and_brought_back()
    {
        // The difference from OutletStatus.Closed, which is terminal. A shop that shut down does not
        // reopen; a product does — seasonal lines return, suppliers resume, ranges are reinstated.
        // Making this one-way would mean re-creating the SKU to sell it again, under a new id that
        // every historical order line fails to point at.
        using var client = fixture.CreateAuthenticatedClient();

        var product = await CreateAsync(client, new CreateProductRequest(Unique("SKU"), "Seasonal"));

        var discontinued = await client.PutAsJsonAsync(
            $"/api/products/{product.Id}",
            new UpdateProductRequest("Seasonal", Status: ProductStatus.Discontinued));
        Assert.Equal(HttpStatusCode.OK, discontinued.StatusCode);
        Assert.Equal(
            ProductStatus.Discontinued,
            (await discontinued.Content.ReadFromJsonAsync<ProductResponse>())!.Status);

        var reinstated = await client.PutAsJsonAsync(
            $"/api/products/{product.Id}",
            new UpdateProductRequest("Seasonal", Status: ProductStatus.Active));
        Assert.Equal(HttpStatusCode.OK, reinstated.StatusCode);
        Assert.Equal(
            ProductStatus.Active,
            (await reinstated.Content.ReadFromJsonAsync<ProductResponse>())!.Status);
    }

    [Fact]
    public async Task Status_crosses_the_wire_as_a_name_not_a_number()
    {
        // Asserted on the raw body, because the typed client would deserialize either form happily
        // and prove nothing. Without the JsonStringEnumConverter this goes out as `"status":0`,
        // which is a number every client needs a private lookup table for — and which changes
        // meaning if an enum member is ever inserted rather than appended.
        //
        // The precedent is OutletStatus and the Configuration enums, which already do this. A
        // second convention for the same thing is worse than either convention alone.
        using var client = fixture.CreateAuthenticatedClient();

        var product = await CreateAsync(client, new CreateProductRequest(Unique("SKU"), "Named status"));

        var body = await client.GetStringAsync("/api/products");

        Assert.Contains("\"status\":\"Active\"", body);
        Assert.DoesNotContain("\"status\":0", body);
        Assert.Equal(ProductStatus.Active, product.Status);
    }

    [Fact]
    public async Task Updating_replaces_the_attributes_rather_than_merging_them()
    {
        // Same PUT semantics as the classification ids: omitting a measure clears it. Pinned
        // separately because a reader who accepted that for ids may still expect a scalar to
        // survive.
        using var client = fixture.CreateAuthenticatedClient();

        var product = await CreateAsync(client, new CreateProductRequest(
            Unique("SKU"), "Described", UnitOfMeasure: "CS", PackSize: 12));

        var response = await client.PutAsJsonAsync(
            $"/api/products/{product.Id}", new UpdateProductRequest("Described"));

        var updated = await response.Content.ReadFromJsonAsync<ProductResponse>();
        Assert.Null(updated!.UnitOfMeasure);
        Assert.Null(updated.PackSize);
    }
}
