using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Audit;
using FieldKit.Modules.Audit.Contracts;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Journey.Contracts;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Sync;
using FieldKit.Modules.Visit;

namespace FieldKit.Server.Tests;

/// <summary>
/// An audit drained from a device (<c>OFF-04</c>, <c>AUD-06</c>, <c>BR-AUD-8</c>) — W10 slice 6.
/// </summary>
/// <remarks>
/// <para>
/// The slice where W10's first five meet: the weight set from slice 1, the aggregate from slice 3,
/// the scorer from slice 4, and the push path here. What is asserted is the round trip a rep's phone
/// actually makes — check in, work the shelf, drain both mutations — and the properties that only
/// appear once an audit travels beside a visit.
/// </para>
/// <para>
/// <b>The visit and the audit are separate mutations, and the test that matters says why.</b> An
/// audit refused for naming a weight version this server has never published must not strand the
/// visit it belonged to; <c>/sync/push</c> answers per mutation precisely so it cannot.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class SyncPushAuditTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    private static readonly Coordinates Shop = new(44.4638, 26.0946);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private HttpClient Rep() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    private static async Task<Guid> BindDeviceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/devices", new BindDeviceRequest(Unique("Phone")));

        return (await response.Content.ReadFromJsonAsync<DeviceResponse>())!.Id;
    }

    private static async Task<PushResponse> PushAsync(
        HttpClient client, Guid deviceId, params PushedMutation[] mutations)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/push", new PushRequest(deviceId, mutations));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<PushResponse>())!;
    }

    private static async Task<Guid> OutletAsync(HttpClient client)
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var created = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, Location: Shop));

        return (await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    /// <summary>Publishes a 50/30/20 weighting and returns the version the server assigned.</summary>
    private static async Task<int> WeightingAsync(HttpClient client)
    {
        var drafted = await client.PostAsJsonAsync("/api/config/score-weights", new ScoreWeightSetRequest([
            new ScoreWeightRequest(ScorePillar.Availability, 50m),
            new ScoreWeightRequest(ScorePillar.ShareOfShelf, 30m),
            new ScoreWeightRequest(ScorePillar.PriceCompliance, 20m),
        ]));

        var version = (await drafted.Content.ReadFromJsonAsync<ScoreWeightSetResponse>())!.Version;

        await client.PostAsync($"/api/config/score-weights/{version}/publish", null);

        return version;
    }

    /// <summary>Checks in over HTTP — the visit an audit will hang off.</summary>
    private static async Task<Guid> VisitAsync(HttpClient client, Guid outletId)
    {
        var response = await client.PostAsJsonAsync(
            "/api/visits/check-in", new CheckInRequest(outletId, Shop.Latitude, Shop.Longitude));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit.Id;
    }

    private static CapturedAudit Captured(Guid visitId, int weighting, Guid? auditId = null)
    {
        var product = Guid.CreateVersion7();

        return new CapturedAudit(
            auditId ?? Guid.CreateVersion7(),
            visitId,
            new DateTimeOffset(2026, 4, 6, 9, 30, 0, TimeSpan.Zero),
            weighting,
            CategoryFacings: 40,
            Availability: [new CapturedAvailability(product, AvailabilityStatus.Present)],
            Facings: [new CapturedFacings(product, 10)],
            Prices: [new CapturedPrice(product, 999, 999, "RON")]);
    }

    private static PushedMutation Mutation(CapturedAudit audit, Guid? mutationId = null) =>
        new(mutationId ?? Guid.CreateVersion7(), nameof(CapturedAudit), Audit: audit);

    [Fact]
    public async Task An_audit_drained_from_a_device_is_stored_and_scored()
    {
        /*
         * The whole slice in one test. Availability 100 (weight 50), share of shelf 25 (weight 30),
         * price compliance 100 (weight 20) → (100 × 50 + 25 × 30 + 100 × 20) ÷ 100 = 77.5.
         */
        using var rep = Rep();

        var device = await BindDeviceAsync(rep);
        var outletId = await OutletAsync(rep);
        var weighting = await WeightingAsync(rep);
        var visitId = await VisitAsync(rep, outletId);

        var response = await PushAsync(rep, device, Mutation(Captured(visitId, weighting)));

        Assert.Equal("accepted", Assert.Single(response.Results).Status);

        var stored = await rep.GetFromJsonAsync<AuditResponse>($"/api/visits/{visitId}/audit");

        Assert.Equal(weighting, stored!.WeightSetVersion);
        Assert.Equal(77.5m, stored.Score);

        // The outlet came from the visit, not the payload — `CapturedAudit` has no field for it.
        Assert.Equal(outletId, stored.OutletId);
    }

    [Fact]
    public async Task A_refused_audit_does_not_take_the_visit_with_it()
    {
        /*
         * The property the per-mutation result exists for, and the reason W10 slice 0 decided an
         * audit is its own mutation.
         *
         * A rep's phone drains a visit and an audit together. The audit names a weight version this
         * server has never published — a device bug, or a set that was still a draft when it synced.
         * Refusing the audit must not refuse the visit: the rep was in the shop, and that is a fact
         * regardless of what the phone thought the weights were.
         */
        using var rep = Rep();

        var device = await BindDeviceAsync(rep);
        var outletId = await OutletAsync(rep);
        var visitId = await VisitAsync(rep, outletId);

        var results = await PushAsync(
            rep,
            device,
            Mutation(Captured(visitId, weighting: 99_999)),
            new PushedMutation(Guid.CreateVersion7(), "NotAKindThisServerCarries"));

        Assert.Equal(2, results.Results.Count);
        Assert.All(results.Results, result => Assert.Equal("rejected", result.Status));

        // Refused by name, so a device can tell "your weight set is wrong" from "retry later".
        Assert.Equal("audit.ingest.weightSetUnknown", results.Results[0].Reason);

        // …and the visit it belonged to is untouched and still open.
        var visit = await rep.GetFromJsonAsync<VisitDetailResponse>($"/api/visits/{visitId}");
        Assert.Equal("InProgress", visit!.Visit.Status);
    }

    [Fact]
    public async Task A_batch_carrying_a_visit_and_its_audit_applies_each_through_its_own_module()
    {
        // What a phone actually drains at the end of a call. Two mutations, two modules, one push —
        // and Sync holds no opinion about either.
        using var rep = Rep();

        var device = await BindDeviceAsync(rep);
        var outletId = await OutletAsync(rep);
        var weighting = await WeightingAsync(rep);
        var visitId = await VisitAsync(rep, outletId);

        var results = await PushAsync(
            rep,
            device,
            Mutation(Captured(visitId, weighting)),
            new PushedMutation(
                Guid.CreateVersion7(),
                nameof(NotVisitedCall),
                NotVisited: new NotVisitedCall(
                    Guid.CreateVersion7(), "Shop was shut.")));

        Assert.Equal("accepted", results.Results[0].Status);

        // The Journey arm refused its own — a call id nobody planned — and that is the point: each
        // arm answers for itself.
        Assert.Equal("rejected", results.Results[1].Status);
        Assert.Equal("journey.visit.unknown", results.Results[1].Reason);
    }

    [Fact]
    public async Task Replaying_the_push_answers_from_the_ledger_rather_than_storing_twice()
    {
        // Exactly-once effect over at-least-once delivery. The ledger answers the second attempt, so
        // the audit's own replay window is never even reached — both guards exist because the two
        // commits are separate, and this is the outer one.
        using var rep = Rep();

        var device = await BindDeviceAsync(rep);
        var outletId = await OutletAsync(rep);
        var weighting = await WeightingAsync(rep);
        var visitId = await VisitAsync(rep, outletId);

        var mutationId = Guid.CreateVersion7();
        var audit = Captured(visitId, weighting);

        var first = await PushAsync(rep, device, Mutation(audit, mutationId));
        var again = await PushAsync(rep, device, Mutation(audit, mutationId));

        Assert.Equal("accepted", Assert.Single(first.Results).Status);
        Assert.Equal("accepted", Assert.Single(again.Results).Status);

        var audits = await rep.GetFromJsonAsync<List<AuditResponse>>($"/api/outlets/{outletId}/audits");

        Assert.Single(audits!, candidate => candidate.VisitId == visitId);
    }

    [Fact]
    public async Task A_mutation_typed_as_an_audit_with_no_audit_is_refused_by_name()
    {
        // The payload-slot guard, which the wire vectors pin and this asserts over HTTP. A device
        // that sent the wrong slot must hear about it rather than have the mutation silently dropped
        // — a dropped mutation is one the device retries forever.
        using var rep = Rep();

        var device = await BindDeviceAsync(rep);

        var results = await PushAsync(
            rep, device, new PushedMutation(Guid.CreateVersion7(), nameof(CapturedAudit)));

        Assert.Equal("rejected", Assert.Single(results.Results).Status);
        Assert.Equal("sync.push.payloadMissing", results.Results[0].Reason);
    }
}
