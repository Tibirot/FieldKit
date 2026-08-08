using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FieldKit.Modules.Outlets;
using FieldKit.SharedKernel;

namespace FieldKit.Server.Tests;

/// <summary>
/// What the API says when a request is *shaped* wrongly rather than parsed wrongly.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MalformedRequestTests"/> covers a body the server cannot read at all. These are the
/// bodies it reads perfectly and then chokes on: a required array the caller omitted, and a string
/// two characters wider than its column. Both used to be <c>500</c>s — the API telling the caller it
/// had broken, when what happened is that they left out a field or typed a long name.
/// </para>
/// <para>
/// Found by a pre-W7 sweep rather than by any test, which is the reason this file exists: both are
/// invisible from the front end, because the back office never sends either shape.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class RequestRobustnessTests(ServerFixture fixture)
{
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task<Guid> ChannelAsync(HttpClient client)
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest($"CH-{Guid.NewGuid():N}"[..20]));

        return (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    /// <summary>
    /// Every endpoint whose request record declares a non-nullable collection.
    /// </summary>
    /// <remarks>
    /// A caller who omits one used to get a <c>NullReferenceException</c> on the handler's first
    /// <c>.Count</c> or <c>.Where</c>. Listed rather than sampled because the fix is a single
    /// serializer option — one test proving one endpoint would pass just as well if the option were
    /// removed and that endpoint happened to be guarded by hand.
    /// </remarks>
    [Fact]
    public async Task An_omitted_array_on_a_role_is_the_callers_mistake()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        // `permissions` is declared non-nullable and simply absent.
        var response = await client.PostAsync("/api/iam/roles", Json("""{"name":"Supervisor"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("StackTrace", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_omitted_array_on_a_put_is_the_callers_mistake()
    {
        // The default client, not the admin: this tenant's admin holds no `product:*`, which is
        // itself the shape the permission catalogue intends — the two roles are separate.
        using var client = fixture.CreateAuthenticatedClient();

        var created = await client.PostAsJsonAsync(
            "/api/products/price-lists",
            new { name = $"PL-{Guid.NewGuid():N}"[..20], currency = "EUR", effectiveFrom = "2026-01-01" });

        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // `{}` — the whole body, with `prices` simply absent.
        var response = await client.PutAsync($"/api/products/price-lists/{id}/prices", Json("{}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_name_wider_than_its_column_is_refused_rather_than_thrown()
    {
        using var client = fixture.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            "/api/products", new { sku = $"SKU-{Guid.NewGuid():N}"[..20], name = new string('x', 300) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problems = await response.Content.ReadFromJsonAsync<JsonElement>();
        var errors = problems.GetProperty("errors").EnumerateArray().ToList();

        Assert.Contains(errors, e => e.GetProperty("field").GetString() == "name");
        Assert.Contains(errors, e => e.GetProperty("code").GetString() == "product.name.tooLong");
    }

    [Theory]
    [InlineData("/api/outlets/channels", "name", 100)]
    [InlineData("/api/org/units", "name", 200)]
    public async Task A_module_refuses_its_own_overlong_name(string path, string field, int max)
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await client.PostAsync(
            path, Json($$"""{"{{field}}":"{{new string('x', max + 1)}}"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_overlong_outlet_name_names_the_field_it_is_about()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channelId = await ChannelAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(
                $"OUT-{Guid.NewGuid():N}"[..20], new string('x', 201), channelId, "UTC"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problems = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains(
            problems.GetProperty("errors").EnumerateArray(),
            e => e.GetProperty("field").GetString() == "name");
    }

    /// <summary>
    /// A country code that is not two letters is refused, not truncated.
    /// </summary>
    /// <remarks>
    /// "Romania" is the mistake worth naming. Truncating it to "Ro" would fit the column and then
    /// match no tax rate for the rest of the outlet's life — the same silent outcome the casing bug
    /// below produced.
    /// </remarks>
    [Theory]
    [InlineData("Romania")]
    [InlineData("R")]
    [InlineData("R0")]
    public async Task A_country_code_that_is_not_one_is_refused(string countryCode)
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channelId = await ChannelAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(
                $"OUT-{Guid.NewGuid():N}"[..20], "Corner Shop", channelId, "UTC",
                Address: new Address(CountryCode: countryCode)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problems = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains(
            problems.GetProperty("errors").EnumerateArray(),
            e => e.GetProperty("code").GetString() == "outlet.countryCode.invalid");
    }

    /// <summary>
    /// A lower-case country code is stored upper-cased, so tax can still find a rate.
    /// </summary>
    /// <remarks>
    /// The bug this replaces was silent in the worst way. <c>TaxRate.Create</c> upper-cases its
    /// country; the outlet's was stored exactly as typed, and tax resolution compares the two
    /// directly. So <c>"ro"</c> matched no rate, resolution answered "no tax", and that is
    /// indistinguishable from a tax class nobody has set a rate for — the very distinction
    /// <c>PRD-07</c> is built to keep. Nothing errored and nothing logged.
    /// </remarks>
    [Fact]
    public async Task A_lower_case_country_is_stored_upper_cased()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channelId = await ChannelAsync(client);

        var created = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(
                $"OUT-{Guid.NewGuid():N}"[..20], "Corner Shop", channelId, "UTC",
                Address: new Address("Str. Dorobanti 1", "Bucharest", "010001", "ro")));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var outlet = (await created.Content.ReadFromJsonAsync<OutletResponse>())!;

        Assert.Equal("RO", outlet.Address!.CountryCode);

        // And it survives an update typed the same way.
        var updated = await client.PutAsJsonAsync(
            $"/api/outlets/{outlet.Id}",
            new UpdateOutletRequest(
                outlet.Name, channelId, "UTC",
                Address: new Address("Str. Dorobanti 1", "Bucharest", "010001", "ro")));

        Assert.Equal("RO", (await updated.Content.ReadFromJsonAsync<OutletResponse>())!.Address!.CountryCode);
    }
}
