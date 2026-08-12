using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Products;
using FieldKit.Modules.Sync;

namespace FieldKit.Server.Tests;

/// <summary>
/// Order minimums on the device (<c>OFF-03</c>, <c>ORD-06</c>) — W11 slice 8b-ii.
/// </summary>
/// <remarks>
/// <para>
/// 8b-i gave the server a minimum to read; nothing carried it to a device, which is where
/// <c>BR-ORD-5</c> is actually enforced — "must be met to submit" is a question answered at a counter
/// with no signal.
/// </para>
/// <para>
/// <b>Two things distinguish this feed from the reference feeds beside it</b>, and they are what most
/// of this file is about. The amount is a <c>string</c>, because a threshold is the one figure here
/// where being out by a hundredth decides whether a rep may send at all. And the tombstones carry
/// more weight than the row count suggests: the authoring PUT replaces the whole set, so *every*
/// edit is a delete-and-recreate, and a device that only upserted would go on enforcing a withdrawn
/// threshold while looking exactly like the rule working.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class SyncPullOrderMinimumTests(ServerFixture fixture)
{
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    /// <summary>Channels and outlets belong to Outlets, whose writes the `rep` fixture lacks.</summary>
    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    private static async Task<Guid> BindDeviceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/devices", new BindDeviceRequest(Unique("Phone")));

        return (await response.Content.ReadFromJsonAsync<DeviceResponse>())!.Id;
    }

    private static async Task<JsonElement> PullAsync(
        HttpClient client, Guid deviceId, long? orderMinimums = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/pull",
            new PullRequest(deviceId, new PullCursors(null, OrderMinimums: orderMinimums)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static JsonElement Section(JsonElement pull) =>
        pull.GetProperty("changes").GetProperty("orderMinimums");

    private static List<JsonElement> Upserts(JsonElement pull) =>
        [.. Section(pull).GetProperty("upserts").EnumerateArray()];

    private static List<JsonElement> Tombstones(JsonElement pull) =>
        [.. Section(pull).GetProperty("tombstones").EnumerateArray()];

    private static long Cursor(JsonElement pull) => Section(pull).GetProperty("cursor").GetInt64();

    private static async Task<Guid> ChannelAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Modern")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    private static async Task<Guid> OutletAsync(HttpClient client, Guid channelId)
    {
        var response = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, "Europe/Bucharest"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    private static async Task SetAsync(HttpClient client, params OrderMinimumRequest[] minimums)
    {
        var response = await client.PutAsJsonAsync(
            "/api/products/order-minimums", new SetOrderMinimumsRequest(minimums));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The one row this test wrote, out of a shared tenant's whole set.</summary>
    private static JsonElement Mine(JsonElement pull, Guid scopeId, string scope)
    {
        var mine = Upserts(pull)
            .Where(upsert =>
                upsert.GetProperty(scope).ValueKind is not JsonValueKind.Null
                && upsert.GetProperty(scope).GetGuid() == scopeId)
            .ToList();

        return Assert.Single(mine);
    }

    [Fact]
    public async Task A_minimum_reaches_the_device_with_its_amount_as_a_string()
    {
        /*
         * The whole point of the slice, and of `WireDecimal` on this feed.
         *
         * A bare `150.00` would be a JSON number, and `JSON.parse` makes an IEEE-754 float of it
         * before `decimal.js` is handed anything. Everywhere else on the wire that costs a
         * hundredth; here it decides whether a rep may send their order at all.
         */
        using var client = fixture.CreateAuthenticatedClient();
        using var admin = Admin();

        var channelId = await ChannelAsync(admin);

        await SetAsync(client, new OrderMinimumRequest(channelId, null, "150.00", "RON"));

        var deviceId = await BindDeviceAsync(client);
        var minimum = Mine(await PullAsync(client, deviceId), channelId, "channelId");

        Assert.Equal(JsonValueKind.String, minimum.GetProperty("amount").ValueKind);
        Assert.Equal("150.00", minimum.GetProperty("amount").GetString());
        Assert.Equal("RON", minimum.GetProperty("currencyCode").GetString());
        Assert.Equal(JsonValueKind.Null, minimum.GetProperty("outletId").ValueKind);
    }

    [Fact]
    public async Task The_currency_travels_so_the_device_can_tell_two_minimums_apart()
    {
        /*
         * `BR-ORD-7`: an order's currency comes from the list that priced it, and nothing makes that
         * agree with the currency somebody typed into a minimum. Comparing 50 EUR against 200 RON by
         * their numbers alone would refuse orders comfortably over the threshold — so the number
         * cannot travel alone.
         */
        using var client = fixture.CreateAuthenticatedClient();
        using var admin = Admin();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);

        await SetAsync(
            client,
            new OrderMinimumRequest(channelId, null, "200.00", "RON"),
            new OrderMinimumRequest(null, outletId, "50.00", "EUR"));

        var pull = await PullAsync(client, await BindDeviceAsync(client));

        Assert.Equal("RON", Mine(pull, channelId, "channelId").GetProperty("currencyCode").GetString());
        Assert.Equal("EUR", Mine(pull, outletId, "outletId").GetProperty("currencyCode").GetString());
    }

    [Fact]
    public async Task Exactly_one_scope_id_is_set_which_is_how_the_device_ranks_them()
    {
        /*
         * The device picks outlet over channel, and it has nothing but these two fields to do it
         * with. A row with both would be a rule with two scopes — the check constraint refuses one,
         * and this asserts the wire keeps that shape rather than, say, filling the channel in from
         * the outlet as a convenience.
         */
        using var client = fixture.CreateAuthenticatedClient();
        using var admin = Admin();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);

        await SetAsync(client, new OrderMinimumRequest(null, outletId, "50.00", "RON"));

        var minimum = Mine(await PullAsync(client, await BindDeviceAsync(client)), outletId, "outletId");

        Assert.Equal(JsonValueKind.Null, minimum.GetProperty("channelId").ValueKind);
    }

    [Fact]
    public async Task A_device_that_has_pulled_gets_only_what_changed_since()
    {
        /*
         * The delta arm, tested on its own — a slice of this shape has been shipped before whose
         * every test pulled once from zero, which exercises the baseline and leaves the arm the
         * device actually spends its life in unproven.
         */
        using var client = fixture.CreateAuthenticatedClient();
        using var admin = Admin();

        var channelId = await ChannelAsync(admin);

        await SetAsync(client, new OrderMinimumRequest(channelId, null, "150.00", "RON"));

        var deviceId = await BindDeviceAsync(client);
        var cursor = Cursor(await PullAsync(client, deviceId));

        var second = await PullAsync(client, deviceId, cursor);

        Assert.Empty(Upserts(second));
        Assert.Empty(Tombstones(second));
        Assert.Equal(cursor, Cursor(second));

        var otherChannelId = await ChannelAsync(admin);

        await SetAsync(
            client,
            new OrderMinimumRequest(channelId, null, "150.00", "RON"),
            new OrderMinimumRequest(otherChannelId, null, "80.00", "RON"));

        var third = await PullAsync(client, deviceId, cursor);

        Assert.Equal("80.00", Mine(third, otherChannelId, "channelId").GetProperty("amount").GetString());
        Assert.True(Cursor(third) > cursor);
    }

    [Fact]
    public async Task A_withdrawn_minimum_reaches_the_device_as_a_tombstone()
    {
        /*
         * The failure this feed's tombstones exist for, and the worst one it has: a device that only
         * upserted would keep refusing orders against a threshold its tenant deleted, silently, and
         * looking exactly like the rule working.
         *
         * Note the cursor comes from the *first* pull — replaying from a stale watermark is what a
         * device does after a failed drain, and the tombstone has to still be there.
         */
        using var client = fixture.CreateAuthenticatedClient();
        using var admin = Admin();

        var channelId = await ChannelAsync(admin);

        await SetAsync(client, new OrderMinimumRequest(channelId, null, "150.00", "RON"));

        var deviceId = await BindDeviceAsync(client);
        var first = await PullAsync(client, deviceId);
        var id = Mine(first, channelId, "channelId").GetProperty("id").GetGuid();

        await SetAsync(client);

        var second = await PullAsync(client, deviceId, Cursor(first));

        Assert.Contains(
            Tombstones(second), tombstone => tombstone.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task Correcting_one_figure_arrives_as_a_tombstone_and_a_new_row()
    {
        /*
         * The ordinary edit, and the reason the tombstones above are not an edge case. The authoring
         * PUT replaces the whole set (8b-i), so raising 150 to 200 deletes the row and writes a new
         * one with a new id — a device that applied only the upsert would hold both and enforce
         * whichever it happened to rank first.
         */
        using var client = fixture.CreateAuthenticatedClient();
        using var admin = Admin();

        var channelId = await ChannelAsync(admin);

        await SetAsync(client, new OrderMinimumRequest(channelId, null, "150.00", "RON"));

        var deviceId = await BindDeviceAsync(client);
        var first = await PullAsync(client, deviceId);
        var oldId = Mine(first, channelId, "channelId").GetProperty("id").GetGuid();

        await SetAsync(client, new OrderMinimumRequest(channelId, null, "200.00", "RON"));

        var second = await PullAsync(client, deviceId, Cursor(first));
        var replacement = Mine(second, channelId, "channelId");

        Assert.Equal("200.00", replacement.GetProperty("amount").GetString());
        Assert.NotEqual(oldId, replacement.GetProperty("id").GetGuid());
        Assert.Contains(
            Tombstones(second), tombstone => tombstone.GetProperty("id").GetGuid() == oldId);
    }
}
