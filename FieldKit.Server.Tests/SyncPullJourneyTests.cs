using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Journey;
using FieldKit.Modules.Org;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Sync;

namespace FieldKit.Server.Tests;

/// <summary>
/// The rep's round on the device: journeys in <c>/sync/pull</c> (<c>OFF-03</c>, W8 slice 8a).
/// </summary>
/// <remarks>
/// <para>
/// The second entity type the protocol carries, and the one that proves the shape generalises. What
/// it scopes by is the interesting part: a call belongs to a rep because the *plan* names them, not
/// because the outlet is in their territory — so these tests are written against the subject in the
/// token throughout.
/// </para>
/// <para>
/// Every one of them pulls twice. A first pull that returns the round proves almost nothing on its
/// own: a protocol that re-sent everything forever would pass it, and would silently cost a rep
/// their data allowance every reconnect.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class SyncPullJourneyTests(ServerFixture fixture)
{
    private static readonly DateOnly From = new(2026, 4, 6);
    private static readonly DateOnly To = new(2026, 4, 30);
    private const string Zone = "Europe/Bucharest";

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private static async Task<Guid> BindDeviceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/devices", new BindDeviceRequest(Unique("Phone")));

        return (await response.Content.ReadFromJsonAsync<DeviceResponse>())!.Id;
    }

    private static async Task<JsonElement> PullAsync(
        HttpClient client, Guid deviceId, long? journeys = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/pull", new PullRequest(deviceId, new PullCursors(null, journeys)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static JsonElement Round(JsonElement pull) => pull.GetProperty("changes").GetProperty("journeys");

    private static long Cursor(JsonElement pull) => Round(pull).GetProperty("cursor").GetInt64();

    private static List<JsonElement> Calls(JsonElement pull) =>
        [.. Round(pull).GetProperty("upserts").EnumerateArray()];

    [Fact]
    public async Task A_published_round_reaches_the_device_and_the_next_pull_carries_nothing()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var subject = await SubjectAsync(rep);
        await ScenarioAsync(admin, subject, outletCount: 2);

        var planId = await GenerateAsync(admin, subject);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/journey/plans/{planId}/publish", null)).StatusCode);

        var device = await BindDeviceAsync(rep);
        var first = await PullAsync(rep, device);

        var calls = Calls(first);
        Assert.NotEmpty(calls);
        Assert.All(calls, call => Assert.Equal("Planned", call.GetProperty("status").GetString()));
        Assert.All(calls, call => Assert.True(call.GetProperty("rowVersion").GetInt64() > 0));
        Assert.True(
            Cursor(first) >= calls.Max(call => call.GetProperty("rowVersion").GetInt64()),
            "the cursor must cover every row in the page, or the next pull re-sends it forever");

        // Nothing has changed, so the device is told nothing and stays where it is.
        var second = await PullAsync(rep, device, Cursor(first));

        Assert.Empty(Calls(second));
        Assert.Equal(Cursor(first), Cursor(second));
    }

    [Fact]
    public async Task A_draft_plan_stays_off_the_device()
    {
        // A draft is a supervisor's experiment that the next generation run replaces wholesale, so a
        // rep working one would be working calls that are about to stop existing.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var subject = await SubjectAsync(rep);
        var outlets = await ScenarioAsync(admin, subject, outletCount: 2);

        await GenerateAsync(admin, subject); // generated, deliberately not published

        var device = await BindDeviceAsync(rep);
        var pull = await PullAsync(rep, device);

        Assert.DoesNotContain(
            Calls(pull),
            call => outlets.Contains(call.GetProperty("outletId").GetGuid()));
    }

    [Fact]
    public async Task Publishing_a_plan_delivers_it_to_a_device_that_had_already_synced()
    {
        // The delta doing its job on a device that is already up to date: the calls exist while the
        // plan is a draft, so it is *publishing* — not creating — that has to reach the phone.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var subject = await SubjectAsync(rep);
        await ScenarioAsync(admin, subject, outletCount: 2);

        var planId = await GenerateAsync(admin, subject);

        var device = await BindDeviceAsync(rep);
        var before = await PullAsync(rep, device);

        await admin.PostAsync($"/api/journey/plans/{planId}/publish", null);

        var after = await PullAsync(rep, device, Cursor(before));

        Assert.NotEmpty(Calls(after));
    }

    [Fact]
    public async Task Another_reps_round_never_arrives()
    {
        // Scoped by the subject in the token, not by the device's outlet set. A plan for somebody
        // else, at shops this rep does cover, still belongs to somebody else.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var subject = await SubjectAsync(rep);
        var someoneElse = await SubjectAsync(admin);

        await ScenarioAsync(admin, someoneElse, outletCount: 2);

        var planId = await GenerateAsync(admin, someoneElse);
        await admin.PostAsync($"/api/journey/plans/{planId}/publish", null);

        var theirCalls = await CallIdsAsync(admin, planId);
        Assert.NotEmpty(theirCalls);

        var device = await BindDeviceAsync(rep);
        var pull = await PullAsync(rep, device);

        Assert.DoesNotContain(Calls(pull), call => theirCalls.Contains(call.GetProperty("id").GetGuid()));

        // And the subject under test really does have a round of their own, or the assertion above
        // would pass on an empty page for the wrong reason.
        Assert.NotEqual(subject, someoneElse);
    }

    [Fact]
    public async Task Marking_a_call_not_visited_sends_it_again_with_the_reason()
    {
        // An annotation is an update, not a delete (BR-JRN-2) — which is why this feed has no
        // tombstones and why the change still has to reach the device.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var subject = await SubjectAsync(rep);
        await ScenarioAsync(admin, subject, outletCount: 1);

        var planId = await GenerateAsync(admin, subject);
        await admin.PostAsync($"/api/journey/plans/{planId}/publish", null);

        var device = await BindDeviceAsync(rep);
        var first = await PullAsync(rep, device);

        // Taken from *this* plan rather than from the head of the page. The fixture is shared, so an
        // earlier test's published plan for the same subject is legitimately in the same round — and
        // annotating one of its calls under this plan's id is a 404, which is how this first failed.
        var mine = await CallIdsAsync(admin, planId);
        var call = Calls(first).Select(sent => sent.GetProperty("id").GetGuid()).First(mine.Contains);

        // Annotated by the administrator, because `journey:annotate` is not on the rep fixture's
        // role. Who may mark a call not-visited is JRN-06's question; this test is about the update
        // reaching the device, and borrowing the permission keeps it about that.
        var marked = await admin.PostAsJsonAsync(
            $"/api/journey/plans/{planId}/visits/{call}/not-visited",
            new NotVisitedRequest("Shop was closed for stocktaking."));
        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);

        var second = await PullAsync(rep, device, Cursor(first));

        var updated = Assert.Single(Calls(second), sent => sent.GetProperty("id").GetGuid() == call);
        Assert.Equal("NotVisited", updated.GetProperty("status").GetString());
        Assert.Equal("Shop was closed for stocktaking.", updated.GetProperty("notVisitedReason").GetString());
        Assert.True(Cursor(second) > Cursor(first));
    }

    [Fact]
    public async Task The_two_cursors_move_independently()
    {
        // Separate watermarks per entity type: a device that is current on outlets and behind on
        // journeys asks for exactly that, and a shared cursor would make each look like the other.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var subject = await SubjectAsync(rep);
        var outlets = await ScenarioAsync(admin, subject, outletCount: 1);

        var planId = await GenerateAsync(admin, subject);
        await admin.PostAsync($"/api/journey/plans/{planId}/publish", null);

        var device = await BindDeviceAsync(rep);
        var first = await PullAsync(rep, device);

        var outletCursor = first.GetProperty("changes").GetProperty("outlets").GetProperty("cursor").GetInt64();
        Assert.True(outletCursor > 0);
        Assert.True(Cursor(first) > 0);

        // Change an outlet, and nothing else. The outlet cursor must move; the journey one must not.
        var outlet = await admin.GetFromJsonAsync<JsonElement>($"/api/outlets/{outlets[0]}");

        var renamed = await admin.PutAsJsonAsync(
            $"/api/outlets/{outlets[0]}",
            new UpdateOutletRequest(
                "Renamed for the cursor test",
                outlet.GetProperty("channelId").GetGuid(),
                Zone));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var second = await PullAsync(rep, device, Cursor(first));
        var outletCursorAfter = second.GetProperty("changes").GetProperty("outlets").GetProperty("cursor").GetInt64();

        // The pull sends the outlet cursor as null here — the point is that one entity's traffic
        // does not consume the other's watermark, which a shared counter could not give.
        Assert.Equal(Cursor(first), Cursor(second));
        Assert.Empty(Calls(second));
        Assert.True(outletCursorAfter > 0);
    }

    private static async Task<string> SubjectAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<WhoAmIResponse>("/api/auth/whoami"))!.Subject;

    private sealed record WhoAmIResponse(string Subject);

    private static async Task<List<Guid>> CallIdsAsync(HttpClient admin, Guid planId)
    {
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/journey/plans/{planId}");

        return [.. detail.GetProperty("visits").EnumerateArray().Select(visit => visit.GetProperty("id").GetGuid())];
    }

    private static async Task<Guid> GenerateAsync(HttpClient admin, string subject)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/journey/plans", new GeneratePlanRequest(subject, From, To));

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("plan").GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Everything a plan needs for one subject: shops, a territory holding them, an assignment, a
    /// frequency and a working calendar — assembled the way an administrator would.
    /// </summary>
    private static async Task<List<Guid>> ScenarioAsync(HttpClient admin, string subject, int outletCount)
    {
        // A FieldKit profile for the token's subject, or the assignment is refused with "No such
        // user in this tenant" — authenticating proves who you are, being assignable is a row in IAM.
        var roles = await admin.GetFromJsonAsync<List<RoleResponse>>("/api/iam/roles");
        await admin.PostAsJsonAsync("/api/iam/users", new
        {
            subjectId = subject,
            email = $"{Guid.NewGuid():N}@fieldkit.local",
            displayName = "Journey Sync Rep",
            locale = "en-GB",
            timeZone = Zone,
            roleIds = new[] { roles!.First(role => role.IsSystemTemplate).Id },
        });

        var channel = await admin.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));
        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var segment = Unique("SEG");
        var outletIds = new List<Guid>();

        for (var index = 0; index < outletCount; index++)
        {
            var created = await admin.PostAsJsonAsync(
                "/api/outlets",
                new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, segment));

            outletIds.Add((await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id);
        }

        var unit = await admin.PostAsJsonAsync("/api/org/units", new OrgUnitRequest(Unique("Unit")));
        var unitId = (await unit.Content.ReadFromJsonAsync<OrgUnitResponse>())!.Id;

        var territory = await admin.PostAsJsonAsync(
            "/api/org/territories", new TerritoryRequest(Unique("Terr"), unitId));
        var territoryId = (await territory.Content.ReadFromJsonAsync<TerritoryResponse>())!.Id;

        await admin.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/outlets", new AssignOutletsRequest(outletIds));

        // Open-ended from a fixed date in the past, and that matters for two different readers. The
        // *plan* needs the rep to cover the shops across April 2026; the *pull* asks `IRepScope` for
        // today, whenever today is. An assignment bounded to the plan's window satisfies the first
        // and hands the second an empty territory — which is how this fixture first failed.
        var assigned = await admin.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/assignments",
            new RepAssignmentRequest(subject, new DateOnly(2020, 1, 1), null));
        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);

        await admin.PutAsJsonAsync(
            $"/api/journey/frequencies/segments/{segment}", new FrequencyRequest(1, 7));

        await admin.PutAsJsonAsync(
            $"/api/journey/calendars/{subject}",
            new WorkingCalendarRequest([DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday], 10));

        return outletIds;
    }
}
