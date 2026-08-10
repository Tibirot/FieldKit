using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Sync;
using FieldKit.Modules.Visit;
using FieldKit.Modules.Visit.Contracts;

namespace FieldKit.Server.Tests;

/// <summary>
/// The drain: <c>/sync/push</c> and the idempotency ledger (<c>OFF-04</c>, sync engine §4).
/// </summary>
/// <remarks>
/// <para>
/// The property under test is not "a visit can be pushed" — it is that pushing the same batch twice
/// changes the world once. A device that loses its connection after the server committed but before
/// the response arrived has no way to tell success from failure, so it retries; the protocol is only
/// safe if that retry is free.
/// </para>
/// <para>
/// So every test here that asserts an outcome asserts it <b>twice</b>, against the same mutation id.
/// A ledger that recorded nothing would still pass the first half of each of them.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class SyncPushTests(ServerFixture fixture)
{
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private static readonly DateTimeOffset Yesterday = new(2026, 3, 17, 9, 0, 0, TimeSpan.Zero);

    private static async Task<Guid> BindDeviceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/devices", new BindDeviceRequest(Unique("Phone")));

        return (await response.Content.ReadFromJsonAsync<DeviceResponse>())!.Id;
    }

    /// <summary>A visit as a device would have captured it: complete, past, and already decided.</summary>
    private static CapturedVisit Captured(
        Guid outletId,
        string outcome = "Productive",
        string? reason = null,
        Guid? visitId = null) => new(
            visitId ?? Guid.CreateVersion7(),
            outletId,
            PlannedVisitId: null,
            CheckedInAtUtc: Yesterday,
            CheckInLatitude: 44.43,
            CheckInLongitude: 26.10,
            CheckInDistanceMetres: 12.5,
            WasInsideGeofence: true,
            OverrideReason: null,
            Steps:
            [
                new CapturedStep(
                    Guid.CreateVersion7(), 1, "Note", true, "Talk to the owner", "Reordered",
                    Yesterday.AddMinutes(10)),
            ],
            Outcome: outcome,
            OutcomeReason: reason,
            CheckedOutAtUtc: Yesterday.AddMinutes(25),
            CheckOutLatitude: 44.43,
            CheckOutLongitude: 26.10);

    private static async Task<PushResponse> PushAsync(
        HttpClient client, Guid deviceId, params PushedMutation[] mutations)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/push", new PushRequest(deviceId, mutations));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PushResponse>())!;
    }

    private static PushedMutation Mutation(CapturedVisit visit, Guid? mutationId = null) =>
        new(mutationId ?? Guid.CreateVersion7(), nameof(CapturedVisit), visit);

    [Fact]
    public async Task Pushing_needs_a_token()
    {
        var response = await fixture.Client.PostAsJsonAsync(
            "/api/sync/push", new PushRequest(Guid.CreateVersion7(), []));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_device_cannot_push()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var response = await rep.PostAsJsonAsync(
            "/api/sync/push", new PushRequest(Guid.CreateVersion7(), []));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("sync.push.deviceUnknown", Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task Another_reps_device_cannot_push_work_as_them()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var someoneElses = await BindDeviceAsync(admin);

        var response = await rep.PostAsJsonAsync(
            "/api/sync/push", new PushRequest(someoneElses, []));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_captured_visit_becomes_a_stored_one_with_the_devices_own_times()
    {
        // The slice. Everything else here is about what happens the *second* time.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outletId = await OutletAsync(admin);
        var device = await BindDeviceAsync(rep);
        var visit = Captured(outletId);

        var push = await PushAsync(rep, device, Mutation(visit));

        Assert.Equal("accepted", Assert.Single(push.Results).Status);

        var stored = await admin.GetFromJsonAsync<VisitDetailResponse>($"/api/visits/{visit.VisitId}");

        Assert.NotNull(stored);
        Assert.Equal("CheckedOut", stored.Visit.Status);
        Assert.Equal("Productive", stored.Visit.Outcome);
        Assert.Equal(outletId, stored.Visit.OutletId);

        // The record is of yesterday, not of reconnection. A server clock anywhere in the ingest
        // path would show up here and nowhere else.
        Assert.Equal(Yesterday, stored.Visit.CheckedInAtUtc);
        Assert.Equal(Yesterday.AddMinutes(25), stored.Visit.CheckedOutAtUtc);

        // The device's geofence verdict, stored as fact rather than re-judged against today's radius.
        Assert.True(stored.Visit.WasInsideGeofence);
        Assert.Equal(12.5, stored.Visit.CheckInDistanceMetres);

        var step = Assert.Single(stored.Steps);
        Assert.Equal("Completed", step.Status);
        Assert.Equal("Reordered", step.Notes);
        Assert.Equal(Yesterday.AddMinutes(10), step.CompletedAtUtc);
        Assert.Empty(stored.OpenMandatorySteps);
    }

    [Fact]
    public async Task Replaying_the_same_mutation_id_changes_nothing_and_answers_the_same()
    {
        // The retry the protocol is built around: the device never learned the first push landed.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outletId = await OutletAsync(admin);
        var device = await BindDeviceAsync(rep);
        var mutation = Mutation(Captured(outletId));

        var first = await PushAsync(rep, device, mutation);
        var second = await PushAsync(rep, device, mutation);

        Assert.Equal("accepted", Assert.Single(first.Results).Status);
        Assert.Equal("accepted", Assert.Single(second.Results).Status);

        // One visit, not two. Without the ledger the second push would refuse as `alreadyExists`
        // *or*, with a server-minted id, quietly store the same afternoon's work twice.
        var visits = await admin.GetFromJsonAsync<List<VisitResponse>>($"/api/visits?outletId={outletId}");
        Assert.Single(visits!);
    }

    [Fact]
    public async Task A_replayed_rejection_is_the_same_rejection_not_a_second_attempt()
    {
        // The half that is easy to get wrong: recording only successes turns a refusal into an
        // infinite retry, because the device is told something different every time it asks.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var device = await BindDeviceAsync(rep);
        var mutation = Mutation(Captured(Guid.CreateVersion7())); // an outlet that does not exist

        var first = await PushAsync(rep, device, mutation);
        var second = await PushAsync(rep, device, mutation);

        var rejected = Assert.Single(first.Results);
        Assert.Equal("rejected", rejected.Status);
        Assert.Equal("visit.ingest.outletUnknown", rejected.Reason);

        var replayed = Assert.Single(second.Results);
        Assert.Equal("rejected", replayed.Status);
        Assert.Equal("visit.ingest.outletUnknown", replayed.Reason);
        Assert.Equal(rejected.Detail, replayed.Detail);
    }

    [Fact]
    public async Task One_bad_mutation_does_not_cost_the_batch()
    {
        // Partial success is the normal case for a device that has been offline for a day: the
        // nineteen good visits must land whatever the twentieth says.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outletId = await OutletAsync(admin);
        var device = await BindDeviceAsync(rep);

        var good = Captured(outletId);
        var bad = Captured(outletId, outcome: "NonProductive"); // BR-VIS-3: no reason given

        var push = await PushAsync(rep, device, Mutation(good), Mutation(bad));

        Assert.Equal(2, push.Results.Count);
        Assert.Equal("accepted", push.Results[0].Status);
        Assert.Equal("rejected", push.Results[1].Status);
        Assert.Equal("visit.ingest.outcomeReasonRequired", push.Results[1].Reason);

        var stored = await admin.GetAsync($"/api/visits/{good.VisitId}");
        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);

        var refused = await admin.GetAsync($"/api/visits/{bad.VisitId}");
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }

    [Fact]
    public async Task The_same_visit_under_a_new_mutation_id_is_accepted_without_storing_it_twice()
    {
        // The crash window between the two commits: the visit landed, the ledger entry did not, and
        // the device's retry arrives looking new. The device-minted visit id is what closes it.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outletId = await OutletAsync(admin);
        var device = await BindDeviceAsync(rep);
        var visit = Captured(outletId);

        await PushAsync(rep, device, Mutation(visit));
        var again = await PushAsync(rep, device, Mutation(visit)); // same visit, fresh mutation id

        Assert.Equal("accepted", Assert.Single(again.Results).Status);

        var visits = await admin.GetFromJsonAsync<List<VisitResponse>>($"/api/visits?outletId={outletId}");
        Assert.Single(visits!);
    }

    [Fact]
    public async Task A_mutation_type_this_server_does_not_speak_is_rejected_rather_than_dropped()
    {
        // Silently ignoring it would leave the device retrying forever with nothing to act on.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var device = await BindDeviceAsync(rep);

        var push = await PushAsync(
            rep, device, new PushedMutation(Guid.CreateVersion7(), "CapturedOrder", null));

        var result = Assert.Single(push.Results);
        Assert.Equal("rejected", result.Status);
        Assert.Equal("sync.push.typeUnsupported", result.Reason);
    }

    [Fact]
    public async Task A_replaced_device_may_still_drain_the_work_it_captured()
    {
        // Unlike pull, which refuses an inactive device. Refusing here would lose a day of work the
        // rep did on the phone they have since swapped — and a drain creates no competing writer,
        // because every record it carries is device-owned and already complete (A8).
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outletId = await OutletAsync(admin);
        var old = await BindDeviceAsync(rep);
        await BindDeviceAsync(rep); // the swap deactivates `old`

        var push = await PushAsync(rep, old, Mutation(Captured(outletId)));

        Assert.Equal("accepted", Assert.Single(push.Results).Status);
    }

    [Fact]
    public async Task A_device_reported_compromised_cannot_push_at_all()
    {
        // The one revocation that does close the drain. A phone in someone else's hands pushing
        // "visits" is fabricated evidence of work, and losing whatever it held is the cheaper loss.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outletId = await OutletAsync(admin);
        var device = await BindDeviceAsync(rep);

        var revoked = await admin.PostAsJsonAsync(
            $"/api/sync/devices/{device}/revoke", new RevokeDeviceRequest(DeactivationReason.Compromised));
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        var response = await rep.PostAsJsonAsync(
            "/api/sync/push", new PushRequest(device, [Mutation(Captured(outletId))]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("sync.push.deviceCompromised", Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task A_batch_beyond_the_limit_is_refused_before_anything_is_applied()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var device = await BindDeviceAsync(rep);
        var mutations = Enumerable.Range(0, 201)
            .Select(_ => Mutation(Captured(Guid.CreateVersion7())))
            .ToList();

        var response = await rep.PostAsJsonAsync("/api/sync/push", new PushRequest(device, mutations));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("sync.push.batchTooLarge", Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

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
