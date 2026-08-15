using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.Modules.Journey;
using FieldKit.Modules.Journey.Contracts;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Org;
using FieldKit.Modules.Outlets;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// Counting what a round promised (<c>JRN-04</c>, <c>BR-JRN-6</c>) — W12 slice 2a.
/// </summary>
/// <remarks>
/// <para>
/// Coverage's <b>denominator</b>. The numerator is Visit's, and the two only mean something together
/// — see <see cref="VisitCoverageTests"/>, which asserts the half this module cannot see.
/// </para>
/// <para>
/// The plans here are generated and published the way an administrator would, through four modules'
/// endpoints, rather than written into the schema. That is deliberate: the count is over
/// <b>published</b> plans and over the calls a real generation run produces, and a hand-seeded row
/// would let this pass while the thing a supervisor actually looks at is empty.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class JourneyCoverageTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    /// <summary>A Monday, and a four-week window — a whole number of weekly cycles.</summary>
    private static readonly DateOnly From = new(2027, 6, 7);
    private static readonly DateOnly To = new(2027, 7, 4);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    [Fact]
    public async Task A_published_round_is_counted_and_a_draft_is_not()
    {
        /*
         * The load-bearing pair. A draft is a supervisor's experiment that the next generation run
         * replaces wholesale, so counting one would put calls nobody committed to into the
         * denominator — a coverage figure that drops the moment somebody tries a what-if.
         *
         * Both plans are real and generated identically; the only difference is that one was
         * published. Asserting the same scope twice, before and after, is what stops this passing
         * because the calls were not there in the first place.
         */
        using var client = Admin();

        var draft = await RoundAsync(client, outletCount: 2);

        var beforePublishing = await CountAsync(draft.Outlets);
        Assert.Equal(0, beforePublishing.Total);

        await PublishAsync(client, draft.PlanId);

        var afterPublishing = await CountAsync(draft.Outlets);

        // Two shops, weekly, four weeks.
        Assert.Equal(8, afterPublishing.Total);
        Assert.Equal(8, afterPublishing.Planned);
        Assert.Equal(0, afterPublishing.NotVisited);
    }

    [Fact]
    public async Task A_call_the_rep_could_not_make_stays_in_the_denominator()
    {
        /*
         * `BR-JRN-2` refuses to let a rep delete a call precisely so that a skipped shop cannot
         * vanish from the promise. If `NotVisited` left `Total`, coverage would measure what was
         * left on the plan rather than what was planned — and a rep who marked everything shut
         * would score 100%.
         *
         * It is reported separately because "80% covered, eight shops shut" and "80% covered, eight
         * shops missed" are different weeks that the ratio alone cannot tell apart.
         */
        using var client = Admin();

        var round = await RoundAsync(client, outletCount: 1);
        await PublishAsync(client, round.PlanId);

        var call = (await CallsAsync(client, round.PlanId))[0];

        var marked = await client.PostAsJsonAsync(
            $"/api/journey/plans/{round.PlanId}/visits/{call}/not-visited",
            new { reason = "Shop shut for refurbishment" });

        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);

        var counts = await CountAsync(round.Outlets);

        Assert.Equal(4, counts.Total);
        Assert.Equal(1, counts.NotVisited);
        Assert.Equal(3, counts.Planned);
    }

    [Fact]
    public async Task The_window_takes_both_its_ends_and_leaves_the_days_outside()
    {
        // The same off-by-one that would lose the last day of a supervisor's month, asserted on the
        // side where the date is a plain `DateOnly` rather than an instant. Narrowing to the first
        // week must drop the other three, and widening must find them again.
        using var client = Admin();

        var round = await RoundAsync(client, outletCount: 1);
        await PublishAsync(client, round.PlanId);

        var whole = await CountAsync(round.Outlets);
        Assert.Equal(4, whole.Total);

        // Exactly the first call's day, at both ends.
        var firstDayOnly = await CountAsync(round.Outlets, From, From);
        Assert.Equal(1, firstDayOnly.Total);

        // …and the day before it holds nothing, so the one above was found by the window rather
        // than by matching everything.
        var dayBefore = await CountAsync(round.Outlets, From.AddDays(-1), From.AddDays(-1));
        Assert.Equal(0, dayBefore.Total);

        // The last call sits on the closing day of the window; a half-open `<` would lose it.
        var lastDayOnly = await CountAsync(round.Outlets, To.AddDays(-7), To);
        Assert.Equal(1, lastDayOnly.Total);
    }

    [Fact]
    public async Task Another_rounds_shops_are_not_this_ones()
    {
        // Scoping, and the mirror that keeps it honest: the shops left out of the ask have calls of
        // their own, so a query that ignored `outletIds` would come back with eight rather than four.
        using var client = Admin();

        var round = await RoundAsync(client, outletCount: 2);
        await PublishAsync(client, round.PlanId);

        var both = await CountAsync(round.Outlets);
        Assert.Equal(8, both.Total);

        var one = await CountAsync([round.Outlets[0]]);
        Assert.Equal(4, one.Total);

        var none = await CountAsync([]);
        Assert.Equal(0, none.Total);
    }

    /// <summary>Asks Journey, in a tenant context matching the admin token.</summary>
    private Task<PlannedCallCounts> CountAsync(
        IReadOnlyCollection<Guid> outletIds, DateOnly? from = null, DateOnly? to = null) =>
        AsTenant.RunAsync(fixture, fixture.AdminAccessToken, services => services
            .GetRequiredService<IJourneyQuery>()
            .CountPlannedAsync(outletIds, from ?? From, to ?? To));

    private sealed record Round(Guid PlanId, IReadOnlyList<Guid> Outlets);

    /// <summary>
    /// A rep with <paramref name="outletCount"/> shops on a weekly frequency, and a generated
    /// four-week plan — still a draft.
    /// </summary>
    private async Task<Round> RoundAsync(HttpClient client, int outletCount)
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));
        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var segment = Unique("SEG");
        var outletIds = new List<Guid>();

        for (var index = 0; index < outletCount; index++)
        {
            var created = await client.PostAsJsonAsync(
                "/api/outlets",
                new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, segment));

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            outletIds.Add((await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id);
        }

        var unit = await client.PostAsJsonAsync("/api/org/units", new OrgUnitRequest(Unique("Unit")));
        var unitId = (await unit.Content.ReadFromJsonAsync<OrgUnitResponse>())!.Id;

        var territory = await client.PostAsJsonAsync(
            "/api/org/territories", new TerritoryRequest(Unique("Terr"), unitId));
        var territoryId = (await territory.Content.ReadFromJsonAsync<TerritoryResponse>())!.Id;

        await client.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/outlets", new AssignOutletsRequest(outletIds));

        var rep = await RepAsync(client);

        var assigned = await client.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/assignments",
            new RepAssignmentRequest(rep, From, To));

        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);

        // Once a week, on Mondays only — so a four-week window generates exactly four calls per
        // shop, one of which lands on `From` and one on the last Monday. Every number in this file
        // is read off that, which is why the calendar is a single day rather than three.
        await client.PutAsJsonAsync(
            $"/api/journey/frequencies/segments/{segment}", new FrequencyRequest(1, 7));

        await client.PutAsJsonAsync(
            $"/api/journey/calendars/{rep}", new WorkingCalendarRequest([DayOfWeek.Monday], 10));

        var generated = await client.PostAsJsonAsync(
            "/api/journey/plans", new GeneratePlanRequest(rep, From, To));

        Assert.True(
            generated.StatusCode == HttpStatusCode.Created,
            $"{generated.StatusCode}: {await generated.Content.ReadAsStringAsync()}");

        var body = await generated.Content.ReadFromJsonAsync<JsonElement>();

        return new Round(
            body.GetProperty("plan").GetProperty("id").GetGuid(),
            outletIds);
    }

    private static async Task PublishAsync(HttpClient client, Guid planId)
    {
        var published = await client.PostAsync($"/api/journey/plans/{planId}/publish", null);

        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
    }

    /// <summary>The ids of the calls on a plan, in date order.</summary>
    private static async Task<IReadOnlyList<Guid>> CallsAsync(HttpClient client, Guid planId)
    {
        var plan = await client.GetFromJsonAsync<JsonElement>($"/api/journey/plans/{planId}");

        return [.. plan.GetProperty("visits").EnumerateArray()
            .OrderBy(visit => visit.GetProperty("date").GetDateTime())
            .Select(visit => visit.GetProperty("id").GetGuid())];
    }

    private async Task<string> RepAsync(HttpClient client)
    {
        var subjectId = Guid.NewGuid().ToString();
        var roles = await client.GetFromJsonAsync<List<RoleResponse>>("/api/iam/roles");

        var created = await client.PostAsJsonAsync("/api/iam/users", new
        {
            subjectId,
            email = $"{Guid.NewGuid():N}@fieldkit.local",
            displayName = "Fixture Rep",
            locale = "en-GB",
            timeZone = Zone,
            roleIds = new[] { roles!.First(role => role.IsSystemTemplate).Id },
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return subjectId;
    }
}
