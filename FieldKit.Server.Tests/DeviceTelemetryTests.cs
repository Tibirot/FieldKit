using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using FieldKit.BuildingBlocks;
using FieldKit.Modules.Sync;

namespace FieldKit.Server.Tests;

/// <summary>
/// What a field device says when it is failing quietly (<c>observability §5</c>) — W13 slice 8.
/// </summary>
/// <remarks>
/// <para>
/// <b>The only signal in W13 that measures the client.</b> Every other one counts work that arrived;
/// a rep whose local store was evicted, whose service worker never installed, or whose sync has been
/// failing for a week looks from here exactly like a rep having a quiet week — the same absence of
/// visits, orders and pulls. There is no SSH into a field fleet.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class DeviceTelemetryTests(ServerFixture fixture)
{
    private static readonly DateTimeOffset Yesterday = new(2026, 3, 17, 9, 0, 0, TimeSpan.Zero);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    [Fact]
    public async Task What_a_device_reports_is_counted_by_kind()
    {
        /*
         * The counter is what makes this alertable rather than merely searchable: a rate of
         * `StorageEvicted` climbing after a release should find an operator, not wait to be found.
         * The detail stays in the log line, where a question about one phone belongs.
         */
        using var rep = fixture.CreateAuthenticatedClient();

        var device = await BindAsync(rep);

        using var recorded = new Recorder();

        var response = await rep.PostAsJsonAsync("/api/sync/telemetry", new DeviceTelemetryRequest(
            device,
            [
                new DeviceEvent(DeviceEventKind.StorageEvicted, Yesterday, "the browser discarded it"),
                new DeviceEvent(DeviceEventKind.SyncFailed, Yesterday, "sync.push.deviceUnknown"),
            ]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<DeviceTelemetryResponse>())!.Accepted);

        var kinds = recorded.Kinds("fieldkit.device.events");

        Assert.Contains("StorageEvicted", kinds);
        Assert.Contains("SyncFailed", kinds);
    }

    [Fact]
    public async Task A_device_that_is_not_this_rep_s_is_refused()
    {
        /*
         * Nothing here is checked against reality — these are log lines a caller composes — so the
         * gate is the only thing between this endpoint and a log-injection hole with a rate limit.
         * The same answer as `/sync/push`, and for the same reason: a device id is a guessable shape,
         * so unknown and not-yours are one response.
         */
        using var rep = fixture.CreateAuthenticatedClient();

        var response = await rep.PostAsJsonAsync("/api/sync/telemetry", new DeviceTelemetryRequest(
            Guid.CreateVersion7(),
            [new DeviceEvent(DeviceEventKind.UnhandledError, Yesterday)]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            "sync.telemetry.deviceUnknown",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task A_kind_this_server_does_not_know_is_refused_rather_than_bucketed()
    {
        /*
         * A closed vocabulary on both sides. Silently accepting an unknown kind would mean a client
         * shipping a typo reports nothing for a release and nobody finds out — the failure worth
         * having is the loud one, at the first batch.
         *
         * A 400 from the binder rather than a refusal code: the value is not one the enum has, so no
         * handler runs (`api-contracts §3.1`).
         */
        using var rep = fixture.CreateAuthenticatedClient();

        var device = await BindAsync(rep);

        var response = await rep.PostAsJsonAsync("/api/sync/telemetry", new
        {
            deviceId = device,
            events = new[] { new { kind = "TheBatteryFellOut", occurredAtUtc = Yesterday } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_batch_larger_than_the_limit_is_refused_whole()
    {
        // A device with more than fifty distinct things to say should send the oldest and keep the
        // rest — the first failure explains the others, and truncating silently would hide which
        // end was dropped.
        using var rep = fixture.CreateAuthenticatedClient();

        var device = await BindAsync(rep);

        var response = await rep.PostAsJsonAsync("/api/sync/telemetry", new DeviceTelemetryRequest(
            device,
            Enumerable
                .Range(0, TelemetryEndpoints.MaximumEvents + 1)
                .Select(_ => new DeviceEvent(DeviceEventKind.UnhandledError, Yesterday))
                .ToList()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "sync.telemetry.batchTooLarge",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task A_location_a_client_sends_anyway_is_discarded_before_anything_reads_it()
    {
        /*
         * <b>The guarantee is structural, and this is what says so.</b> `observability §5` and
         * `security §4` both promise that no location is captured outside a visit check-in, and the
         * enforcement here is not validation — it is that <c>DeviceEvent</c> has nowhere to put one.
         * A client sending latitude and longitude has them dropped by the deserializer before any
         * code sees them, which is stronger than a check because there is no branch to get wrong.
         *
         * The batch is <b>accepted</b>, which is the point: the device is not punished for a field
         * this server does not want, and the field does not arrive.
         */
        using var rep = fixture.CreateAuthenticatedClient();

        var device = await BindAsync(rep);

        var response = await rep.PostAsJsonAsync("/api/sync/telemetry", new
        {
            deviceId = device,
            events = new[]
            {
                new
                {
                    kind = "UnhandledError",
                    occurredAtUtc = Yesterday,
                    detail = "boom",
                    latitude = 44.43,
                    longitude = 26.10,
                },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<DeviceTelemetryResponse>())!.Accepted);
    }

    [Fact]
    public async Task Reporting_needs_a_token()
    {
        // It is an authenticated endpoint like the rest of `/sync`: telemetry names a device, and a
        // device belongs to a rep.
        var response = await fixture.Client.PostAsJsonAsync(
            "/api/sync/telemetry",
            new DeviceTelemetryRequest(Guid.CreateVersion7(), []));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<Guid> BindAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/devices", new BindDeviceRequest(Unique("Phone")));

        return (await response.Content.ReadFromJsonAsync<DeviceResponse>())!.Id;
    }

    /// <summary>Collects what the FieldKit meter publishes, the way an exporter would.</summary>
    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Instrument, string? Kind)> _readings = [];
        private readonly Lock _gate = new();

        public Recorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == Telemetry.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            };

            _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            {
                string? kind = null;

                foreach (var tag in tags)
                    if (tag.Key == Telemetry.Tags.Kind) kind = tag.Value?.ToString();

                lock (_gate) _readings.Add((instrument.Name, kind));
            });

            _listener.Start();
        }

        public IReadOnlyList<string?> Kinds(string instrument)
        {
            lock (_gate)
                return _readings.Where(r => r.Instrument == instrument).Select(r => r.Kind).ToList();
        }

        public void Dispose() => _listener.Dispose();
    }
}
