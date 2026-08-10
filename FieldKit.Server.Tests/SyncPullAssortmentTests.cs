using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Org;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Products;
using FieldKit.Modules.Sync;

namespace FieldKit.Server.Tests;

/// <summary>
/// What a rep may sell: the assortment in <c>/sync/pull</c> (<c>OFF-03</c>, W8 slice 8d).
/// </summary>
/// <remarks>
/// <para>
/// Two entity types in one slice because they are two halves of one rule, with two different
/// scopes. The channel list is tenant-wide; the per-outlet overrides are the <b>first entity scoped
/// by the device's outlet set</b>, and therefore the first to need the baseline shape outlets have
/// had since slice 3.
/// </para>
/// <para>
/// The tests that matter here are the two that are about *membership* rather than content: an outlet
/// entering a rep's territory brings overrides written long ago, and an outlet leaving takes them
/// away without the server sending a single tombstone for them.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class SyncPullAssortmentTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private static async Task<Guid> BindDeviceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/devices", new BindDeviceRequest(Unique("Phone")));

        return (await response.Content.ReadFromJsonAsync<DeviceResponse>())!.Id;
    }

    private static async Task<JsonElement> PullAsync(
        HttpClient client, Guid deviceId, long? assortment = null, long? overrides = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/pull",
            new PullRequest(deviceId, new PullCursors(null, null, null, null, assortment, overrides)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static JsonElement Lines(JsonElement pull) =>
        pull.GetProperty("changes").GetProperty("assortment");

    private static JsonElement Overrides(JsonElement pull) =>
        pull.GetProperty("changes").GetProperty("outletAssortment");

    private static List<JsonElement> Upserts(JsonElement page) =>
        [.. page.GetProperty("upserts").EnumerateArray()];

    [Fact]
    public async Task The_channel_list_reaches_every_device_and_the_next_pull_carries_nothing()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var productId = await ProductAsync(writer);
        await SetAssortmentAsync(writer, channelId, productId, mustStock: true);

        var device = await BindDeviceAsync(rep);
        var first = await PullAsync(rep, device);

        var line = Assert.Single(Upserts(Lines(first)), row =>
            row.GetProperty("channelId").GetGuid() == channelId);

        Assert.Equal(productId, line.GetProperty("productId").GetGuid());
        Assert.True(line.GetProperty("isMustStock").GetBoolean());

        var second = await PullAsync(rep, device, Lines(first).GetProperty("cursor").GetInt64());

        Assert.Empty(Upserts(Lines(second)));
    }

    [Fact]
    public async Task Replacing_a_channel_list_tombstones_what_it_dropped()
    {
        // Setting an assortment replaces it, so an ordinary edit deletes rows. Without tombstones a
        // device accumulates the union of every list the channel has ever had, and a rep is offered
        // products the tenant removed months ago with no way to tell which.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var dropped = await ProductAsync(writer);
        var kept = await ProductAsync(writer);
        await SetAssortmentAsync(writer, channelId, dropped, kept);

        var device = await BindDeviceAsync(rep);
        var first = await PullAsync(rep, device);

        var droppedLine = Assert.Single(Upserts(Lines(first)), row =>
            row.GetProperty("productId").GetGuid() == dropped);

        await SetAssortmentAsync(writer, channelId, kept);

        var second = await PullAsync(rep, device, Lines(first).GetProperty("cursor").GetInt64());

        Assert.Contains(
            Lines(second).GetProperty("tombstones").EnumerateArray(),
            tombstone => tombstone.GetProperty("id").GetGuid() == droppedLine.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task An_outlet_entering_the_territory_brings_overrides_written_long_ago()
    {
        // The reason this feed needs a baseline. The override's row version was stamped before the
        // device ever existed, so `rowVersion > cursor` will never mention it — only the fact that
        // its outlet has just entered scope can.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer);
        await OverrideAsync(writer, outletId, productId);

        var device = await BindDeviceAsync(rep);

        // First pull: the outlet is not the rep's yet, so neither is the override — and the device
        // banks a cursor well above the override's row version.
        var before = await PullAsync(rep, device);
        Assert.DoesNotContain(
            Upserts(Overrides(before)), row => row.GetProperty("outletId").GetGuid() == outletId);

        await GiveRepTheOutletAsync(admin, rep, outletId);

        var after = await PullAsync(
            rep, device, overrides: Overrides(before).GetProperty("cursor").GetInt64());

        Assert.Single(Upserts(Overrides(after)), row => row.GetProperty("outletId").GetGuid() == outletId);
    }

    [Fact]
    public async Task An_override_travels_with_its_kind_by_name()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer);

        await GiveRepTheOutletAsync(admin, rep, outletId);
        await OverrideAsync(writer, outletId, productId, AssortmentOverrideKind.Removed);

        var device = await BindDeviceAsync(rep);
        var pull = await PullAsync(rep, device);

        var sent = Assert.Single(Upserts(Overrides(pull)), row =>
            row.GetProperty("outletId").GetGuid() == outletId);

        // A name, not an ordinal: an inserted enum value would otherwise turn every stored `Removed`
        // into an `Added`, which is a product appearing in an order screen a buyer has refused.
        Assert.Equal("Removed", sent.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Another_reps_outlet_overrides_never_arrive()
    {
        // The overrides are exactly as private as the outlets they qualify.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var somebodyElses = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer);
        await OverrideAsync(writer, somebodyElses, productId);

        var device = await BindDeviceAsync(rep);
        var pull = await PullAsync(rep, device);

        Assert.DoesNotContain(
            Upserts(Overrides(pull)), row => row.GetProperty("outletId").GetGuid() == somebodyElses);
    }

    private static async Task<Guid> ChannelAsync(HttpClient writer)
    {
        var channel = await writer.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        return (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    private static async Task<Guid> OutletAsync(HttpClient writer, Guid channelId)
    {
        var created = await writer.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, null));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    private static async Task<Guid> ProductAsync(HttpClient writer)
    {
        var created = await writer.PostAsJsonAsync(
            "/api/products", new { sku = Unique("SKU"), name = "Cola 500ml" });

        Assert.True(
            created.StatusCode == HttpStatusCode.Created,
            $"{created.StatusCode}: {await created.Content.ReadAsStringAsync()}");

        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static Task SetAssortmentAsync(
        HttpClient writer, Guid channelId, Guid product, bool mustStock) =>
        SetLinesAsync(writer, channelId, [new AssortmentLineRequest(product, MustStock: mustStock)]);

    private static Task SetAssortmentAsync(HttpClient writer, Guid channelId, params Guid[] products) =>
        SetLinesAsync(writer, channelId, [.. products.Select(id => new AssortmentLineRequest(id))]);

    private static async Task SetLinesAsync(
        HttpClient writer, Guid channelId, IReadOnlyList<AssortmentLineRequest> lines)
    {
        var response = await writer.PutAsJsonAsync(
            $"/api/products/assortments/channels/{channelId}", new SetAssortmentRequest(lines));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>
    /// Replaces an outlet's overrides. Returns nothing, because the API does not expose the row id
    /// — the pull is where an override first has one, which is why every assertion below matches on
    /// the outlet and product instead.
    /// </summary>
    private static async Task OverrideAsync(
        HttpClient writer, Guid outletId, Guid productId, AssortmentOverrideKind kind = AssortmentOverrideKind.Added)
    {
        var response = await writer.PutAsJsonAsync(
            $"/api/products/assortments/outlets/{outletId}/overrides",
            new SetOverridesRequest([new OverrideLineRequest(productId, kind)]));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>Puts one outlet in the token subject's territory, covering today.</summary>
    private static async Task GiveRepTheOutletAsync(HttpClient admin, HttpClient rep, Guid outletId)
    {
        var me = await rep.GetFromJsonAsync<WhoAmIResponse>("/api/auth/whoami");

        var roles = await admin.GetFromJsonAsync<List<RoleResponse>>("/api/iam/roles");
        await admin.PostAsJsonAsync("/api/iam/users", new
        {
            subjectId = me!.Subject,
            email = $"{Guid.NewGuid():N}@fieldkit.local",
            displayName = "Assortment Sync Rep",
            locale = "en-GB",
            timeZone = Zone,
            roleIds = new[] { roles!.First(role => role.IsSystemTemplate).Id },
        });

        var unit = await admin.PostAsJsonAsync("/api/org/units", new OrgUnitRequest(Unique("Unit")));
        var unitId = (await unit.Content.ReadFromJsonAsync<OrgUnitResponse>())!.Id;

        var territory = await admin.PostAsJsonAsync(
            "/api/org/territories", new TerritoryRequest(Unique("Terr"), unitId));
        var territoryId = (await territory.Content.ReadFromJsonAsync<TerritoryResponse>())!.Id;

        await admin.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/outlets", new AssignOutletsRequest([outletId]));

        var assigned = await admin.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/assignments",
            new RepAssignmentRequest(me.Subject, new DateOnly(2020, 1, 1), null));

        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);
    }

    private sealed record WhoAmIResponse(string Subject);
}
