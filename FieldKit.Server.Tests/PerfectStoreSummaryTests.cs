using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Audit.Contracts;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Visit;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// Perfect store across a territory and a month (<c>AUD-09</c>) — W12 slice 2b.
/// </summary>
/// <remarks>
/// <para>
/// <b>The aggregate is asserted against the audits it aggregates, not against a number I worked out
/// by hand.</b> Each audit's own score is read back through <c>ForVisitAsync</c> — the read W10 has
/// already pinned — and the mean of those is what the summary has to equal. That keeps this file
/// about the <i>aggregation</i>: the scorer's arithmetic is <c>PerfectStoreScoreTests</c>' business,
/// and duplicating its expected values here would produce a test that fails twice for one change.
/// </para>
/// <para>
/// The scores are checked to be <b>distinct</b> before the mean is compared, because a mean of
/// identical numbers equals any of them — a summary that returned the first audit's score would pass
/// a careless version of this test.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class PerfectStoreSummaryTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    private static readonly Coordinates Shop = new(44.4638, 26.0946);

    private static readonly DateOnly AprilFirst = new(2026, 4, 1);
    private static readonly DateOnly AprilLast = new(2026, 4, 30);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    [Fact]
    public async Task The_average_is_the_mean_of_the_audits_it_summarises()
    {
        using var client = Admin();

        var outletId = await OutletAsync(client);
        var weights = await WeightingAsync(client);

        // Three shelves in three different states, so the scores differ and a mean means something.
        var first = await AuditAsync(client, outletId, weights, new DateOnly(2026, 4, 6), stocked: 3, facings: 30);
        var second = await AuditAsync(client, outletId, weights, new DateOnly(2026, 4, 13), stocked: 2, facings: 20);
        var third = await AuditAsync(client, outletId, weights, new DateOnly(2026, 4, 20), stocked: 1, facings: 4);

        var scores = new[] { first, second, third };

        Assert.All(scores, score => Assert.NotNull(score));
        Assert.Equal(3, scores.Distinct().Count());

        var summary = await SummariseAsync([outletId]);

        Assert.Equal(3, summary.Audits);
        Assert.Equal(3, summary.Scored);

        /*
         * Rounded on both sides, and the reason is a finding rather than a convenience.
         *
         * The first run of this compared the raw values and failed: Postgres returned
         * 66.8366666666666667 where C# computed 66.836666666666666666666666667 — the same mean, and
         * the same to sixteen digits. `avg(numeric)` works at the engine's scale, so an unrounded
         * aggregate is not reproducible off the database that produced it.
         *
         * The contract now rounds half-up to two places, which is `BR-PRD-9`'s policy and the one
         * every score being averaged already carries.
         */
        var mean = Math.Round(
            scores.Average(score => score!.Value), 2, MidpointRounding.AwayFromZero);

        Assert.Equal(mean, summary.AverageScore);
    }

    [Fact]
    public async Task An_audit_nobody_could_score_is_counted_and_not_averaged()
    {
        /*
         * A rep who could measure nothing *scorable* — no availability, no aisle total to divide
         * facings by, no expected price to compare against — leaves an audit with a null score.
         * Averaging it in as zero would be a claim about a shop nobody managed to look at, which is
         * the distinction `Audit.Score` refuses at capture and `StrikeRate` refuses on the visit
         * side.
         *
         * Found while writing this: an audit carrying *nothing at all* never reaches the schema —
         * `AuditRefusal.Empty` turns it away at ingest. So the unscorable case is narrower than it
         * first looks, and the fixture has to record something that scores nothing rather than
         * record nothing.
         *
         * The scored audit beside it is what makes the average non-null, so this cannot pass by
         * returning nothing at all.
         */
        using var client = Admin();

        var outletId = await OutletAsync(client);
        var weights = await WeightingAsync(client);

        var scored = await AuditAsync(client, outletId, weights, new DateOnly(2026, 4, 7), stocked: 3, facings: 30);
        var unscorable = await AuditAsync(client, outletId, weights, new DateOnly(2026, 4, 8), measured: false);

        Assert.NotNull(scored);
        Assert.Null(unscorable);

        var summary = await SummariseAsync([outletId]);

        Assert.Equal(2, summary.Audits);
        Assert.Equal(1, summary.Scored);
        Assert.Equal(scored, summary.AverageScore);
    }

    [Fact]
    public async Task A_pillar_nobody_could_measure_is_skipped_rather_than_zero()
    {
        /*
         * `BR-AUD-2`: without a captured category total, share of shelf is renormalised out of the
         * score rather than counted as zero. The aggregate has to carry that through — an average
         * that folded the skip in as 0% would drag a territory's share-of-shelf down for every shop
         * where the rep could not count the aisle.
         *
         * Both audits measure availability, so the pillar that *was* measured is the control: it has
         * two behind it where share of shelf has one.
         */
        using var client = Admin();

        var outletId = await OutletAsync(client);
        var weights = await WeightingAsync(client);

        await AuditAsync(client, outletId, weights, new DateOnly(2026, 4, 9), stocked: 3, facings: 30);
        await AuditAsync(client, outletId, weights, new DateOnly(2026, 4, 10), stocked: 3, facings: 30, countedAisle: false);

        var summary = await SummariseAsync([outletId]);

        var shareOfShelf = Single(summary, ScorePillar.ShareOfShelf);

        Assert.Equal(1, shareOfShelf.Measured);
        Assert.Equal(1, shareOfShelf.Skipped);

        // The average is over the one that was measured — not halved by the one that was not.
        var measuredAlone = await SummariseAsync([outletId], AprilFirst, new DateOnly(2026, 4, 9));

        Assert.Equal(Single(measuredAlone, ScorePillar.ShareOfShelf).Average, shareOfShelf.Average);

        var availability = Single(summary, ScorePillar.Availability);

        Assert.Equal(2, availability.Measured);
        Assert.Equal(0, availability.Skipped);
    }

    [Fact]
    public async Task Two_weight_sets_in_one_window_are_named_rather_than_hidden()
    {
        /*
         * `BR-AUD-8` records the weighting each audit was scored against because a re-weighting
         * cannot be undone afterwards. An average across two of them is an average of two rulers —
         * still worth showing, since a supervisor whose weights changed mid-month still needs the
         * month, but a five-point movement across that boundary is not a change in their shops.
         *
         * So the contract says which versions it mixed rather than refusing the number.
         */
        using var client = Admin();

        var outletId = await OutletAsync(client);

        var original = await WeightingAsync(client);
        await AuditAsync(client, outletId, original, new DateOnly(2026, 4, 14), stocked: 3, facings: 30);

        var oneRuler = await SummariseAsync([outletId]);

        Assert.True(oneRuler.Comparable);
        Assert.Equal([original], oneRuler.WeightSetVersions);

        // Availability now carries the weight share of shelf used to.
        var reweighted = await WeightingAsync(client, availability: 80m, shareOfShelf: 0m, price: 20m);
        await AuditAsync(client, outletId, reweighted, new DateOnly(2026, 4, 15), stocked: 3, facings: 30);

        var twoRulers = await SummariseAsync([outletId]);

        Assert.False(twoRulers.Comparable);
        Assert.Equal([original, reweighted], twoRulers.WeightSetVersions);

        // …and it still answers, because withholding the month is not the contract's job.
        Assert.Equal(2, twoRulers.Audits);
        Assert.NotNull(twoRulers.AverageScore);
    }

    [Fact]
    public async Task It_answers_about_the_shops_and_the_days_it_was_asked_about()
    {
        // Scope and window, each with the mirror that keeps it from passing on an empty set: the
        // shop left out and the days either side all hold audits of their own.
        using var client = Admin();

        var outletId = await OutletAsync(client);
        var elsewhere = await OutletAsync(client);
        var weights = await WeightingAsync(client);

        await AuditAsync(client, outletId, weights, AprilFirst, stocked: 3, facings: 30);
        await AuditAsync(client, outletId, weights, AprilLast, stocked: 2, facings: 20);
        await AuditAsync(client, outletId, weights, AprilFirst.AddDays(-1), stocked: 3, facings: 30);
        await AuditAsync(client, outletId, weights, AprilLast.AddDays(1), stocked: 3, facings: 30);
        await AuditAsync(client, elsewhere, weights, new DateOnly(2026, 4, 16), stocked: 3, facings: 30);

        // Both ends of the window are inside it, and the days outside are not.
        Assert.Equal(2, (await SummariseAsync([outletId])).Audits);

        Assert.Equal(
            4, (await SummariseAsync([outletId], AprilFirst.AddDays(-1), AprilLast.AddDays(1))).Audits);

        // The other shop's audit is real and simply not this question's.
        Assert.Equal(1, (await SummariseAsync([elsewhere])).Audits);
        Assert.Equal(3, (await SummariseAsync([outletId, elsewhere])).Audits);

        var nothing = await SummariseAsync([]);

        Assert.Equal(0, nothing.Audits);
        Assert.Null(nothing.AverageScore);
        Assert.Empty(nothing.Pillars);

        // An average that does not exist cannot mislead, so an empty window is comparable.
        Assert.True(nothing.Comparable);
    }

    private static PillarAverage Single(PerfectStoreSummary summary, ScorePillar pillar) =>
        summary.Pillars.Single(row => row.Pillar == pillar.ToString());

    private Task<PerfectStoreSummary> SummariseAsync(
        IReadOnlyCollection<Guid> outletIds, DateOnly? from = null, DateOnly? to = null) =>
        AsTenant.RunAsync(fixture, fixture.AdminAccessToken, services => services
            .GetRequiredService<IAuditQuery>()
            .SummariseAsync(outletIds, from ?? AprilFirst, to ?? AprilLast));

    /// <summary>
    /// An audit captured on <paramref name="on"/>, and the score the server gave it.
    /// </summary>
    /// <param name="stocked">How many of three products were on the shelf.</param>
    /// <param name="facings">This brand's facings, against an aisle of 40.</param>
    /// <param name="countedAisle">False leaves the share-of-shelf denominator uncounted — skipped.</param>
    /// <param name="measured">
    /// False leaves every pillar unscorable — no availability, no aisle total, no expected price —
    /// while still recording something, since an audit carrying nothing at all is refused outright.
    /// </param>
    private async Task<decimal?> AuditAsync(
        HttpClient client,
        Guid outletId,
        int weightSetVersion,
        DateOnly on,
        int stocked = 3,
        int facings = 30,
        bool countedAisle = true,
        bool measured = true)
    {
        var visitId = await VisitAsync(client, outletId);

        var availability = measured
            ? Enumerable.Range(0, 3)
                .Select(index => new CapturedAvailability(
                    Guid.CreateVersion7(),
                    index < stocked ? AvailabilityStatus.Present : AvailabilityStatus.OutOfStock))
                .ToArray()
            : [];

        var captured = new CapturedAudit(
            Guid.CreateVersion7(),
            visitId,
            new DateTimeOffset(on.ToDateTime(new TimeOnly(11, 0)), TimeSpan.Zero),
            weightSetVersion,
            CategoryFacings: measured && countedAisle ? 40 : null,
            Availability: availability,
            Facings: [new CapturedFacings(Guid.CreateVersion7(), facings)],

            // An unscorable audit still carries lines — the aggregate refuses one that carries
            // nothing at all (`AuditRefusal.Empty`), so "nothing could be scored" has to be built
            // out of measurements that score nothing: facings with no aisle total to divide by, and
            // an observed price with no expected one to compare against.
            Prices:
            [
                new CapturedPrice(
                    Guid.CreateVersion7(), 1099, measured ? 1099 : null, "RON"),
            ]);

        var result = await AsTenant.RunAsync(fixture, fixture.AdminAccessToken, services => services
            .GetRequiredService<IAuditIngest>()
            .IngestAsync(captured, AsTenant.SubjectOf(fixture.AdminAccessToken)));

        Assert.Equal(AuditIngestRefusal.None, result.Refusal);

        var stored = await AsTenant.RunAsync(fixture, fixture.AdminAccessToken, services => services
            .GetRequiredService<IAuditQuery>()
            .ForVisitAsync(visitId));

        return stored!.Score;
    }

    /// <summary>Publishes a weighting and returns its version.</summary>
    /// <remarks>
    /// The version is read back rather than assumed: these tests share a database, and the tenant's
    /// version counter is genuinely shared state — the same reason <c>AuditIngestTests</c> gives.
    /// </remarks>
    private static async Task<int> WeightingAsync(
        HttpClient client,
        decimal availability = 50m,
        decimal shareOfShelf = 30m,
        decimal price = 20m)
    {
        var drafted = await client.PostAsJsonAsync("/api/config/score-weights", new ScoreWeightSetRequest([
            new ScoreWeightRequest(ScorePillar.Availability, availability),
            new ScoreWeightRequest(ScorePillar.ShareOfShelf, shareOfShelf),
            new ScoreWeightRequest(ScorePillar.PriceCompliance, price),
        ]));

        Assert.Equal(HttpStatusCode.Created, drafted.StatusCode);

        var version = (await drafted.Content.ReadFromJsonAsync<ScoreWeightSetResponse>())!.Version;

        var published = await client.PostAsync($"/api/config/score-weights/{version}/publish", null);

        Assert.Equal(HttpStatusCode.OK, published.StatusCode);

        return version;
    }

    private static async Task<Guid> VisitAsync(HttpClient client, Guid outletId)
    {
        var response = await client.PostAsJsonAsync(
            "/api/visits/check-in", new CheckInRequest(outletId, Shop.Latitude, Shop.Longitude));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit.Id;
    }

    private static async Task<Guid> OutletAsync(HttpClient client)
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var created = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, Location: Shop));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return (await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }
}
