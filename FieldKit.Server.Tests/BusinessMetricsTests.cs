using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using FieldKit.BuildingBlocks;
using FieldKit.Modules.Audit;
using FieldKit.Modules.Audit.Contracts;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Order.Contracts;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Products.Contracts;
using FieldKit.Modules.Visit;
using FieldKit.Modules.Visit.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// The signals that say FieldKit is doing business, not just staying up
/// (<c>observability §2</c>) — W13 slice 4.
/// </summary>
/// <remarks>
/// <para>
/// <b>Emitted where the work happens rather than from an integration-event handler</b>, and the
/// tests are written against the work for the same reason. Visit and Order both raise events that
/// something now delivers (slice 3), so a handler was the tempting shape — and would have made every
/// business number depend on the outbox draining. A stalled dispatcher would flatten "visits
/// completed", which reads as *reps have stopped working*: the most expensive confusion available
/// between a business signal and an infrastructure one.
/// </para>
/// <para>
/// The second reason is smaller and just as final: neither <c>VisitCompleted</c> nor
/// <c>OrderSubmitted</c> carries a tenant, so a handler could not have tagged by tenant without
/// changing a published integration event for a metric's convenience.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class BusinessMetricsTests(ServerFixture fixture)
{
    private static readonly DateTimeOffset Yesterday = new(2026, 3, 17, 9, 0, 0, TimeSpan.Zero);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    [Fact]
    public async Task A_visit_finished_online_and_one_drained_from_a_phone_count_the_same()
    {
        /*
         * The property, in one test. To a supervisor these are the same event; they differ only in
         * how the rep's phone got here. Counting one and not the other would turn business
         * throughput into a measurement of how good the signal was in that territory — and the
         * territories with the worst signal are exactly the ones a supervisor is asking about.
         */
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outletId = await OutletAsync(admin);

        using var recorded = new Recorder();

        // Online: check in at the counter, then leave.
        var checkIn = await admin.PostAsJsonAsync(
            "/api/visits/check-in", new CheckInRequest(outletId, 44.43, 26.10));

        var visitId = (await checkIn.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit.Id;

        await admin.PostAsJsonAsync(
            $"/api/visits/{visitId}/check-out", new CheckOutRequest(VisitOutcome.Productive));

        // Offline: the same day's work, drained through the module the push path uses.
        await AsTenant.RunAsync(fixture, fixture.AdminAccessToken, services =>
            services.GetRequiredService<IVisitIngest>()
                .IngestAsync(Captured(outletId), AsTenant.SubjectOf(fixture.AdminAccessToken)));

        var completions = recorded.Measurements("fieldkit.visits.completed");

        Assert.Equal(2, completions.Count);
        Assert.All(completions, reading =>
            Assert.Equal("Productive", reading.Tags[Telemetry.Tags.Outcome]));
    }

    [Fact]
    public async Task A_non_productive_visit_is_counted_and_told_apart()
    {
        // Outcome is a tag because "reps are busy" and "reps are selling" are different questions,
        // and a strike rate is the supervisor dashboard's own vocabulary.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outletId = await OutletAsync(admin);

        using var recorded = new Recorder();

        await AsTenant.RunAsync(fixture, fixture.AdminAccessToken, services =>
            services.GetRequiredService<IVisitIngest>()
                .IngestAsync(
                    Captured(outletId, "NonProductive", "Shop shut"),
                    AsTenant.SubjectOf(fixture.AdminAccessToken)));

        Assert.Equal(
            "NonProductive",
            Assert.Single(recorded.Measurements("fieldkit.visits.completed")).Tags[Telemetry.Tags.Outcome]);
    }

    [Fact]
    public async Task A_visit_the_server_refuses_is_not_a_finished_visit()
    {
        /*
         * `BR-VIS-3`: a non-productive visit has to say why, and one that does not is refused. The
         * count must not move — a metric that counts attempts rather than outcomes would show a
         * territory getting busier precisely as its data quality fell apart.
         */
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outletId = await OutletAsync(admin);

        using var recorded = new Recorder();

        var result = await AsTenant.RunAsync(fixture, fixture.AdminAccessToken, services =>
            services.GetRequiredService<IVisitIngest>()
                .IngestAsync(
                    Captured(outletId, "NonProductive", reason: null),
                    AsTenant.SubjectOf(fixture.AdminAccessToken)));

        Assert.Equal(VisitIngestRefusal.OutcomeReasonRequired, result.Refusal);
        Assert.Empty(recorded.Measurements("fieldkit.visits.completed"));
    }

    [Fact]
    public async Task An_order_is_measured_in_the_currency_it_was_taken_in()
    {
        /*
         * A histogram mixing RON and EUR describes nothing — the buckets are the same numbers with
         * different meanings — so an amount without its currency is worse than no amount at all.
         * `BR-PRD-8` already treats a currency as part of a figure rather than decoration; this is
         * that rule at the metrics layer.
         *
         * And the value is the **device's** total (`BR-ORD-2`): the server re-prices and flags, so
         * charting its opinion would produce a commercial signal nobody in the shop ever saw.
         */
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var (visitId, productId) = await ShopWithAVisitAsync(admin, rep);

        using var recorded = new Recorder();

        var captured = new CapturedOrder(
            Guid.CreateVersion7(),
            visitId,
            "EUR",
            Total: 20.00m,
            Yesterday,
            [new CapturedOrderLine(productId, 2m, "CS", null, 10.00m, 20.00m)]);

        var result = await AsTenant.RunAsync(fixture, fixture.AdminAccessToken, services =>
            services.GetRequiredService<IOrderIngest>()
                .IngestAsync(captured, Guid.CreateVersion7(), AsTenant.SubjectOf(fixture.AdminAccessToken)));

        Assert.Equal(OrderIngestRefusal.None, result.Refusal);

        var reading = Assert.Single(recorded.Measurements("fieldkit.orders.submitted.value"));

        Assert.Equal(20.00, reading.Value);
        Assert.Equal("EUR", reading.Tags[Telemetry.Tags.Currency]);
    }

    [Fact]
    public async Task Pricing_is_timed_even_when_it_answers_nothing()
    {
        /*
         * An outlet this tenant does not have returns after one query rather than four. Recording
         * only the long path would bias the distribution towards the expensive case — and a p95 that
         * never sees the cheap answers is not a p95.
         *
         * This is also the test that makes the `finally` in `PriceAsync` load-bearing, unlike the one
         * in `/sync/push` that slice 1 found nothing covers: here the early return is a path a test
         * actually takes.
         */
        using var recorded = new Recorder();

        var priced = await AsTenant.RunAsync(fixture, fixture.AdminAccessToken, services =>
            services.GetRequiredService<IPricingService>()
                .PriceAsync(Guid.CreateVersion7(), new DateOnly(2026, 3, 17), [new(Guid.CreateVersion7(), 1m)]));

        Assert.Null(priced);
        Assert.Single(recorded.Measurements("fieldkit.pricing.resolve.duration"));
    }

    [Fact]
    public async Task The_photo_backlog_rises_with_an_audit_and_falls_when_the_bytes_arrive()
    {
        /*
         * The level, at both points that move it. There is no loop behind this gauge and there
         * cannot be: `PhotoEntry` is tenant-owned, so the global query filter reads the ambient
         * tenant — and a background service has no principal to read one from. Counting across
         * tenants from a loop would need `IgnoreQueryFilters`, which the build bans.
         *
         * So the two writes *are* the sampling, and asserting both is asserting the design.
         */
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var (visitId, productId) = await ShopWithAVisitAsync(admin, rep);
        var weights = await WeightingAsync(admin);
        var key = $"photos/{Guid.NewGuid():N}.jpg";

        using var recorded = new Recorder();

        var ingest = await AsTenant.RunAsync(fixture, fixture.AdminAccessToken, services =>
            services.GetRequiredService<IAuditIngest>().IngestAsync(
                new CapturedAudit(
                    Guid.CreateVersion7(),
                    visitId,
                    Yesterday,
                    WeightSetVersion: weights,
                    CategoryFacings: null,
                    // An audit that measures nothing is refused as Empty, so it has to
                    // record something for the photograph to have an audit to hang off.
                    Availability: [new CapturedAvailability(productId, AvailabilityStatus.Present)],
                    Facings: [],
                    Prices: [],
                    Photos: [new CapturedPhoto(AuditSection.Availability, key)]),
                AsTenant.SubjectOf(fixture.AdminAccessToken)));

        Assert.Equal(AuditIngestRefusal.None, ingest.Refusal);

        var afterIngest = recorded.Latest("fieldkit.photos.upload.pending");

        Assert.NotNull(afterIngest);

        // The bytes land. Absolute values are not asserted — this fixture is shared and other tests
        // leave photographs behind — but the *direction* is the whole claim.
        await admin.PostAsJsonAsync("/api/sync/photos/confirm", new { objectKeys = new[] { key } });

        Assert.Equal(afterIngest - 1, recorded.Latest("fieldkit.photos.upload.pending"));
    }

    /// <summary>A published weighting, read back rather than assumed.</summary>
    /// <remarks>
    /// The version counter is per tenant and this collection shares a database, so a test that named
    /// a version would pass alone and fail the moment another published first — the shape
    /// <c>AuditIngestTests</c> already uses, and for the reason it gives.
    /// </remarks>
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

    private static CapturedVisit Captured(Guid outletId, string outcome = "Productive", string? reason = null) =>
        new(
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
            Outcome: outcome,
            OutcomeReason: reason,
            CheckedOutAtUtc: Yesterday.AddMinutes(25),
            CheckOutLatitude: 44.43,
            CheckOutLongitude: 26.10);

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

    /// <summary>A shop, a product the rep may order, and an open visit to hang work off.</summary>
    private async Task<(Guid VisitId, Guid ProductId)> ShopWithAVisitAsync(
        HttpClient admin, HttpClient rep)
    {
        var channel = await admin.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var outlet = await admin.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, "Europe/Bucharest"));

        var outletId = (await outlet.Content.ReadFromJsonAsync<OutletResponse>())!.Id;

        using var products = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var product = await products.PostAsJsonAsync(
            "/api/products", new { code = Unique("SKU"), name = "Beer", unitOfMeasure = "CS" });

        var productId = (await product.Content.ReadFromJsonAsync<CreatedId>())!.Id;

        var checkIn = await admin.PostAsJsonAsync(
            "/api/visits/check-in", new CheckInRequest(outletId, 44.43, 26.10));

        var visitId = (await checkIn.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit.Id;

        return (visitId, productId);
    }

    private sealed record CreatedId(Guid Id);

    private sealed record Reading(string Instrument, double Value, IReadOnlyDictionary<string, string?> Tags);

    /// <summary>Collects what the FieldKit meter publishes, the way an exporter would.</summary>
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

            _listener.SetMeasurementEventCallback<int>((i, m, t, _) => Add(i.Name, m, t));
            _listener.SetMeasurementEventCallback<long>((i, m, t, _) => Add(i.Name, m, t));
            _listener.SetMeasurementEventCallback<double>((i, m, t, _) => Add(i.Name, m, t));

            _listener.Start();
        }

        public IReadOnlyList<Reading> Measurements(string instrument)
        {
            lock (_gate) return _readings.Where(reading => reading.Instrument == instrument).ToList();
        }

        public double? Latest(string instrument) =>
            Measurements(instrument) is { Count: > 0 } readings ? readings[^1].Value : null;

        public void Dispose() => _listener.Dispose();

        private void Add(string instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var flattened = new Dictionary<string, string?>(tags.Length);

            foreach (var tag in tags) flattened[tag.Key] = tag.Value?.ToString();

            lock (_gate) _readings.Add(new Reading(instrument, value, flattened));
        }
    }
}
