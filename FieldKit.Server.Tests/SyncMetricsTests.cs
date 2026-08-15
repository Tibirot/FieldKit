using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using FieldKit.BuildingBlocks;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Sync;
using FieldKit.Modules.Visit.Contracts;

namespace FieldKit.Server.Tests;

/// <summary>
/// What <c>/sync/push</c> reports about itself (<c>observability §2</c>) — W13 slice 1.
/// </summary>
/// <remarks>
/// <para>
/// Driven over HTTP against the real host rather than by calling <see cref="SyncMetrics"/> directly.
/// The interesting claims are all about the <b>endpoint</b>: that a refused push is still measured,
/// that a replayed rejection is not counted twice, that the tags are the ones the cardinality rule
/// admits. A unit test of the recorder would assert none of them and would pass with the endpoint
/// wired to nothing.
/// </para>
/// <para>
/// Listening is a plain <c>MeterListener</c> — the same mechanism an exporter uses — rather than a
/// testing package. It is a dependency this repository does not otherwise need, and the listener is
/// six lines.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class SyncMetricsTests(ServerFixture fixture)
{
    private static readonly DateTimeOffset Yesterday = new(2026, 3, 17, 9, 0, 0, TimeSpan.Zero);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    [Fact]
    public async Task Records_how_much_a_push_carried_and_how_long_it_took()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outletId = await OutletAsync(admin);
        var device = await BindDeviceAsync(rep);

        using var recorded = Recording();

        await rep.PostAsJsonAsync("/api/sync/push", new PushRequest(
            device, [Visit(outletId), Visit(outletId)]));

        Assert.Equal(2, Assert.Single(recorded.Values("fieldkit.sync.push.batch_size")));

        // Greater than zero rather than under a threshold: this asserts that something timed the
        // call, not how fast a CI box happens to be. A latency budget belongs to an alert.
        Assert.True(Assert.Single(recorded.Values("fieldkit.sync.push.latency")) > 0);
    }

    [Fact]
    public async Task Counts_a_refused_mutation_under_the_code_the_device_was_given()
    {
        /*
         * "Sync is failing" and "one outlet was deleted while a rep was offline" are the same line on
         * a success-rate graph and different problems, which is the whole reason the counter carries
         * a reason at all.
         */
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var device = await BindDeviceAsync(rep);

        using var recorded = Recording();

        await rep.PostAsJsonAsync("/api/sync/push", new PushRequest(
            device, [new PushedMutation(Guid.CreateVersion7(), "SomethingThisServerHasNeverHeardOf")]));

        var rejection = Assert.Single(recorded.Measurements("fieldkit.sync.mutations.rejected"));

        Assert.Equal(1, rejection.Value);
        Assert.Equal("sync.push.typeUnsupported", rejection.Tags[Telemetry.Tags.Reason]);
    }

    [Fact]
    public async Task A_retried_rejection_is_not_a_second_rejection()
    {
        /*
         * A device that cannot fix a mutation retries it until something changes. Counting the replay
         * would make the rejection *rate* a measurement of the device's retry policy — it would climb
         * on its own with nothing happening in the field, and it would climb fastest for the rep whose
         * connection is worst.
         *
         * The ledger already answers a replay without re-applying it; this asserts the counter sits on
         * the side of that branch which only a first attempt reaches.
         */
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var device = await BindDeviceAsync(rep);
        var mutation = new PushedMutation(Guid.CreateVersion7(), "SomethingThisServerHasNeverHeardOf");

        using var recorded = Recording();

        await rep.PostAsJsonAsync("/api/sync/push", new PushRequest(device, [mutation]));
        await rep.PostAsJsonAsync("/api/sync/push", new PushRequest(device, [mutation]));

        // Two pushes, so two batches measured — and one rejection between them.
        Assert.Equal(2, recorded.Values("fieldkit.sync.push.batch_size").Count);
        Assert.Single(recorded.Measurements("fieldkit.sync.mutations.rejected"));
    }

    [Fact]
    public async Task A_push_the_server_refuses_whole_is_still_measured()
    {
        /*
         * The batch is over the limit and answers 400. The device still carried that work, and the
         * distribution this histogram describes is "what does a reconnect bring" — so dropping the
         * outliers would leave a graph that looks healthiest exactly when devices are misbehaving.
         */
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var device = await BindDeviceAsync(rep);
        var oversized = Enumerable
            .Range(0, 201)
            .Select(_ => new PushedMutation(Guid.CreateVersion7(), nameof(CapturedVisit)))
            .ToArray();

        using var recorded = Recording();

        var response = await rep.PostAsJsonAsync("/api/sync/push", new PushRequest(device, oversized));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(201, Assert.Single(recorded.Values("fieldkit.sync.push.batch_size")));

        // And nothing inside a batch that never ran was counted as refused.
        Assert.Empty(recorded.Measurements("fieldkit.sync.mutations.rejected"));
    }

    [Fact]
    public async Task Labels_the_tenant_and_nothing_that_could_be_unbounded()
    {
        /*
         * The cardinality rule, as an assertion (`Telemetry`). A device id, a subject or a mutation id
         * on a tag is a new time series per value — the failure mode where the thing meant to warn you
         * is what falls over. They belong on a span instead, where a unique value costs one trace.
         *
         * Written as an exact set rather than "does not contain deviceId": a list of things to avoid
         * only ever catches the ones somebody thought of.
         */
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var device = await BindDeviceAsync(rep);

        using var recorded = Recording();

        await rep.PostAsJsonAsync("/api/sync/push", new PushRequest(
            device, [new PushedMutation(Guid.CreateVersion7(), "SomethingThisServerHasNeverHeardOf")]));

        Assert.Equal(
            [Telemetry.Tags.Tenant],
            Assert.Single(recorded.Measurements("fieldkit.sync.push.batch_size")).Tags.Keys.Order());

        Assert.Equal(
            [Telemetry.Tags.Reason, Telemetry.Tags.Tenant],
            Assert.Single(recorded.Measurements("fieldkit.sync.mutations.rejected")).Tags.Keys.Order());
    }

    [Fact]
    public async Task Every_sync_instrument_hangs_off_the_one_meter()
    {
        /*
         * The meter name is what an exporter subscribes to and what the host passes to `AddMeter`. An
         * instrument created on a second meter is not an error anywhere — it simply never arrives, and
         * the panel that reads it stays empty with no failure to trace.
         */
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var device = await BindDeviceAsync(rep);

        using var recorded = Recording();

        await rep.PostAsJsonAsync("/api/sync/push", new PushRequest(
            device, [new PushedMutation(Guid.CreateVersion7(), "SomethingThisServerHasNeverHeardOf")]));

        Assert.Equal(
            ["fieldkit.sync.mutations.rejected", "fieldkit.sync.push.batch_size", "fieldkit.sync.push.latency"],
            recorded.Names.Order());
    }

    /// <summary>Everything the FieldKit meter published while this is alive.</summary>
    private static Recorder Recording() => new();

    private static async Task<Guid> BindDeviceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/devices", new BindDeviceRequest(Unique("Phone")));

        return (await response.Content.ReadFromJsonAsync<DeviceResponse>())!.Id;
    }

    private static async Task<Guid> OutletAsync(HttpClient client)
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var outlet = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, "Europe/Bucharest"));

        return (await outlet.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    private static PushedMutation Visit(Guid outletId) => new(
        Guid.CreateVersion7(),
        nameof(CapturedVisit),
        new CapturedVisit(
            Guid.CreateVersion7(),
            outletId,
            PlannedVisitId: null,
            CheckedInAtUtc: Yesterday,
            CheckInLatitude: 44.43,
            CheckInLongitude: 26.10,
            CheckInDistanceMetres: 12.5,
            WasInsideGeofence: true,
            OverrideReason: null,
            Steps: [],
            Outcome: "Productive",
            OutcomeReason: null,
            CheckedOutAtUtc: Yesterday.AddMinutes(25),
            CheckOutLatitude: 44.43,
            CheckOutLongitude: 26.10));

    /// <summary>One measurement, flattened to what an exporter would see.</summary>
    private sealed record Reading(string Instrument, double Value, IReadOnlyDictionary<string, string?> Tags);

    /// <summary>
    /// Collects every measurement the FieldKit meter publishes for as long as it is alive.
    /// </summary>
    /// <remarks>
    /// A <c>MeterListener</c> rather than <c>MetricCollector</c>: the testing package is a dependency
    /// this repository would otherwise not have, and what it wraps is this. Both numeric callbacks are
    /// registered because the three instruments are not all the same type — a listener that enables an
    /// instrument and then has no callback for its type receives nothing and reports no error, which
    /// is the quiet way this kind of test goes vacuous.
    /// </remarks>
    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<Reading> _readings = [];
        private readonly Lock _gate = new();

        public Recorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == Telemetry.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            };

            _listener.SetMeasurementEventCallback<int>(
                (instrument, measurement, tags, _) => Add(instrument, measurement, tags));

            _listener.SetMeasurementEventCallback<long>(
                (instrument, measurement, tags, _) => Add(instrument, measurement, tags));

            _listener.SetMeasurementEventCallback<double>(
                (instrument, measurement, tags, _) => Add(instrument, measurement, tags));

            _listener.Start();
        }

        /// <summary>The instrument names that published anything.</summary>
        public IEnumerable<string> Names
        {
            get
            {
                lock (_gate) return _readings.Select(reading => reading.Instrument).Distinct().ToList();
            }
        }

        public IReadOnlyList<Reading> Measurements(string instrument)
        {
            lock (_gate)
                return _readings.Where(reading => reading.Instrument == instrument).ToList();
        }

        public IReadOnlyList<double> Values(string instrument) =>
            Measurements(instrument).Select(reading => reading.Value).ToList();

        public void Dispose() => _listener.Dispose();

        private void Add(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var flattened = new Dictionary<string, string?>(tags.Length);

            foreach (var tag in tags) flattened[tag.Key] = tag.Value?.ToString();

            lock (_gate) _readings.Add(new Reading(instrument.Name, measurement, flattened));
        }
    }
}
