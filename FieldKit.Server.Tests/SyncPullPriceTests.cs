using System.Globalization;
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
/// Prices on the device: lists, lines and assignments in <c>/sync/pull</c> (<c>OFF-03</c>, W8 8e).
/// </summary>
/// <remarks>
/// <para>
/// Three shapes, split along the same line as the assortment's: lists and lines are tenant-wide,
/// assignments are channel-or-outlet and therefore scoped-or-not one row at a time.
/// </para>
/// <para>
/// The case worth reading first is <see cref="A_rep_with_no_territory_still_gets_the_channel_policy"/>.
/// An empty outlet set means "nothing" for assortment overrides and does <b>not</b> mean nothing
/// here, and that difference is the only thing this feed does not share with 8d's.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class SyncPullPriceTests(ServerFixture fixture)
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
        HttpClient client, Guid deviceId, long? lists = null, long? lines = null, long? assignments = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/pull",
            new PullRequest(
                deviceId, new PullCursors(null, null, null, null, null, null, lists, lines, assignments)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static JsonElement Page(JsonElement pull, string name) =>
        pull.GetProperty("changes").GetProperty(name);

    private static List<JsonElement> Upserts(JsonElement page) =>
        [.. page.GetProperty("upserts").EnumerateArray()];

    [Fact]
    public async Task A_list_and_its_lines_reach_the_device_and_the_next_pull_carries_nothing()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var productId = await ProductAsync(writer);
        var listId = await PriceListAsync(writer);
        await SetPriceAsync(writer, listId, productId, 12.5m);

        var device = await BindDeviceAsync(rep);
        var first = await PullAsync(rep, device);

        var list = Assert.Single(Upserts(Page(first, "priceLists")), row =>
            row.GetProperty("id").GetGuid() == listId);

        Assert.Equal("RON", list.GetProperty("currency").GetString());
        Assert.True(list.GetProperty("rowVersion").GetInt64() > 0);

        var line = Assert.Single(Upserts(Page(first, "priceLines")), row =>
            row.GetProperty("priceListId").GetGuid() == listId);

        /*
         * A **string**, and the assertion says so rather than reading a decimal out of it (W11
         * slice 7a).
         *
         * This used to be `GetDecimal()`, which passes against a JSON number and a JSON string alike
         * — so it could not see the thing that was wrong: `JSON.parse` in the device turns a bare
         * `12.5` into an IEEE-754 float before `decimal.js` is ever handed it, which defeats the
         * whole of the pricing engine's exactness. `GetString()` is what pins the wire form.
         *
         * "12.50" rather than "12.5": the shape `ScoreWeightSnapshot` uses, and what a price list
         * showing 12.5 would read as to anyone who works with prices.
         */
        Assert.Equal("12.50", line.GetProperty("amount").GetString());

        var second = await PullAsync(
            rep,
            device,
            Page(first, "priceLists").GetProperty("cursor").GetInt64(),
            Page(first, "priceLines").GetProperty("cursor").GetInt64());

        Assert.Empty(Upserts(Page(second, "priceLists")));
        Assert.Empty(Upserts(Page(second, "priceLines")));
    }

    [Fact]
    public async Task A_rep_with_no_territory_still_gets_the_channel_policy()
    {
        // The one place this feed differs from the assortment's. An empty outlet set means "nothing
        // to send" for overrides; here it must not, because the shops this rep is given tomorrow are
        // priced by the channel policy that exists today.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var listId = await PriceListAsync(writer);
        await AssignToChannelAsync(writer, listId, channelId);

        var device = await BindDeviceAsync(rep);
        var pull = await PullAsync(rep, device);

        Assert.Contains(
            Upserts(Page(pull, "priceAssignments")),
            row => row.GetProperty("id").GetGuid() != Guid.Empty
                && row.TryGetProperty("channelId", out var channel)
                && channel.ValueKind is not JsonValueKind.Null
                && channel.GetGuid() == channelId);
    }

    [Fact]
    public async Task An_outlet_assignment_only_reaches_the_rep_who_covers_that_shop()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var somebodyElses = await OutletAsync(admin, channelId);
        var listId = await PriceListAsync(writer);
        await AssignToOutletAsync(writer, listId, somebodyElses);

        var device = await BindDeviceAsync(rep);
        var pull = await PullAsync(rep, device);

        Assert.DoesNotContain(
            Upserts(Page(pull, "priceAssignments")),
            row => row.TryGetProperty("outletId", out var outlet)
                && outlet.ValueKind is not JsonValueKind.Null
                && outlet.GetGuid() == somebodyElses);
    }

    [Fact]
    public async Task An_outlet_entering_the_territory_brings_an_assignment_written_long_ago()
    {
        // The baseline half. The assignment's row version was stamped before the device existed, so
        // only the fact that its outlet has just entered scope can deliver it.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var listId = await PriceListAsync(writer);
        await AssignToOutletAsync(writer, listId, outletId);

        var device = await BindDeviceAsync(rep);

        var before = await PullAsync(rep, device);
        Assert.DoesNotContain(
            Upserts(Page(before, "priceAssignments")),
            row => row.TryGetProperty("outletId", out var outlet)
                && outlet.ValueKind is not JsonValueKind.Null
                && outlet.GetGuid() == outletId);

        await GiveRepTheOutletAsync(admin, rep, outletId);

        var after = await PullAsync(
            rep, device, assignments: Page(before, "priceAssignments").GetProperty("cursor").GetInt64());

        Assert.Contains(
            Upserts(Page(after, "priceAssignments")),
            row => row.TryGetProperty("outletId", out var outlet)
                && outlet.ValueKind is not JsonValueKind.Null
                && outlet.GetGuid() == outletId);
    }

    [Fact]
    public async Task The_three_price_cursors_move_independently()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var productId = await ProductAsync(writer);
        var listId = await PriceListAsync(writer);

        var device = await BindDeviceAsync(rep);
        var first = await PullAsync(rep, device);

        // Add a price. The line cursor must move; the list cursor must not.
        await SetPriceAsync(writer, listId, productId, 9.99m);

        var second = await PullAsync(
            rep,
            device,
            Page(first, "priceLists").GetProperty("cursor").GetInt64(),
            Page(first, "priceLines").GetProperty("cursor").GetInt64());

        Assert.NotEmpty(Upserts(Page(second, "priceLines")));
        Assert.Empty(Upserts(Page(second, "priceLists")));
    }

    private static async Task<Guid> ChannelAsync(HttpClient admin)
    {
        var channel = await admin.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        return (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    private static async Task<Guid> OutletAsync(HttpClient admin, Guid channelId)
    {
        var created = await admin.PostAsJsonAsync(
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

    private static async Task<Guid> PriceListAsync(HttpClient writer)
    {
        var created = await writer.PostAsJsonAsync(
            "/api/products/price-lists",
            new CreatePriceListRequest(Unique("List"), "RON", new DateOnly(2026, 1, 1), null));

        Assert.True(
            created.StatusCode == HttpStatusCode.Created,
            $"{created.StatusCode}: {await created.Content.ReadAsStringAsync()}");

        return (await created.Content.ReadFromJsonAsync<PriceListResponse>())!.Id;
    }

    private static async Task SetPriceAsync(HttpClient writer, Guid listId, Guid productId, decimal amount)
    {
        var response = await writer.PutAsJsonAsync(
            $"/api/products/price-lists/{listId}/prices",
            new SetPricesRequest([new PriceLineRequest(productId, amount.ToString(CultureInfo.InvariantCulture))]));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static Task AssignToChannelAsync(HttpClient writer, Guid listId, Guid channelId) =>
        AssignAsync(writer, listId, new SetAssignmentsRequest([channelId], []));

    private static Task AssignToOutletAsync(HttpClient writer, Guid listId, Guid outletId) =>
        AssignAsync(writer, listId, new SetAssignmentsRequest([], [outletId]));

    /// <summary>A PUT replaces a list's assignments — see the endpoint.</summary>
    private static async Task AssignAsync(HttpClient writer, Guid listId, SetAssignmentsRequest body)
    {
        var response = await writer.PutAsJsonAsync(
            $"/api/products/price-lists/{listId}/assignments", body);

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
            displayName = "Price Sync Rep",
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
