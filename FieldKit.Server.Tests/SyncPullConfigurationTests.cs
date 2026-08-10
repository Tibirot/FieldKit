using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Sync;

namespace FieldKit.Server.Tests;

/// <summary>
/// Visit workflows on the device: configuration in <c>/sync/pull</c> (<c>OFF-03</c>, W8 slice 8b).
/// </summary>
/// <remarks>
/// <para>
/// The third entity type, and the third distinct answer to "whose row is it" — <b>nobody's</b>.
/// Every device in the tenant gets every workflow, which is why nothing here sets up a territory or
/// a plan: there is no scope to get wrong.
/// </para>
/// <para>
/// It is also the first feed whose tombstones are both produced and sendable. An administrator can
/// delete a workflow, and the resulting tombstone is tenant-wide, so it tells nobody anything about
/// anybody.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class SyncPullConfigurationTests(ServerFixture fixture)
{
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private static async Task<Guid> BindDeviceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/devices", new BindDeviceRequest(Unique("Phone")));

        return (await response.Content.ReadFromJsonAsync<DeviceResponse>())!.Id;
    }

    private static async Task<JsonElement> PullAsync(
        HttpClient client, Guid deviceId, long? configuration = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/pull", new PullRequest(deviceId, new PullCursors(null, null, configuration)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static JsonElement Config(JsonElement pull) =>
        pull.GetProperty("changes").GetProperty("configuration");

    private static long Cursor(JsonElement pull) => Config(pull).GetProperty("cursor").GetInt64();

    private static List<JsonElement> Workflows(JsonElement pull) =>
        [.. Config(pull).GetProperty("upserts").EnumerateArray()];

    private static List<JsonElement> Tombstones(JsonElement pull) =>
        [.. Config(pull).GetProperty("tombstones").EnumerateArray()];

    private static async Task<Guid> ChannelAsync(HttpClient admin)
    {
        var channel = await admin.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        return (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    private static async Task ConfigureAsync(
        HttpClient admin, Guid channelId, bool presenceExpected = true, params VisitStepRequest[] steps)
    {
        var response = await admin.PutAsJsonAsync(
            $"/api/config/visit-workflows/{channelId}",
            new VisitWorkflowRequest(presenceExpected, steps));

        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task A_workflow_arrives_whole_with_its_steps_in_order()
    {
        // The steps travel inside the workflow rather than as a fourth entity type. A device holding
        // four of five would run a visit asking for less than the tenant configured, and BR-VIS-3
        // would gate check-out on a mandatory step it never received.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var channelId = await ChannelAsync(admin);
        await ConfigureAsync(
            admin,
            channelId,
            presenceExpected: false,
            new VisitStepRequest(VisitStepType.Audit, true, "Shelf check"),
            new VisitStepRequest(VisitStepType.Note, false, "Anything else"));

        var device = await BindDeviceAsync(rep);
        var pull = await PullAsync(rep, device);

        var sent = Assert.Single(Workflows(pull), workflow =>
            workflow.GetProperty("channelId").GetGuid() == channelId);

        Assert.False(sent.GetProperty("presenceExpected").GetBoolean());
        Assert.True(sent.GetProperty("rowVersion").GetInt64() > 0);

        var steps = sent.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(2, steps.Count);
        Assert.Equal(1, steps[0].GetProperty("order").GetInt32());

        // By name, never by ordinal: inserting a value into the middle of `VisitStepType` would
        // otherwise silently reinterpret every workflow already stored on every device.
        Assert.Equal("Audit", steps[0].GetProperty("type").GetString());
        Assert.True(steps[0].GetProperty("mandatory").GetBoolean());
        Assert.Equal("Note", steps[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_second_pull_at_the_cursor_carries_nothing()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var channelId = await ChannelAsync(admin);
        await ConfigureAsync(admin, channelId, true, new VisitStepRequest(VisitStepType.Task, true, "Chiller"));

        var device = await BindDeviceAsync(rep);
        var first = await PullAsync(rep, device);
        Assert.Contains(Workflows(first), w => w.GetProperty("channelId").GetGuid() == channelId);

        var second = await PullAsync(rep, device, Cursor(first));

        Assert.Empty(Workflows(second));
        Assert.Equal(Cursor(first), Cursor(second));
    }

    [Fact]
    public async Task Editing_a_step_alone_still_reaches_the_device()
    {
        // The row version is on the workflow, not the step — so this is the case that would break if
        // the aggregate ever gained an edit path that did not touch the root.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var channelId = await ChannelAsync(admin);
        await ConfigureAsync(admin, channelId, true, new VisitStepRequest(VisitStepType.Task, true, "Chiller"));

        var device = await BindDeviceAsync(rep);
        var first = await PullAsync(rep, device);

        // Same presence flag, same step count: only the label moves.
        await ConfigureAsync(
            admin, channelId, true, new VisitStepRequest(VisitStepType.Task, true, "Check the chiller is lit"));

        var second = await PullAsync(rep, device, Cursor(first));

        var updated = Assert.Single(Workflows(second), w =>
            w.GetProperty("channelId").GetGuid() == channelId);

        Assert.Equal(
            "Check the chiller is lit",
            updated.GetProperty("steps").EnumerateArray().First().GetProperty("label").GetString());
        Assert.True(Cursor(second) > Cursor(first));
    }

    [Fact]
    public async Task A_deleted_workflow_arrives_as_a_tombstone()
    {
        // The first feed whose tombstones are both produced *and* sendable: a workflow is
        // tenant-wide, so telling every device it is gone leaks nothing about anybody.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var channelId = await ChannelAsync(admin);
        await ConfigureAsync(admin, channelId, true, new VisitStepRequest(VisitStepType.Task, true, "Chiller"));

        var device = await BindDeviceAsync(rep);
        var first = await PullAsync(rep, device);

        var workflowId = Assert.Single(Workflows(first), w =>
            w.GetProperty("channelId").GetGuid() == channelId).GetProperty("id").GetGuid();

        var deleted = await admin.DeleteAsync($"/api/config/visit-workflows/{channelId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var second = await PullAsync(rep, device, Cursor(first));

        Assert.Single(Tombstones(second), tombstone => tombstone.GetProperty("id").GetGuid() == workflowId);
    }

    [Fact]
    public async Task Every_rep_gets_every_workflow_because_there_is_nothing_to_scope_by()
    {
        // Deliberately asserted rather than assumed. Narrowing to the channels of a rep's outlets
        // would reintroduce the membership problem the outlet baseline exists to work around — a
        // shop moving channel would put a workflow in scope without editing it — to save a payload
        // of rows the tenant's own administrators wrote.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        // A channel with no outlets at all, so nothing could possibly bring it into a rep's scope.
        var orphan = await ChannelAsync(admin);
        await ConfigureAsync(admin, orphan, true, new VisitStepRequest(VisitStepType.Survey, false, "Ask about stock"));

        var device = await BindDeviceAsync(rep);
        var pull = await PullAsync(rep, device);

        Assert.Contains(Workflows(pull), workflow =>
            workflow.GetProperty("channelId").GetGuid() == orphan);
    }

    [Fact]
    public async Task The_configuration_cursor_moves_on_its_own()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var channelId = await ChannelAsync(admin);
        await ConfigureAsync(admin, channelId, true, new VisitStepRequest(VisitStepType.Task, true, "Chiller"));

        var device = await BindDeviceAsync(rep);
        var first = await PullAsync(rep, device);

        // A new outlet moves the *outlet* counter, in a different schema and on a different cursor.
        var created = await admin.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, "Europe/Bucharest", null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var second = await PullAsync(rep, device, Cursor(first));

        Assert.Empty(Workflows(second));
        Assert.Equal(Cursor(first), Cursor(second));
    }
}
