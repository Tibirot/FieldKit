using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Visit;
using FieldKit.Modules.Visit.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// Counting visits by outcome (<c>VIS-10</c>) — W12 slice 1.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a contract with no caller yet</b> — <c>/api/reporting/summary</c> is W12 slice 3 — so
/// these tests are the only thing standing between the shape and a guess. They are written against a
/// <b>seeded month</b> for exactly that reason: an aggregate is the easiest kind of query to prove
/// vacuously, since a filter that matches nothing returns zeroes and every assertion about "not
/// counted" passes on an empty table. Every "excluded" case here therefore has a matching "included"
/// one a few days away, so a broken window fails rather than silently agrees.
/// </para>
/// <para>
/// The visits arrive through <see cref="IVisitIngest"/>, which is the path a device that was offline
/// uses and the only one that takes a <i>date</i>: check-in stamps the clock, so a month of history
/// cannot be built through the HTTP route. Open visits do come from check-in, because that is the
/// only way a visit exists without an outcome.
/// </para>
/// <para>
/// <b>Scoped by outlet ids that this test minted</b>, so the shared fixture database — which other
/// tests are writing visits into — cannot move a number here. That is a property of the contract
/// rather than a convenience: it takes the shops it was asked about and counts nothing else.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class VisitQueryTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    /// <summary>A shop on Calea Dorobanți, and a doorway to stand in.</summary>
    private static readonly Coordinates Shop = new(44.4638, 26.0946);

    /// <summary>A month in the past, so "today" cannot wander into the window.</summary>
    private static readonly DateOnly MarchFirst = new(2026, 3, 1);
    private static readonly DateOnly MarchLast = new(2026, 3, 31);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    [Fact]
    public async Task A_month_at_two_shops_is_counted_by_outcome()
    {
        using var client = Admin();

        var mine = await OutletAsync(client);
        var alsoMine = await OutletAsync(client);
        var notMine = await OutletAsync(client);

        await IngestAsync(mine, new DateOnly(2026, 3, 3), VisitOutcome.Productive);
        await IngestAsync(mine, new DateOnly(2026, 3, 10), VisitOutcome.Productive);
        await IngestAsync(alsoMine, new DateOnly(2026, 3, 17), VisitOutcome.Productive);
        await IngestAsync(alsoMine, new DateOnly(2026, 3, 18), VisitOutcome.NonProductive);

        // The two that must not be counted, each the mirror of one that is: a visit at a shop outside
        // the scope, and a visit inside the scope but a month early.
        await IngestAsync(notMine, new DateOnly(2026, 3, 11), VisitOutcome.Productive);
        await IngestAsync(mine, new DateOnly(2026, 2, 11), VisitOutcome.Productive);

        var counts = await CountAsync([mine, alsoMine], MarchFirst, MarchLast);

        Assert.Equal(3, counts.Productive);
        Assert.Equal(1, counts.NonProductive);
        Assert.Equal(0, counts.Open);

        Assert.Equal(4, counts.Total);
        Assert.Equal(4, counts.Finished);
        Assert.Equal(0.75m, counts.StrikeRate);
    }

    [Fact]
    public async Task Both_ends_of_the_window_are_inside_it()
    {
        /*
         * The assertion the implementation is most likely to get wrong, and the reason it is worth a
         * test of its own: the contract promises two inclusive dates and the query is written as a
         * half-open range of instants, `>= from 00:00` and `< the day after to`. An off-by-one in
         * that translation loses the last day of every month a supervisor asks about — and would be
         * invisible to the test above, whose visits are all comfortably mid-month.
         *
         * All four visits are at the same shop, so nothing but the date can explain the answer.
         */
        using var client = Admin();

        var outletId = await OutletAsync(client);

        await IngestAsync(outletId, MarchFirst, VisitOutcome.Productive);
        await IngestAsync(outletId, MarchLast, VisitOutcome.Productive);
        await IngestAsync(outletId, MarchFirst.AddDays(-1), VisitOutcome.Productive);
        await IngestAsync(outletId, MarchLast.AddDays(1), VisitOutcome.Productive);

        var counts = await CountAsync([outletId], MarchFirst, MarchLast);

        Assert.Equal(2, counts.Productive);

        // …and the day either side is genuinely there, so this cannot pass by counting nothing.
        var wider = await CountAsync([outletId], MarchFirst.AddDays(-1), MarchLast.AddDays(1));

        Assert.Equal(4, wider.Productive);
    }

    [Fact]
    public async Task A_visit_the_rep_is_still_working_is_open_and_outside_the_ratio()
    {
        // Check-in rather than ingest, because an ingested visit is sealed on arrival — an open one
        // only exists while somebody is standing in the shop. It is dated now, so the window is today.
        using var client = Admin();

        var outletId = await OutletAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/visits/check-in", new CheckInRequest(outletId, Shop.Latitude, Shop.Longitude));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // The day the *server* stamped, read back off the visit rather than taken from this
        // process's clock — which is also what keeps `DateTime.UtcNow` out of a test file.
        var visit = (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;
        var today = DateOnly.FromDateTime(visit.CheckedInAtUtc.UtcDateTime);

        var counts = await CountAsync([outletId], today, today);

        Assert.Equal(1, counts.Open);
        Assert.Equal(1, counts.Total);

        // The whole reason `Open` is not a third outcome: a rep mid-visit has not failed, so the
        // strike rate is undefined rather than zero, and a supervisor reading this at 10am sees "one
        // call in progress" rather than "0% and something is wrong".
        Assert.Equal(0, counts.Finished);
        Assert.Null(counts.StrikeRate);
    }

    [Fact]
    public async Task No_shops_in_scope_means_no_visits_rather_than_every_visit()
    {
        /*
         * A supervisor whose scope resolves to nothing must see nothing. The hazard is the standard
         * one for a filtered query — reading an empty filter as "no filter" — and here it is
         * doubly worth pinning: `Contains` over an empty set does translate to `false` in Postgres,
         * so the query would in fact answer zeroes on its own. This asserts the decision rather than
         * the provider's behaviour, and the seeded visits are what make the zero mean something.
         */
        using var client = Admin();

        var outletId = await OutletAsync(client);

        await IngestAsync(outletId, new DateOnly(2026, 3, 4), VisitOutcome.Productive);
        await IngestAsync(outletId, new DateOnly(2026, 3, 5), VisitOutcome.NonProductive);

        Assert.Equal(2, (await CountAsync([outletId], MarchFirst, MarchLast)).Total);

        var counts = await CountAsync([], MarchFirst, MarchLast);

        Assert.Equal(0, counts.Total);
        Assert.Null(counts.StrikeRate);
    }

    [Fact]
    public async Task Another_tenants_visits_are_not_counted_even_when_their_outlets_are_named()
    {
        /*
         * The failure this refuses is a report, not a leak of rows — but a count is a fact about
         * somebody else's business all the same, and the caller here is a host composition that will
         * be handed outlet ids by a scope resolver. Naming another tenant's shops is the shape a bug
         * in that resolver would take, so the query is asserted to answer zero rather than to trust
         * the ids it was given: the global filter is what makes that true, and this is what proves
         * the filter is on.
         */
        using var theirs = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var theirOutlet = await OutletAsync(theirs);

        await IngestAsync(
            theirOutlet, new DateOnly(2026, 3, 6), VisitOutcome.Productive,
            token: fixture.TenantBAccessToken);

        // They can see it…
        var seen = await CountAsync(
            [theirOutlet], MarchFirst, MarchLast, token: fixture.TenantBAccessToken);

        Assert.Equal(1, seen.Productive);

        // …and we cannot, holding the outlet id in our hand.
        var ours = await CountAsync([theirOutlet], MarchFirst, MarchLast);

        Assert.Equal(0, ours.Total);
    }

    /// <summary>Asks the contract, in a tenant context matching <paramref name="token"/>.</summary>
    private Task<VisitOutcomeCounts> CountAsync(
        IReadOnlyCollection<Guid> outletIds, DateOnly from, DateOnly to, string? token = null) =>
        AsTenant.RunAsync(fixture, token ?? fixture.AdminAccessToken, services => services
            .GetRequiredService<IVisitQuery>()
            .CountByOutcomeAsync(outletIds, from, to));

    /// <summary>A visit that happened on <paramref name="on"/> and came out the way it came out.</summary>
    /// <remarks>
    /// Mid-morning rather than midnight, so a visit is never sitting on the boundary this file is
    /// trying to test — a day-boundary bug should fail the dates, not the hours.
    /// </remarks>
    private async Task IngestAsync(
        Guid outletId, DateOnly on, VisitOutcome outcome, string? token = null)
    {
        var checkedIn = new DateTimeOffset(on.ToDateTime(new TimeOnly(9, 30)), TimeSpan.Zero);

        var captured = new CapturedVisit(
            VisitId: Guid.CreateVersion7(),
            OutletId: outletId,
            PlannedVisitId: null,
            CheckedInAtUtc: checkedIn,
            CheckInLatitude: Shop.Latitude,
            CheckInLongitude: Shop.Longitude,
            CheckInDistanceMetres: 0,
            WasInsideGeofence: true,
            OverrideReason: null,
            Steps: [],
            Outcome: outcome.ToString(),
            OutcomeReason: outcome == VisitOutcome.NonProductive ? "Shop closed" : null,
            CheckedOutAtUtc: checkedIn.AddMinutes(20),
            CheckOutLatitude: Shop.Latitude,
            CheckOutLongitude: Shop.Longitude);

        var result = await AsTenant.RunAsync(
            fixture, token ?? fixture.AdminAccessToken, services => services
                .GetRequiredService<IVisitIngest>()
                .IngestAsync(captured, AsTenant.SubjectOf(token ?? fixture.AdminAccessToken)));

        Assert.Equal(VisitIngestRefusal.None, result.Refusal);
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
