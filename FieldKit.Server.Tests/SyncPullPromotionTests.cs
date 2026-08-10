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
/// Promotions on the device (<c>OFF-03</c>, W8 slice 8f) — the last reference entity.
/// </summary>
/// <remarks>
/// <para>
/// The interesting case is <see cref="Editing_only_the_tiers_still_reaches_the_device"/>. Targets and
/// tiers travel <i>inside</i> the promotion, so the row version lives on the root — and until this
/// slice, the endpoints that set them never touched the root at all. A device would have gone on
/// computing yesterday's discount with nothing looking wrong.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class SyncPullPromotionTests(ServerFixture fixture)
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
        HttpClient client, Guid deviceId, long? promotions = null, long? assignments = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/pull",
            new PullRequest(
                deviceId,
                new PullCursors(
                    null, null, null, null, null, null, null, null, null, promotions, assignments)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static JsonElement Page(JsonElement pull, string name) =>
        pull.GetProperty("changes").GetProperty(name);

    private static List<JsonElement> Upserts(JsonElement page) =>
        [.. page.GetProperty("upserts").EnumerateArray()];

    [Fact]
    public async Task A_promotion_arrives_whole_with_its_tiers_in_order()
    {
        // A device holding four of five tiers does not fail — it computes a different discount, and
        // neither the rep nor the shop can tell. Which is why the aggregate travels as one row.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var promotionId = await VolumePromotionAsync(writer);
        await SetTiersAsync(writer, promotionId, (6, 5m), (12, 10m));

        var device = await BindDeviceAsync(rep);
        var pull = await PullAsync(rep, device);

        var sent = Assert.Single(Upserts(Page(pull, "promotions")), row =>
            row.GetProperty("id").GetGuid() == promotionId);

        // By name, never by ordinal: an inserted enum value would turn a percentage into a fixed
        // amount on every device already holding it.
        Assert.Equal("VolumeTiered", sent.GetProperty("type").GetString());

        var tiers = sent.GetProperty("tiers").EnumerateArray().ToList();
        Assert.Equal(2, tiers.Count);
        Assert.Equal(6, tiers[0].GetProperty("minQuantity").GetInt32());
        Assert.Equal(12, tiers[1].GetProperty("minQuantity").GetInt32());
    }

    [Fact]
    public async Task Editing_only_the_tiers_still_reaches_the_device()
    {
        // The bug this slice found. `PUT /tiers` writes the tier table and — until now — never
        // touched the promotion row, so the row version never moved and the change never arrived.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var promotionId = await VolumePromotionAsync(writer);
        await SetTiersAsync(writer, promotionId, (6, 5m));

        var device = await BindDeviceAsync(rep);
        var first = await PullAsync(rep, device);
        var cursor = Page(first, "promotions").GetProperty("cursor").GetInt64();

        await SetTiersAsync(writer, promotionId, (6, 5m), (24, 15m));

        var second = await PullAsync(rep, device, cursor);

        var updated = Assert.Single(Upserts(Page(second, "promotions")), row =>
            row.GetProperty("id").GetGuid() == promotionId);

        Assert.Equal(2, updated.GetProperty("tiers").GetArrayLength());
        Assert.True(Page(second, "promotions").GetProperty("cursor").GetInt64() > cursor);
    }

    [Fact]
    public async Task Editing_only_the_targets_still_reaches_the_device()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var productId = await ProductAsync(writer);
        var promotionId = await VolumePromotionAsync(writer);

        var device = await BindDeviceAsync(rep);
        var first = await PullAsync(rep, device);
        var cursor = Page(first, "promotions").GetProperty("cursor").GetInt64();

        var targeted = await writer.PutAsJsonAsync(
            $"/api/products/promotions/{promotionId}/targets",
            new SetPromotionTargetsRequest([productId], []));
        Assert.Equal(HttpStatusCode.OK, targeted.StatusCode);

        var second = await PullAsync(rep, device, cursor);

        var updated = Assert.Single(Upserts(Page(second, "promotions")), row =>
            row.GetProperty("id").GetGuid() == promotionId);

        var target = Assert.Single(updated.GetProperty("targets").EnumerateArray());
        Assert.Equal(productId, target.GetProperty("productId").GetGuid());
    }

    [Fact]
    public async Task An_outlet_assignment_only_reaches_the_rep_who_covers_that_shop()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var somebodyElses = await OutletAsync(admin, channelId);
        var promotionId = await VolumePromotionAsync(writer);
        await AssignAsync(writer, promotionId, [], [somebodyElses]);

        var device = await BindDeviceAsync(rep);
        var pull = await PullAsync(rep, device);

        Assert.DoesNotContain(
            Upserts(Page(pull, "promotionAssignments")),
            row => row.TryGetProperty("outletId", out var outlet)
                && outlet.ValueKind is not JsonValueKind.Null
                && outlet.GetGuid() == somebodyElses);
    }

    [Fact]
    public async Task An_outlet_entering_the_territory_brings_an_assignment_written_long_ago()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var promotionId = await VolumePromotionAsync(writer);
        await AssignAsync(writer, promotionId, [], [outletId]);

        var device = await BindDeviceAsync(rep);
        var before = await PullAsync(rep, device);

        await GiveRepTheOutletAsync(admin, rep, outletId);

        var after = await PullAsync(
            rep,
            device,
            assignments: Page(before, "promotionAssignments").GetProperty("cursor").GetInt64());

        Assert.Contains(
            Upserts(Page(after, "promotionAssignments")),
            row => row.TryGetProperty("outletId", out var outlet)
                && outlet.ValueKind is not JsonValueKind.Null
                && outlet.GetGuid() == outletId);
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

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> VolumePromotionAsync(HttpClient writer)
    {
        var created = await writer.PostAsJsonAsync(
            "/api/products/promotions",
            new
            {
                name = Unique("Promo"),
                type = "VolumeTiered",
                validFrom = "2026-01-01",
                priority = 1,
            });

        Assert.True(
            created.StatusCode == HttpStatusCode.Created,
            $"{created.StatusCode}: {await created.Content.ReadAsStringAsync()}");

        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task SetTiersAsync(
        HttpClient writer, Guid promotionId, params (int MinQuantity, decimal PercentOff)[] tiers)
    {
        var response = await writer.PutAsJsonAsync(
            $"/api/products/promotions/{promotionId}/tiers",
            new SetPromotionTiersRequest(
                [.. tiers.Select(tier => new PromotionTierRequest(
                    tier.MinQuantity, tier.PercentOff.ToString(CultureInfo.InvariantCulture), null))]));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task AssignAsync(
        HttpClient writer, Guid promotionId, Guid[] channelIds, Guid[] outletIds)
    {
        var response = await writer.PutAsJsonAsync(
            $"/api/products/promotions/{promotionId}/assignments",
            new SetPromotionScopeRequest(channelIds, outletIds));

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
            displayName = "Promotion Sync Rep",
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
