using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Products;
using FieldKit.Modules.Sync;

namespace FieldKit.Server.Tests;

/// <summary>
/// The catalogue on the device: products in <c>/sync/pull</c> (<c>OFF-03</c>, W8 slice 8c).
/// </summary>
/// <remarks>
/// <para>
/// The fourth entity type, and the second to be scoped by nothing. What is new here is that the
/// no-scope decision has a <i>second</i> reason of its own: a rep standing in a shop has to be able
/// to name what they are looking at, and a catalogue narrowed to the assortment would give a blank
/// where a name should be.
/// </para>
/// <para>
/// It is also the first store big enough for the page limit to be a real number rather than a
/// formality, which is why the cursor assertions matter more here than anywhere else.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class SyncPullProductTests(ServerFixture fixture)
{
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private static async Task<Guid> BindDeviceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/devices", new BindDeviceRequest(Unique("Phone")));

        return (await response.Content.ReadFromJsonAsync<DeviceResponse>())!.Id;
    }

    private static async Task<JsonElement> PullAsync(HttpClient client, Guid deviceId, long? products = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/pull", new PullRequest(deviceId, new PullCursors(null, null, null, products)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static JsonElement Catalogue(JsonElement pull) =>
        pull.GetProperty("changes").GetProperty("products");

    private static long Cursor(JsonElement pull) => Catalogue(pull).GetProperty("cursor").GetInt64();

    private static List<JsonElement> Products(JsonElement pull) =>
        [.. Catalogue(pull).GetProperty("upserts").EnumerateArray()];

    private static async Task<Guid> ProductAsync(HttpClient writer, string? name = null)
    {
        var response = await writer.PostAsJsonAsync(
            "/api/products", new { sku = Unique("SKU"), name = name ?? "Cola 500ml" });

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task The_catalogue_reaches_the_device_and_the_next_pull_carries_nothing()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        // Products are written by the role that holds `product:write`, which the administrator
        // fixture does not — catalogue maintenance is sales-ops work, not tenant administration.
        using var writer = fixture.CreateAuthenticatedClient();

        var productId = await ProductAsync(writer, "Sparkling Water 1L");

        var device = await BindDeviceAsync(rep);
        var first = await PullAsync(rep, device);

        var sent = Assert.Single(Products(first), row => row.GetProperty("id").GetGuid() == productId);

        Assert.Equal("Sparkling Water 1L", sent.GetProperty("name").GetString());
        Assert.True(sent.GetProperty("rowVersion").GetInt64() > 0);

        // By name, never by ordinal — inserting a value into the middle of `ProductStatus` would
        // otherwise make a discontinued product read as active on every device already holding it.
        Assert.Equal("Active", sent.GetProperty("status").GetString());

        Assert.True(
            Cursor(first) >= Products(first).Max(row => row.GetProperty("rowVersion").GetInt64()),
            "the cursor must cover every row in the page, or the next pull re-sends it forever");

        var second = await PullAsync(rep, device, Cursor(first));

        Assert.Empty(Products(second));
        Assert.Equal(Cursor(first), Cursor(second));
    }

    [Fact]
    public async Task A_new_product_reaches_a_device_that_had_already_synced()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        // Products are written by the role that holds `product:write`, which the administrator
        // fixture does not — catalogue maintenance is sales-ops work, not tenant administration.
        using var writer = fixture.CreateAuthenticatedClient();

        var device = await BindDeviceAsync(rep);
        var before = await PullAsync(rep, device);

        var productId = await ProductAsync(writer);

        var after = await PullAsync(rep, device, Cursor(before));

        Assert.Single(Products(after), row => row.GetProperty("id").GetGuid() == productId);
        Assert.True(Cursor(after) > Cursor(before));
    }

    [Fact]
    public async Task A_rep_with_no_territory_still_gets_the_whole_catalogue()
    {
        // The no-scope decision, asserted rather than assumed. The rep in this fixture may cover no
        // outlets at all, and the catalogue is not narrowed by anything — a rep on an unplanned call
        // at a shop nobody assigned them still has to be able to name what is on the shelf.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        // Products are written by the role that holds `product:write`, which the administrator
        // fixture does not — catalogue maintenance is sales-ops work, not tenant administration.
        using var writer = fixture.CreateAuthenticatedClient();

        var productId = await ProductAsync(writer);

        var device = await BindDeviceAsync(rep);
        var pull = await PullAsync(rep, device);

        Assert.Contains(Products(pull), row => row.GetProperty("id").GetGuid() == productId);
    }

    [Fact]
    public async Task A_discontinued_product_is_sent_rather_than_filtered()
    {
        // A device holding an order taken last week still has to name what is on it. Filtering here
        // would make the row vanish with no tombstone and no explanation, and the screen would show
        // an id. Whether it may be *ordered* is PRD-02's question, answered on the device.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        // Products are written by the role that holds `product:write`, which the administrator
        // fixture does not — catalogue maintenance is sales-ops work, not tenant administration.
        using var writer = fixture.CreateAuthenticatedClient();

        var productId = await ProductAsync(writer);

        var device = await BindDeviceAsync(rep);
        var first = await PullAsync(rep, device);

        var retired = await writer.PutAsJsonAsync(
            $"/api/products/{productId}",
            new UpdateProductRequest("Cola 500ml", Status: ProductStatus.Discontinued));
        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);

        var second = await PullAsync(rep, device, Cursor(first));

        var sent = Assert.Single(Products(second), row => row.GetProperty("id").GetGuid() == productId);
        Assert.Equal("Discontinued", sent.GetProperty("status").GetString());
        Assert.Empty(Catalogue(second).GetProperty("tombstones").EnumerateArray());
    }

    [Fact]
    public async Task The_product_cursor_moves_on_its_own()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        // Products are written by the role that holds `product:write`, which the administrator
        // fixture does not — catalogue maintenance is sales-ops work, not tenant administration.
        using var writer = fixture.CreateAuthenticatedClient();

        await ProductAsync(writer);

        var device = await BindDeviceAsync(rep);
        var first = await PullAsync(rep, device);

        // A channel is Outlets' schema and Outlets' counter. Nothing about it may move this one.
        var channel = await admin.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));
        Assert.Equal(HttpStatusCode.Created, channel.StatusCode);

        var second = await PullAsync(rep, device, Cursor(first));

        Assert.Empty(Products(second));
        Assert.Equal(Cursor(first), Cursor(second));
    }
}
