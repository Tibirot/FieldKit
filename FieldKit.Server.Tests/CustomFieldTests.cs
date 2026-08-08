using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Outlets;

namespace FieldKit.Server.Tests;

/// <summary>
/// The custom-field catalogue and the validation it drives (<c>CFG-01</c>, <c>CFG-02</c>, <c>OUT-02</c>).
/// </summary>
/// <remarks>
/// Two modules and one rule: Configuration says what a tenant may record, Outlets decides whether
/// what arrived matches. The interesting assertions are the ones where a value is rejected for a
/// reason no code in Outlets knows in advance.
/// </remarks>
[Collection(ServerCollection.Name)]
public class CustomFieldTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..20];

    /// <summary>A key is an identifier, and every test needs its own so they cannot collide.</summary>
    private static string UniqueKey() => $"f{Guid.NewGuid():N}"[..12];

    private static JsonElement Json(object value) =>
        JsonSerializer.SerializeToElement(value);

    private async Task<FieldDefinitionResponse> DefineAsync(
        HttpClient client, CreateFieldDefinitionRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/config/field-definitions", request);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<FieldDefinitionResponse>())!;
    }

    private async Task<Guid> ChannelAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        return (await response.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    private async Task<HttpResponseMessage> OutletAsync(
        HttpClient client, Guid channelId, Dictionary<string, JsonElement>? customFields) =>
        await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(
                Unique("OUT"), "Corner Shop", channelId, Zone, CustomFields: customFields));

    [Fact]
    public async Task A_definition_describes_what_a_tenant_may_record()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var key = UniqueKey();
        var created = await DefineAsync(client, new CreateFieldDefinitionRequest(
            CustomFieldEntity.Outlet, key, "Chiller count", CustomFieldType.Number,
            Required: false, Options: null, MaxLength: null, Minimum: 0, Maximum: 50));

        Assert.Equal(key, created.Key);
        Assert.Equal(CustomFieldType.Number, created.Type);
        Assert.Equal(0, created.Minimum);
        Assert.Equal(50, created.Maximum);
    }

    [Fact]
    public async Task A_definition_that_could_never_be_satisfied_is_refused()
    {
        // Rules about the definition, not about values. Both of these would save happily and then
        // reject every value an admin tried, with the failure looking like the value's fault.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var noOptions = await client.PostAsJsonAsync("/api/config/field-definitions",
            new CreateFieldDefinitionRequest(
                CustomFieldEntity.Outlet, UniqueKey(), "Ownership", CustomFieldType.Choice));
        Assert.Equal(HttpStatusCode.BadRequest, noOptions.StatusCode);

        var backwards = await client.PostAsJsonAsync("/api/config/field-definitions",
            new CreateFieldDefinitionRequest(
                CustomFieldEntity.Outlet, UniqueKey(), "Size", CustomFieldType.Number,
                Minimum: 100, Maximum: 10));
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);

        // A key has to be an identifier: it goes into JSON and, one day, into an index expression.
        var badKey = await client.PostAsJsonAsync("/api/config/field-definitions",
            new CreateFieldDefinitionRequest(
                CustomFieldEntity.Outlet, "Chiller Count!", "Chillers", CustomFieldType.Number));
        Assert.Equal(HttpStatusCode.BadRequest, badKey.StatusCode);
    }

    [Fact]
    public async Task One_key_per_entity()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var key = UniqueKey();
        await DefineAsync(client, new CreateFieldDefinitionRequest(
            CustomFieldEntity.Outlet, key, "First", CustomFieldType.Text));

        var duplicate = await client.PostAsJsonAsync("/api/config/field-definitions",
            new CreateFieldDefinitionRequest(CustomFieldEntity.Outlet, key, "Second", CustomFieldType.Text));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // The same key on a different entity is a different field — "size" means something to an
        // outlet and something else to a product.
        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/config/field-definitions",
                new CreateFieldDefinitionRequest(CustomFieldEntity.Product, key, "Second", CustomFieldType.Text)))
                .StatusCode);
    }

    [Fact]
    public async Task An_outlet_carries_values_the_catalogue_describes()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var number = UniqueKey();
        var choice = UniqueKey();

        await DefineAsync(client, new CreateFieldDefinitionRequest(
            CustomFieldEntity.Outlet, number, "Chillers", CustomFieldType.Number, Minimum: 0, Maximum: 50));
        await DefineAsync(client, new CreateFieldDefinitionRequest(
            CustomFieldEntity.Outlet, choice, "Ownership", CustomFieldType.Choice,
            Options: ["independent", "franchise"]));

        var response = await OutletAsync(client, await ChannelAsync(client), new()
        {
            [number] = Json(3),
            [choice] = Json("franchise"),
        });

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var outlet = await response.Content.ReadFromJsonAsync<OutletResponse>();
        Assert.Equal(3, outlet!.CustomFields[number].GetInt32());
        Assert.Equal("franchise", outlet.CustomFields[choice].GetString());
    }

    [Fact]
    public async Task A_value_no_definition_describes_is_rejected()
    {
        // Rejected rather than dropped. Silently discarding an unknown key means an import or a typo
        // loses data with no signal — and the catalogue exists precisely so that what is stored can
        // be described.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await OutletAsync(client, await ChannelAsync(client), new()
        {
            ["not_a_defined_field"] = Json("anything"),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Named by the request path, and coded as Outlets' own (ADR-0012). The same violation from
        // the same shared rules is `product.customField.unknown` in Products — which is the point of
        // each module owning its codes rather than the rules deriving them.
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("customFields.not_a_defined_field", problem.Field);
        Assert.Equal("outlet.customField.unknown", problem.Code);
        Assert.Contains("for outlets.", problem.Message);
    }

    [Fact]
    public async Task Each_type_rejects_what_it_is_not()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channelId = await ChannelAsync(client);

        var text = UniqueKey();
        var number = UniqueKey();
        var flag = UniqueKey();
        var date = UniqueKey();
        var choice = UniqueKey();

        await DefineAsync(client, new CreateFieldDefinitionRequest(
            CustomFieldEntity.Outlet, text, "Note", CustomFieldType.Text, MaxLength: 5));
        await DefineAsync(client, new CreateFieldDefinitionRequest(
            CustomFieldEntity.Outlet, number, "Chillers", CustomFieldType.Number, Minimum: 0, Maximum: 10));
        await DefineAsync(client, new CreateFieldDefinitionRequest(
            CustomFieldEntity.Outlet, flag, "Has parking", CustomFieldType.Boolean));
        await DefineAsync(client, new CreateFieldDefinitionRequest(
            CustomFieldEntity.Outlet, date, "Refit", CustomFieldType.Date));
        await DefineAsync(client, new CreateFieldDefinitionRequest(
            CustomFieldEntity.Outlet, choice, "Ownership", CustomFieldType.Choice,
            Options: ["independent", "franchise"]));

        // Each of these is wrong in one specific way, and every one would be plausible JSON to a
        // schema that only checked the key existed.
        var rejected = new Dictionary<string, JsonElement>[]
        {
            new() { [text] = Json("far too long") },       // over MaxLength
            new() { [number] = Json("3") },                // a number as text
            new() { [number] = Json(11) },                 // over Maximum
            new() { [flag] = Json("true") },               // a boolean as text
            new() { [date] = Json("01/03/2026") },         // a date in the wrong format
            new() { [date] = Json("2026-03-01T00:00:00Z") }, // an instant where a day was meant
            new() { [choice] = Json("Franchise") },        // right value, wrong case
            new() { [choice] = Json("cooperative") },      // not an option
        };

        foreach (var values in rejected)
        {
            var response = await OutletAsync(client, channelId, values);

            Assert.True(
                response.StatusCode == HttpStatusCode.BadRequest,
                $"expected 400 for {JsonSerializer.Serialize(values)}, got {response.StatusCode}");
        }
    }

    [Fact]
    public async Task A_required_field_must_be_present()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channelId = await ChannelAsync(client);
        var key = UniqueKey();

        var definition = await DefineAsync(client, new CreateFieldDefinitionRequest(
            CustomFieldEntity.Outlet, key, "Ownership", CustomFieldType.Choice,
            Required: true, Options: ["independent", "franchise"]));

        try
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await OutletAsync(client, channelId, null)).StatusCode);

            Assert.Equal(
                HttpStatusCode.Created,
                (await OutletAsync(client, channelId, new() { [key] = Json("independent") })).StatusCode);
        }
        finally
        {
            // Shared fixture: a lingering required field would make every later outlet test fail on
            // a rule it never asked for.
            await client.DeleteAsync($"/api/config/field-definitions/{definition.Id}");
        }
    }

    [Fact]
    public async Task Values_are_replaced_wholesale()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channelId = await ChannelAsync(client);
        var key = UniqueKey();

        await DefineAsync(client, new CreateFieldDefinitionRequest(
            CustomFieldEntity.Outlet, key, "Chillers", CustomFieldType.Number));

        var created = await (await OutletAsync(client, channelId, new() { [key] = Json(3) }))
            .Content.ReadFromJsonAsync<OutletResponse>();

        // An empty map clears them — which is the only way an optional field can be unset, and the
        // reason this is not a patch.
        var cleared = await client.PutAsJsonAsync(
            $"/api/outlets/{created!.Id}",
            new UpdateOutletRequest(
                created.Name, channelId, Zone,
                CustomFields: new Dictionary<string, JsonElement>()));

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Empty((await cleared.Content.ReadFromJsonAsync<OutletResponse>())!.CustomFields);
    }

    [Fact]
    public async Task Enums_travel_as_names_in_both_directions()
    {
        // Written against raw JSON on purpose. Every other test here goes through PostAsJsonAsync,
        // which serializes from the DTO — so it happily sent ordinals and read ordinals back, and
        // agreed with itself while the API answered a hand-written request with `"type":0` and threw
        // on `"type":"Number"`. Found by running the thing; this is what stops it coming back.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var key = UniqueKey();

        var created = await client.PostAsync(
            "/api/config/field-definitions",
            new StringContent(
                $$"""
                {"entity":"Outlet","key":"{{key}}","label":"Chillers","type":"Number","maximum":50}
                """,
                Encoding.UTF8,
                "application/json"));

        Assert.True(
            created.StatusCode == HttpStatusCode.Created,
            $"{created.StatusCode}: {await created.Content.ReadAsStringAsync()}");

        var body = await created.Content.ReadAsStringAsync();
        Assert.Contains("\"entity\":\"Outlet\"", body);
        Assert.Contains("\"type\":\"Number\"", body);

        // And the query parameter, which binds by name rather than through the body's converters.
        var listed = await client.GetStringAsync("/api/config/field-definitions?entity=Outlet");
        Assert.Contains($"\"key\":\"{key}\"", listed);
    }

    [Fact]
    public async Task Authoring_the_catalogue_and_reading_it_are_different_capabilities()
    {
        // `viewer` holds config:read and not config:write — the outlet screens render from the
        // catalogue, so reading it is not an administrative act.
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/config/field-definitions")).StatusCode);

        var write = await viewer.PostAsJsonAsync("/api/config/field-definitions",
            new CreateFieldDefinitionRequest(CustomFieldEntity.Outlet, UniqueKey(), "Nope", CustomFieldType.Text));

        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task One_tenants_catalogue_never_validates_anothers_values()
    {
        // The cross-module, cross-tenant case: the definition is real, but it belongs to tenant A, so
        // tenant B's outlet is rejected for using a key that — from where B stands — does not exist.
        // Nothing in Outlets had to know about tenants to get that right.
        using var tenantA = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var key = UniqueKey();
        await DefineAsync(tenantA, new CreateFieldDefinitionRequest(
            CustomFieldEntity.Outlet, key, "A's field", CustomFieldType.Text));

        var visibleToB = await tenantB.GetFromJsonAsync<List<FieldDefinitionResponse>>(
            "/api/config/field-definitions");
        Assert.DoesNotContain(visibleToB!, definition => definition.Key == key);

        var response = await OutletAsync(tenantB, await ChannelAsync(tenantB), new() { [key] = Json("value") });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
