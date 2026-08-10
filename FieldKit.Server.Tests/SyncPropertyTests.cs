using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Org;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Sync;
using FieldKit.Modules.Visit;
using FieldKit.Modules.Visit.Contracts;

namespace FieldKit.Server.Tests;

/// <summary>
/// What the protocol must do for <i>every</i> input, not just the ones anyone wrote down
/// (<c>OFF-04</c>, W8 slice 9).
/// </summary>
/// <remarks>
/// <para>
/// The worked examples in <c>SyncPushTests</c> and the <c>SyncPull*Tests</c> each pin one scenario
/// somebody thought of. These pin the two statements the whole engine rests on, over generated
/// input: <b>replaying a batch changes nothing</b>, and <b>a pull interrupted anywhere resumes
/// without loss or duplication</b>.
/// </para>
/// <para>
/// <b>Deterministic, seeded input rather than a randomised run per build</b> — the position
/// <c>VectorPropertyTests</c> took in W6 and the reason still holds: a property suite that fails once
/// a fortnight on a seed nobody can reproduce teaches people to re-run CI, which is worse than not
/// having it. The generator below is a fixed sweep; changing it is a diff somebody reviews.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class SyncPropertyTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    private static readonly DateTimeOffset Captured = new(2026, 3, 17, 9, 0, 0, TimeSpan.Zero);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    /// <summary>
    /// The batch shapes the replay property runs over.
    /// </summary>
    /// <remarks>
    /// Each string is one batch, read left to right: <c>o</c> is a visit at a real outlet (accepted),
    /// <c>x</c> is one at an outlet that does not exist (rejected), <c>n</c> is a non-productive
    /// visit with no reason (rejected for a different rule). The mix matters because the ledger has
    /// to record *both* answers — a version that stored only successes passes every all-<c>o</c> case
    /// and turns every rejection into an infinite retry.
    /// </remarks>
    public static TheoryData<string> Batches() =>
    [
        "o",
        "x",
        "n",
        "oo",
        "ox",
        "xo",
        "on",
        "oxn",
        "xxx",
        "ooo",
        "oxoxo",
        "nnoo",
        "oooooooo",
    ];

    [Theory]
    [MemberData(nameof(Batches))]
    public async Task Replaying_a_batch_changes_nothing(string shape)
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outletId = await OutletAsync(admin);
        var device = await BindDeviceAsync(rep);
        var mutations = Build(shape, outletId);

        var first = await PushAsync(rep, device, mutations);
        var second = await PushAsync(rep, device, mutations);

        // Identical answers, mutation by mutation. Not just "both succeeded" — a replay that
        // re-evaluated the rules would agree on this batch and disagree the day one of its inputs
        // changed underneath it, which is the case the ledger exists for.
        Assert.Equal(
            first.Select(result => (result.MutationId, result.Status, result.Reason)),
            second.Select(result => (result.MutationId, result.Status, result.Reason)));

        // And the world changed once. Every accepted visit is stored exactly once; every rejected
        // one is stored not at all.
        var stored = await admin.GetFromJsonAsync<List<VisitResponse>>($"/api/visits?outletId={outletId}");
        var accepted = first.Count(result => result.Status == "accepted");

        Assert.Equal(accepted, stored!.Count);
    }

    [Theory]
    [MemberData(nameof(Batches))]
    public async Task Splitting_a_batch_anywhere_gives_the_same_answers(string shape)
    {
        // The other half of "a retry is free": a device that lost its connection mid-batch re-sends
        // what it could not confirm, which is not the same batch. Every split must be equivalent to
        // the whole — otherwise the protocol is only idempotent for devices that fail tidily.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outletId = await OutletAsync(admin);
        var device = await BindDeviceAsync(rep);
        var mutations = Build(shape, outletId);

        for (var split = 1; split < mutations.Count; split++)
        {
            var head = mutations.Take(split).ToList();
            var tail = mutations.Skip(split).ToList();

            var whole = await PushAsync(rep, device, mutations);
            var piecewise = (await PushAsync(rep, device, head))
                .Concat(await PushAsync(rep, device, tail))
                .ToList();

            Assert.Equal(
                whole.Select(result => (result.MutationId, result.Status)),
                piecewise.Select(result => (result.MutationId, result.Status)));
        }
    }

    /// <summary>
    /// A pull interrupted at any point resumes without loss or duplication.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Interruption is modelled the way the protocol actually experiences it: the device banks the
    /// cursor it committed and asks again from there. A response that never arrived is a pull that
    /// never happened, so the next request repeats the previous cursor — which is the case
    /// <c>step == 0</c> covers.
    /// </para>
    /// <para>
    /// The two invariants are the ones the whole delta rests on. <b>No loss:</b> every outlet in the
    /// rep's territory is eventually delivered. <b>No duplication:</b> nothing is delivered twice
    /// unless its row version moved, which nothing here makes happen.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(5, 2)]
    [InlineData(5, 4)]
    public async Task A_pull_interrupted_anywhere_resumes_without_loss_or_duplication(
        int outletCount, int interruptAfter)
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var (_, outlets) = await TerritoryAsync(admin, rep, outletCount);
        var device = await BindDeviceAsync(rep);

        // The device is up to date first, so what follows exercises the *delta*. The baseline half
        // has a known asymmetry of its own — see the test below, which names it rather than hiding
        // it inside this loop.
        long cursor = Cursor(await PullAsync(rep, device, 0));

        // Now change every one of them, so there is a delta to interrupt.
        foreach (var outletId in outlets) await RenameAsync(admin, outletId);

        var delivered = new List<Guid>();

        for (var step = 0; step < outletCount + 3; step++)
        {
            var page = await PullAsync(rep, device, cursor);
            var ids = Outlets(page).Select(outlet => outlet.GetProperty("id").GetGuid()).ToList();

            /*
             * The interruption: the response never reached the device, so it neither stores the rows
             * nor advances its cursor. Everything the server just sent must arrive again on the next
             * request — and, once it has, exactly once, which is what the duplicate check below is
             * really asserting.
             */
            if (step == interruptAfter) continue;

            delivered.AddRange(ids.Where(outlets.Contains));
            cursor = Cursor(page);

            if (ids.Count == 0) break;
        }

        Assert.Equal(outlets.OrderBy(id => id), delivered.OrderBy(id => id));
        Assert.Equal(delivered.Count, delivered.Distinct().Count());
    }

    [Fact]
    public async Task A_discarded_first_pull_is_recovered_by_the_next_one()
    {
        /*
         * The case `RecordScopeAsync` warns about, and it turns out to be narrower than the warning.
         *
         * The server records the device's scope set *before* the response is delivered, so a first
         * pull whose response is lost leaves the server believing the device holds outlets it never
         * received. The remark on that method calls this "the one place this protocol is not
         * self-healing" and points at slice 9 — this is slice 9 looking.
         *
         * It heals. A device that lost the response also lost the cursor, so it asks again from
         * zero; the outlets are no longer *entering*, but they are `retained`, and the delta over a
         * retained set with cursor 0 is every row it holds. The baseline and the delta cover each
         * other.
         *
         * What is genuinely unrecoverable is a device that advances its cursor without storing the
         * rows — and that is exactly what `applyOutletChanges` makes impossible, by writing both in
         * one IndexedDB transaction. The gap needs *both* halves to fail, and the client is built so
         * they cannot fail separately.
         */
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var (_, outlets) = await TerritoryAsync(admin, rep, 2);
        var device = await BindDeviceAsync(rep);

        // The first pull happens on the server; its response never arrives, so the device keeps
        // cursor 0 and stores nothing.
        var lost = await PullAsync(rep, device, 0);
        Assert.Equal(
            outlets.Count,
            Outlets(lost).Count(outlet => outlets.Contains(outlet.GetProperty("id").GetGuid())));

        var recovered = await PullAsync(rep, device, 0);

        Assert.Equal(
            outlets.OrderBy(id => id),
            Outlets(recovered)
                .Select(outlet => outlet.GetProperty("id").GetGuid())
                .Where(outlets.Contains)
                .OrderBy(id => id));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task A_device_that_keeps_pulling_stops_being_told_anything(int outletCount)
    {
        // Convergence. A protocol that re-sent the world every time would satisfy "no loss" forever
        // and be useless — this is the assertion that the delta actually engages.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        await TerritoryAsync(admin, rep, outletCount);
        var device = await BindDeviceAsync(rep);

        long cursor = 0;
        var pulls = 0;

        while (pulls++ < 10)
        {
            var page = await PullAsync(rep, device, cursor);
            cursor = Cursor(page);

            if (Outlets(page).Count == 0) break;
        }

        Assert.True(pulls <= 10, "the device never stopped being told about changes");

        // And once quiet, it stays quiet: the cursor does not move on an unchanged tenant.
        var settled = await PullAsync(rep, device, cursor);

        Assert.Empty(Outlets(settled));
        Assert.Equal(cursor, Cursor(settled));
    }

    private static List<PushedMutation> Build(string shape, Guid outletId) =>
    [
        .. shape.Select(kind => new PushedMutation(
            Guid.CreateVersion7(),
            nameof(CapturedVisit),
            kind switch
            {
                'o' => Captured_(outletId),
                'x' => Captured_(Guid.CreateVersion7()),
                'n' => Captured_(outletId, "NonProductive"),
                _ => throw new ArgumentOutOfRangeException(nameof(shape), $"Unknown kind '{kind}'."),
            })),
    ];

    private static CapturedVisit Captured_(Guid outletId, string outcome = "Productive") => new(
        Guid.CreateVersion7(),
        outletId,
        PlannedVisitId: null,
        CheckedInAtUtc: Captured,
        CheckInLatitude: null,
        CheckInLongitude: null,
        CheckInDistanceMetres: null,
        WasInsideGeofence: true,
        OverrideReason: null,
        Steps: [],
        Outcome: outcome,
        OutcomeReason: null,
        CheckedOutAtUtc: Captured.AddMinutes(20),
        CheckOutLatitude: null,
        CheckOutLongitude: null);

    private static async Task<List<MutationResult>> PushAsync(
        HttpClient client, Guid deviceId, IReadOnlyList<PushedMutation> mutations)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/push", new PushRequest(deviceId, mutations));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return [.. (await response.Content.ReadFromJsonAsync<PushResponse>())!.Results];
    }

    private static async Task<JsonElement> PullAsync(HttpClient client, Guid deviceId, long cursor)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/pull", new PullRequest(deviceId, new PullCursors(cursor)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static JsonElement Page(JsonElement pull) =>
        pull.GetProperty("changes").GetProperty("outlets");

    private static long Cursor(JsonElement pull) => Page(pull).GetProperty("cursor").GetInt64();

    private static List<JsonElement> Outlets(JsonElement pull) =>
        [.. Page(pull).GetProperty("upserts").EnumerateArray()];

    private static async Task<Guid> BindDeviceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/devices", new BindDeviceRequest(Unique("Phone")));

        return (await response.Content.ReadFromJsonAsync<DeviceResponse>())!.Id;
    }

    /// <summary>Edits one outlet, so it has a new row version and a delta has something to carry.</summary>
    private static async Task RenameAsync(HttpClient admin, Guid outletId)
    {
        var outlet = await admin.GetFromJsonAsync<JsonElement>($"/api/outlets/{outletId}");

        var renamed = await admin.PutAsJsonAsync(
            $"/api/outlets/{outletId}",
            new UpdateOutletRequest(
                Unique("Renamed"), outlet.GetProperty("channelId").GetGuid(), Zone));

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
    }

    private static async Task<Guid> OutletAsync(HttpClient admin)
    {
        var channel = await admin.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));
        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var created = await admin.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, null));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    /// <summary>A territory of <paramref name="outletCount"/> shops, covering today, for the token's subject.</summary>
    private static async Task<(string Subject, HashSet<Guid> Outlets)> TerritoryAsync(
        HttpClient admin, HttpClient rep, int outletCount)
    {
        var me = await rep.GetFromJsonAsync<WhoAmIResponse>("/api/auth/whoami");

        var roles = await admin.GetFromJsonAsync<List<RoleResponse>>("/api/iam/roles");
        await admin.PostAsJsonAsync("/api/iam/users", new
        {
            subjectId = me!.Subject,
            email = $"{Guid.NewGuid():N}@fieldkit.local",
            displayName = "Property Rep",
            locale = "en-GB",
            timeZone = Zone,
            roleIds = new[] { roles!.First(role => role.IsSystemTemplate).Id },
        });

        var outlets = new List<Guid>();
        for (var index = 0; index < outletCount; index++) outlets.Add(await OutletAsync(admin));

        var unit = await admin.PostAsJsonAsync("/api/org/units", new OrgUnitRequest(Unique("Unit")));
        var unitId = (await unit.Content.ReadFromJsonAsync<OrgUnitResponse>())!.Id;

        var territory = await admin.PostAsJsonAsync(
            "/api/org/territories", new TerritoryRequest(Unique("Terr"), unitId));
        var territoryId = (await territory.Content.ReadFromJsonAsync<TerritoryResponse>())!.Id;

        await admin.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/outlets", new AssignOutletsRequest(outlets));

        var assigned = await admin.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/assignments",
            new RepAssignmentRequest(me.Subject, new DateOnly(2020, 1, 1), null));

        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);

        return (me.Subject, [.. outlets]);
    }

    private sealed record WhoAmIResponse(string Subject);
}
