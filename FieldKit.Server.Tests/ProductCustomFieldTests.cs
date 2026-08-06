using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Products;

namespace FieldKit.Server.Tests;

/// <summary>
/// Tenant-defined fields on a product (<c>PRD-01</c>, <c>CFG-02</c>) — W6 slice 2c.
/// </summary>
/// <remarks>
/// The rules themselves are unit-tested in <see cref="CustomFieldRulesTests"/> without a database.
/// What these cover is the wiring Products owns: that values reach the column, come back, survive a
/// round trip, and that a violation is named and coded the way this module names and codes things.
/// </remarks>
[Collection(ServerCollection.Name)]
public class ProductCustomFieldTests(ServerFixture fixture)
{
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..20];

    /// <summary>Defines a custom field for products and returns its key.</summary>
    private static async Task<string> DefineAsync(
        HttpClient client,
        CustomFieldEntity entity = CustomFieldEntity.Product,
        CustomFieldType type = CustomFieldType.Text,
        bool required = false,
        int? maxLength = null)
    {
        var key = $"f{Guid.NewGuid():N}"[..12];

        var response = await client.PostAsJsonAsync(
            "/api/config/field-definitions",
            new CreateFieldDefinitionRequest(entity, key, key, type, required, MaxLength: maxLength));

        // The body on failure, not just the status: a definition rejected for a reason this test did
        // not intend is the kind of thing that otherwise reads as "the feature is broken".
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return key;
    }

    private static async Task<ProductResponse> CreateAsync(HttpClient client, CreateProductRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/products", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!;
    }

    private static Dictionary<string, JsonElement> Fields(string key, string rawJson) =>
        new() { [key] = JsonDocument.Parse(rawJson).RootElement };

    [Fact]
    public async Task A_product_carries_the_values_its_tenant_defined()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var key = await DefineAsync(client);

        using var writer = fixture.CreateAuthenticatedClient();
        var product = await CreateAsync(writer, new CreateProductRequest(
            Unique("SKU"), "Described", CustomFields: Fields(key, "\"organic\"")));

        Assert.Equal("organic", product.CustomFields[key].GetString());

        // And it survives the round trip through jsonb rather than only living in the response.
        var listed = await writer.GetFromJsonAsync<List<ProductResponse>>("/api/products");
        Assert.Equal("organic", Assert.Single(listed!, p => p.Id == product.Id).CustomFields[key].GetString());
    }

    [Fact]
    public async Task A_product_with_no_custom_fields_reads_back_as_an_empty_object()
    {
        // This is the test that catches the migration's generated `defaultValue: ""`. An empty
        // string is not JSON, so every pre-existing row would throw on deserialization the first
        // time it was read — invisible at migration time, and only here does anything look.
        using var client = fixture.CreateAuthenticatedClient();

        var product = await CreateAsync(client, new CreateProductRequest(Unique("SKU"), "Plain"));

        Assert.Empty(product.CustomFields);

        var listed = await client.GetFromJsonAsync<List<ProductResponse>>("/api/products");
        Assert.Empty(Assert.Single(listed!, p => p.Id == product.Id).CustomFields);
    }

    [Fact]
    public async Task An_undefined_key_is_refused_with_this_modules_code()
    {
        // The rules produce the violation; Products decides it is named `customFields.<key>` and
        // coded `product.customField.*`. Outlets will say `outlet.customField.*` for the same Kind,
        // which is why the shared rules return a Kind rather than a code.
        using var client = fixture.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            "/api/products",
            new CreateProductRequest(Unique("SKU"), "Wrong", CustomFields: Fields("nope", "\"x\"")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("customFields.nope", problem.Field);
        Assert.Equal("product.customField.unknown", problem.Code);
        Assert.Contains("for products.", problem.Message);
    }

    [Fact]
    public async Task A_value_of_the_wrong_type_is_refused_and_carries_its_arguments()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var key = await DefineAsync(admin, maxLength: 3);

        using var client = fixture.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync(
            "/api/products",
            new CreateProductRequest(Unique("SKU"), "TooLong", CustomFields: Fields(key, "\"abcdef\"")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("product.customField.tooLong", problem.Code);
        Assert.Equal("3", problem.Args?["max"]);
    }

    [Fact]
    public async Task A_required_field_must_be_supplied()
    {
        // The definition is removed at the end, and that is not tidiness. Definitions are tenant
        // state shared by every test in this collection, and a *required* one changes the meaning of
        // "create a product" for all of them — every other test's product suddenly carries an extra
        // violation it never asked about. Leaving it behind makes unrelated tests fail depending on
        // the order they ran in.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var key = await DefineAsync(admin, required: true);

        try
        {
            using var client = fixture.CreateAuthenticatedClient();
            var response = await client.PostAsJsonAsync(
                "/api/products", new CreateProductRequest(Unique("SKU"), "Missing it"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = Assert.Single(await Refusals.ProblemsOf(response));
            Assert.Equal($"customFields.{key}", problem.Field);
            Assert.Equal("product.customField.required", problem.Code);
        }
        finally
        {
            await DeleteDefinitionAsync(admin, key);
        }
    }

    /// <summary>Removes a definition so it stops applying to every other test's products.</summary>
    private static async Task DeleteDefinitionAsync(HttpClient client, string key)
    {
        var all = await client.GetFromJsonAsync<List<FieldDefinitionResponse>>(
            $"/api/config/field-definitions?entity={CustomFieldEntity.Product}");

        if (all?.SingleOrDefault(definition => definition.Key == key) is { } found)
        {
            await client.DeleteAsync($"/api/config/field-definitions/{found.Id}");
        }
    }

    [Fact]
    public async Task Updating_replaces_the_custom_fields_rather_than_merging_them()
    {
        // Same PUT semantics as the classification and the attributes: what the request describes is
        // what the product becomes.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var key = await DefineAsync(admin);

        using var client = fixture.CreateAuthenticatedClient();
        var product = await CreateAsync(client, new CreateProductRequest(
            Unique("SKU"), "Described", CustomFields: Fields(key, "\"before\"")));

        var response = await client.PutAsJsonAsync(
            $"/api/products/{product.Id}", new UpdateProductRequest("Described"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<ProductResponse>())!.CustomFields);
    }

    [Fact]
    public async Task A_products_definitions_are_not_an_outlets()
    {
        // The entity argument doing its job. A field defined for outlets is not defined for
        // products, so sending it here is an unknown key rather than a valid value — which is the
        // whole reason `CustomFieldEntity` is part of the catalogue's key.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var key = await DefineAsync(admin, CustomFieldEntity.Outlet);

        using var client = fixture.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync(
            "/api/products",
            new CreateProductRequest(Unique("SKU"), "Borrowed", CustomFields: Fields(key, "\"x\"")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.customField.unknown",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }
}
