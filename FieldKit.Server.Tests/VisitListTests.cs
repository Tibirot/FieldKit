using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Visit;
using FieldKit.Modules.Visit.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// Reading back what was recorded (<c>VIS-10</c>) — W12 slice 5a.
/// </summary>
/// <remarks>
/// <para>
/// <b>The list had no ceiling until this slice, and the reason it went unnoticed is worth keeping.</b>
/// Its only caller was a rep's device, which always passes an outlet or a user — so the unbounded
/// case never ran. The back-office screen is the first caller that asks the tenant-wide question, and
/// a read whose cost grows with how long the tenant has existed is an outage that development never
/// sees.
/// </para>
/// <para>
/// The ordering is asserted alongside, because a ceiling on an unordered list is worse than no
/// ceiling: it would return an arbitrary two hundred rather than the two hundred a supervisor wants.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class VisitListTests(ServerFixture fixture)
{
    /// <summary>
    /// Mirrors <c>VisitEndpoints.MaximumVisits</c>, which is internal and stays that way.
    /// </summary>
    /// <remarks>
    /// The endpoint class is an implementation detail of the module — nothing outside it maps routes
    /// — and this project has no <c>InternalsVisibleTo</c> anywhere, deliberately: a test that can
    /// see internals is a test that can pin them. The cost is this one duplicated number. What the
    /// assertions below are careful to do is fail on the *property* (the answer is bounded, and it
    /// is the newest rows) rather than on the value, so a deliberate change to the ceiling turns
    /// this red only if it also broke the bound.
    /// </remarks>
    private const int Ceiling = 200;

    private const string Zone = "Europe/Bucharest";

    private static readonly Coordinates Shop = new(44.4638, 26.0946);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    [Fact]
    public async Task Newest_first_and_never_more_than_the_ceiling()
    {
        /*
         * One shop, one more visit than the ceiling allows, and the filter aimed at that shop — so
         * the test carries its own evidence rather than borrowing the suite's.
         *
         * The first draft asserted against the tenant's total instead, on the assumption that a
         * shared database "obviously" holds more than a few hundred visits by the time the suite
         * runs. Run alone, it held three: an assertion about the ceiling that was really an
         * assertion about which other tests had gone first. Exactly the order-dependence this file's
         * neighbour was fixed for one slice ago.
         *
         * The dates run backwards from the newest so that the one that must fall off is the *oldest*
         * — and it is named, so "the ceiling returned an arbitrary slice" fails rather than passes.
         */
        using var client = Admin();

        var outletId = await OutletAsync(client);
        var newest = new DateOnly(2026, 7, 31);

        for (var day = 0; day <= Ceiling; day++)
        {
            await VisitedAsync(outletId, newest.AddDays(-day));
        }

        var mine = await client.GetFromJsonAsync<List<VisitResponse>>($"/api/visits?outletId={outletId}");

        Assert.Equal(Ceiling, mine!.Count);

        var dates = mine.Select(visit => DateOnly.FromDateTime(visit.CheckedInAtUtc.UtcDateTime)).ToList();

        // Newest first, by when the rep arrived rather than when the server stored it — every one of
        // these was ingested in the opposite order to the one they come back in.
        Assert.Equal(dates.OrderByDescending(date => date), dates);
        Assert.Equal(newest, dates[0]);

        // The oldest is the one that fell off, which is what makes this a ceiling on the *newest*
        // rather than on whichever rows the database felt like returning.
        Assert.DoesNotContain(newest.AddDays(-Ceiling), dates);
        Assert.Equal(newest.AddDays(-(Ceiling - 1)), dates[^1]);
    }

    [Fact]
    public async Task A_reader_without_permission_is_refused()
    {
        // The list is the whole of a rep's day, at every shop they called on. `rep` holds no
        // `visit:read` — reading back is oversight, and performing a visit is not.
        using var rep = fixture.CreateAuthenticatedClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await rep.GetAsync("/api/visits")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("/api/visits")).StatusCode);
    }

    private async Task VisitedAsync(Guid outletId, DateOnly on)
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
            Outcome: nameof(VisitOutcome.Productive),
            OutcomeReason: null,
            CheckedOutAtUtc: checkedIn.AddMinutes(20),
            CheckOutLatitude: Shop.Latitude,
            CheckOutLongitude: Shop.Longitude);

        var result = await AsTenant.RunAsync(fixture, fixture.AdminAccessToken, services => services
            .GetRequiredService<IVisitIngest>()
            .IngestAsync(captured, AsTenant.SubjectOf(fixture.AdminAccessToken)));

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
