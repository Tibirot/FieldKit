using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Org;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Sync;

namespace FieldKit.Server.Tests;

/// <summary>
/// The reference delta: <c>/sync/pull</c> for outlets (<c>OFF-03</c>, sync engine §3).
/// </summary>
/// <remarks>
/// <para>
/// This is the first slice where W8's parts have to agree with each other. The row version
/// (ADR-0013) decides what is new, the tombstone table decides what is gone, and the device
/// registry decides whether the caller may ask at all — each was testable alone and none of them
/// proved the protocol.
/// </para>
/// <para>
/// The rep token is used because scope is the rep's: <c>IRepScope</c> answers for the subject in
/// the token, and a pull scoped to somebody else is the bug these are guarding.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class SyncPullTests(ServerFixture fixture)
{
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private static async Task<Guid> BindDeviceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/devices", new BindDeviceRequest(Unique("Phone")));

        return (await response.Content.ReadFromJsonAsync<DeviceResponse>())!.Id;
    }

    private static async Task<PullResponse> PullAsync(HttpClient client, Guid deviceId, long? cursor = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/pull", new PullRequest(deviceId, new PullCursors(cursor)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PullResponse>())!;
    }

    [Fact]
    public async Task An_unknown_device_is_refused_before_anything_is_resolved()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var response = await rep.PostAsJsonAsync(
            "/api/sync/pull", new PullRequest(Guid.CreateVersion7(), new PullCursors(0)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("sync.pull.deviceUnknown", Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task Another_reps_device_is_refused_as_unknown_not_as_forbidden()
    {
        // Same answer as "no such device", deliberately: telling the difference would let a caller
        // enumerate device ids by watching 403 and 404 diverge.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var someoneElses = await BindDeviceAsync(admin);

        var response = await rep.PostAsJsonAsync(
            "/api/sync/pull", new PullRequest(someoneElses, new PullCursors(0)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("sync.pull.deviceUnknown", Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task A_replaced_device_is_told_to_bind_again_rather_than_given_data()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var old = await BindDeviceAsync(rep);
        await BindDeviceAsync(rep); // the swap deactivates `old`

        var response = await rep.PostAsJsonAsync(
            "/api/sync/pull", new PullRequest(old, new PullCursors(0)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("sync.pull.deviceInactive", Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task Pulling_needs_a_token()
    {
        var response = await fixture.Client.PostAsJsonAsync(
            "/api/sync/pull", new PullRequest(Guid.CreateVersion7(), new PullCursors(0)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_rep_with_no_territory_gets_an_empty_page_not_the_tenants_outlets()
    {
        // The failure this guards is a filter that degrades to "no filter" on an empty scope, which
        // hands an unassigned rep every outlet in the tenant. It is one `if` away at all times.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        await OutletAsync(admin);
        var device = await BindDeviceAsync(rep);

        var pull = await PullAsync(rep, device);

        Assert.Empty(pull.Changes.Outlets.Upserts);
        Assert.Equal(0, pull.Changes.Outlets.Cursor);
    }

    [Fact]
    public async Task The_cursor_only_advances_as_far_as_the_rows_actually_sent()
    {
        // A cursor reporting the table's high-water mark rather than the page's would skip every
        // row between the last one sent and that mark — permanently, and only for devices far
        // enough behind to fill a page.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var device = await BindDeviceAsync(rep);
        var first = await PullAsync(rep, device);
        var second = await PullAsync(rep, device, first.Changes.Outlets.Cursor);

        // Nothing changed in between, so the second pull carries nothing and does not move.
        Assert.Empty(second.Changes.Outlets.Upserts);
        Assert.Equal(first.Changes.Outlets.Cursor, second.Changes.Outlets.Cursor);
    }

    [Fact]
    public async Task The_snapshot_version_carries_the_cursor_it_was_taken_at()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var device = await BindDeviceAsync(rep);
        var pull = await PullAsync(rep, device);

        Assert.EndsWith($"#{pull.Changes.Outlets.Cursor}", pull.SnapshotVersion);
    }

    [Fact]
    public async Task An_outlet_in_the_reps_territory_reaches_the_device()
    {
        // The test the other seven do not make: every one of them asserts a refusal or an empty
        // page, which a pull that returned nothing at all would satisfy. This is the slice.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outletId = await OutletAsync(admin);
        await GiveRepTheTerritoryAsync(admin, rep, outletId);

        var device = await BindDeviceAsync(rep);
        var pull = await PullAsync(rep, device);

        var delivered = Assert.Single(pull.Changes.Outlets.Upserts, outlet => outlet.Id == outletId);
        Assert.True(delivered.RowVersion > 0, "the outlet arrived unstamped, so no delta could order it");
        Assert.Equal(delivered.RowVersion, pull.Changes.Outlets.Cursor);
    }

    [Fact]
    public async Task A_second_pull_at_the_cursor_carries_only_what_changed_since()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outletId = await OutletAsync(admin);
        await GiveRepTheTerritoryAsync(admin, rep, outletId);

        var device = await BindDeviceAsync(rep);
        var first = await PullAsync(rep, device);

        // Nothing has changed, so the device is told nothing and stays where it is.
        var second = await PullAsync(rep, device, first.Changes.Outlets.Cursor);

        Assert.Empty(second.Changes.Outlets.Upserts);
        Assert.Equal(first.Changes.Outlets.Cursor, second.Changes.Outlets.Cursor);
    }

    /// <summary>
    /// Assembles what a rep needs to cover an outlet today: a unit, a territory holding it, and an
    /// assignment covering today — against the subject in the token rather than a synthetic rep,
    /// because the pull scopes to whoever the token says is asking.
    /// </summary>
    private static async Task GiveRepTheTerritoryAsync(HttpClient admin, HttpClient rep, Guid outletId)
    {
        var me = await rep.GetFromJsonAsync<WhoAmIResponse>("/api/auth/whoami");

        // A FieldKit profile for the token's subject. Authenticating proves who you are; being
        // assignable is a row in IAM, and an assignment to a subject with no profile is refused
        // with "No such user in this tenant" — which is how this fixture first failed.
        var roles = await admin.GetFromJsonAsync<List<RoleResponse>>("/api/iam/roles");
        await admin.PostAsJsonAsync("/api/iam/users", new
        {
            subjectId = me!.Subject,
            email = $"{Guid.NewGuid():N}@fieldkit.local",
            displayName = "Sync Fixture Rep",
            locale = "en-GB",
            timeZone = "Europe/Bucharest",
            roleIds = new[] { roles!.First(role => role.IsSystemTemplate).Id },
        });

        var unit = await admin.PostAsJsonAsync("/api/org/units", new OrgUnitRequest(Unique("Unit")));
        var unitId = (await unit.Content.ReadFromJsonAsync<OrgUnitResponse>())!.Id;

        var territory = await admin.PostAsJsonAsync(
            "/api/org/territories", new TerritoryRequest(Unique("Terr"), unitId));
        var territoryId = (await territory.Content.ReadFromJsonAsync<TerritoryResponse>())!.Id;

        await admin.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/outlets", new AssignOutletsRequest([outletId]));

        // Open-ended from a fixed date in the past. The pull asks IRepScope for *today*, so a window
        // that starts tomorrow or ended yesterday produces exactly the empty page the other tests
        // accept — and a window computed from the wall clock would make the fixture itself the thing
        // that decides, which is what this project's ban on static time exists to prevent.
        var assigned = await admin.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/assignments",
            new RepAssignmentRequest(me.Subject, new DateOnly(2020, 1, 1), null));

        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);
    }

    private sealed record WhoAmIResponse(string Subject);

    private static async Task<Guid> OutletAsync(HttpClient client)
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));
        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var response = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, "Europe/Bucharest", null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }
}
