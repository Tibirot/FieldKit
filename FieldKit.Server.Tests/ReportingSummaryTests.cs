using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.Modules.Audit.Contracts;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Journey;
using FieldKit.Modules.Org;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Visit;
using FieldKit.Modules.Visit.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// The dashboard's read (<c>AUD-09</c>, <c>JRN-04</c>, <c>ORD-09</c>, <c>VIS-10</c>) — W12 slice 3.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is asserted here is the composition, not the arithmetic.</b> Each of the four aggregates
/// has its own file and its own sabotage; repeating their sums here would make one change fail in two
/// places. What only this level can show is that all four answered about <i>the same shops and the
/// same days</i>, that the scope was resolved once, and that coverage — the one figure neither module
/// can produce — comes out right.
/// </para>
/// <para>
/// The territory holds two shops and the window holds a month, with work deliberately placed just
/// outside both, so a scope or window that leaked would change a number rather than merely fail to.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class ReportingSummaryTests(ServerFixture fixture)
{
    private const string Summary = "/api/reporting/summary";
    private const string Zone = "Europe/Bucharest";

    private static readonly Coordinates Shop = new(44.4638, 26.0946);

    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 30);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    [Fact]
    public async Task Four_modules_answer_about_the_same_shops_and_the_same_days()
    {
        /*
         * The whole point of the composition, in one request.
         *
         * Two shops in one territory; a third in a territory of its own carrying work that must not
         * appear. Visits, audits and orders inside the window, and one of each a day outside it.
         * Every number below is therefore wrong in a *different* way if the scope leaks than if the
         * window does.
         */
        using var client = Admin();

        var mine = await TerritoryAsync(client);
        var first = await OutletAsync(client, mine);
        var second = await OutletAsync(client, mine);

        var elsewhere = await TerritoryAsync(client);
        var theirs = await OutletAsync(client, elsewhere);

        await VisitedAsync(client, first, new DateOnly(2026, 9, 3), VisitOutcome.Productive, audited: true);
        await VisitedAsync(client, first, new DateOnly(2026, 9, 10), VisitOutcome.NonProductive);
        await VisitedAsync(client, second, new DateOnly(2026, 9, 17), VisitOutcome.Productive, audited: true);

        // The two that must not be counted, each a mirror of one that is.
        await VisitedAsync(client, theirs, new DateOnly(2026, 9, 11), VisitOutcome.Productive, audited: true);
        await VisitedAsync(client, first, To.AddDays(1), VisitOutcome.Productive, audited: true);

        var summary = await SummaryAsync(client, territoryId: mine);

        Assert.Equal(2, summary.GetProperty("outlets").GetInt32());

        var visits = summary.GetProperty("visits");

        Assert.Equal(2, visits.GetProperty("productive").GetInt32());
        Assert.Equal(1, visits.GetProperty("nonProductive").GetInt32());

        // A percentage, not a fraction: two of three finished visits were productive.
        Assert.Equal(66.67m, visits.GetProperty("strikeRate").GetDecimal());

        // Audit and Order answered about the same three visits, not about the five that exist.
        Assert.Equal(2, summary.GetProperty("perfectStore").GetProperty("audits").GetInt32());

        // …and the shop in the other territory is real, so the scope is doing work.
        var everything = await SummaryAsync(client, territoryId: null);

        Assert.True(everything.GetProperty("outlets").GetInt32() >= 3);
        Assert.True(
            everything.GetProperty("visits").GetProperty("productive").GetInt32()
            > visits.GetProperty("productive").GetInt32());
    }

    [Fact]
    public async Task Coverage_is_the_one_figure_neither_module_could_produce()
    {
        /*
         * Journey knows what was promised and never learns a call was made; Visit knows which calls
         * its visits claimed and knows nothing about the round. The division happens here, and this
         * is the only place it can be tested end to end.
         *
         * Four planned calls, one of them declined by the rep and one of them actually visited: 25%
         * of the promise kept, with the declined call still in the denominator — `BR-JRN-2` keeps a
         * skipped shop on the round precisely so coverage cannot be improved by giving up.
         */
        using var client = Admin();

        var round = await RoundAsync(client);

        var coverage = (await SummaryAsync(client, territoryId: round.TerritoryId))
            .GetProperty("coverage");

        Assert.Equal(4, coverage.GetProperty("planned").GetInt32());
        Assert.Equal(1, coverage.GetProperty("notVisited").GetInt32());
        Assert.Equal(1, coverage.GetProperty("made").GetInt32());
        Assert.Equal(25.00m, coverage.GetProperty("percentage").GetDecimal());
    }

    [Fact]
    public async Task A_scope_with_no_round_has_no_coverage_rather_than_none_kept()
    {
        // Null, not zero. A territory nobody has planned for has not failed to visit anybody, and a
        // dashboard reading 0% would send a supervisor after a team that was never given a round.
        using var client = Admin();

        var territoryId = await TerritoryAsync(client);
        await OutletAsync(client, territoryId);

        var summary = await SummaryAsync(client, territoryId);

        Assert.Equal(1, summary.GetProperty("outlets").GetInt32());

        var coverage = summary.GetProperty("coverage");

        Assert.Equal(0, coverage.GetProperty("planned").GetInt32());
        Assert.Equal(JsonValueKind.Null, coverage.GetProperty("percentage").ValueKind);

        // The same distinction on the visit side, from the same empty scope.
        Assert.Equal(
            JsonValueKind.Null,
            summary.GetProperty("visits").GetProperty("strikeRate").ValueKind);
    }

    [Fact]
    public async Task A_territory_this_tenant_does_not_have_totals_nothing()
    {
        /*
         * An unknown id answers an empty scope rather than a 404, so the endpoint cannot be used to
         * discover whether somebody else's territory id is real. Tenant B's territory is a *real*
         * id with real shops behind it, which is what makes this stronger than passing a random
         * Guid — and the zero has to come from the tenant filter rather than from the id not
         * existing.
         */
        using var theirs = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var theirTerritory = await TerritoryAsync(theirs);
        await OutletAsync(theirs, theirTerritory);

        using var client = Admin();

        var response = await client.GetAsync($"{Summary}?territoryId={theirTerritory}&from={From:O}&to={To:O}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var summary = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, summary.GetProperty("outlets").GetInt32());
        Assert.Equal(0, summary.GetProperty("visits").GetProperty("productive").GetInt32());
    }

    [Fact]
    public async Task A_period_that_ends_before_it_starts_is_refused()
    {
        using var client = Admin();

        var backwards = await client.GetAsync($"{Summary}?from={To:O}&to={From:O}");

        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);

        // And a window longer than the guard. Asserted because the alternative — an unbounded
        // period — makes the cost of one request a function of how long the tenant has existed.
        var decade = await client.GetAsync($"{Summary}?from=2020-01-01&to={To:O}");

        Assert.Equal(HttpStatusCode.BadRequest, decade.StatusCode);

        // The boundary itself is allowed, so the guard refuses "too long" rather than "long".
        var year = await client.GetAsync($"{Summary}?from={To.AddDays(-365):O}&to={To:O}");

        Assert.Equal(HttpStatusCode.OK, year.StatusCode);
    }

    [Fact]
    public async Task Reading_a_territorys_numbers_needs_both_read_permissions()
    {
        /*
         * `visit:read` already covers audits and orders — neither declares a read permission of its
         * own — and coverage's denominator is Journey's, so the endpoint asks for both.
         *
         * What is *not* covered: a caller holding exactly one of the two. No realm user has that
         * shape, and inventing one is a realm change, which the W10 finding says is not applied by
         * deploying. Recorded rather than skipped silently.
         */
        using var rep = fixture.CreateAuthenticatedClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await rep.GetAsync(Summary)).StatusCode);

        // The fixture's bare client, not `CreateAuthenticatedClient(null)` — that overload defaults
        // to the rep's token, which would have made this a second 403 wearing a 401's name.
        Assert.Equal(
            HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync(Summary)).StatusCode);

        // The viewer holds both and is not an administrator, so this is the permission answering
        // rather than the role.
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Summary)).StatusCode);
    }

    private async Task<JsonElement> SummaryAsync(HttpClient client, Guid? territoryId)
    {
        var scope = territoryId is { } id ? $"&territoryId={id}" : string.Empty;

        var response = await client.GetAsync($"{Summary}?from={From:O}&to={To:O}{scope}");

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private sealed record Round(Guid TerritoryId, Guid OutletId);

    /// <summary>
    /// A published four-call round at one shop, with one call declined and one visited.
    /// </summary>
    private async Task<Round> RoundAsync(HttpClient client)
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));
        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var segment = Unique("SEG");

        var created = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, segment, Location: Shop));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var outletId = (await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id;

        var territoryId = await TerritoryAsync(client);

        await client.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/outlets", new AssignOutletsRequest([outletId]));

        var rep = await RepAsync(client);

        var assigned = await client.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/assignments",
            new RepAssignmentRequest(rep, From, To));

        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);

        // Weekly, Mondays only — four calls in the four-week window, one per Monday.
        await client.PutAsJsonAsync(
            $"/api/journey/frequencies/segments/{segment}", new FrequencyRequest(1, 7));

        await client.PutAsJsonAsync(
            $"/api/journey/calendars/{rep}", new WorkingCalendarRequest([DayOfWeek.Monday], 10));

        var generated = await client.PostAsJsonAsync(
            "/api/journey/plans", new GeneratePlanRequest(rep, From, To));

        Assert.True(
            generated.StatusCode == HttpStatusCode.Created,
            $"{generated.StatusCode}: {await generated.Content.ReadAsStringAsync()}");

        var plan = await generated.Content.ReadFromJsonAsync<JsonElement>();
        var planId = plan.GetProperty("plan").GetProperty("id").GetGuid();

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsync($"/api/journey/plans/{planId}/publish", null)).StatusCode);

        var calls = plan.GetProperty("visits").EnumerateArray()
            .OrderBy(visit => visit.GetProperty("date").GetDateTime())
            .Select(visit => (Id: visit.GetProperty("id").GetGuid(), Date: visit.GetProperty("date").GetDateTime()))
            .ToList();

        Assert.Equal(4, calls.Count);

        // One declined, and one actually made — the numerator and the part of the denominator that
        // BR-JRN-2 refuses to let disappear.
        var declined = await client.PostAsJsonAsync(
            $"/api/journey/plans/{planId}/visits/{calls[0].Id}/not-visited",
            new { reason = "Shop shut for refurbishment" });

        Assert.Equal(HttpStatusCode.OK, declined.StatusCode);

        await VisitedAsync(
            client,
            outletId,
            DateOnly.FromDateTime(calls[1].Date),
            VisitOutcome.Productive,
            plannedVisitId: calls[1].Id);

        return new Round(territoryId, outletId);
    }

    /// <summary>A visit that happened, optionally claiming a planned call and carrying an audit.</summary>
    private async Task VisitedAsync(
        HttpClient client,
        Guid outletId,
        DateOnly on,
        VisitOutcome outcome,
        bool audited = false,
        Guid? plannedVisitId = null)
    {
        var checkedIn = new DateTimeOffset(on.ToDateTime(new TimeOnly(9, 30)), TimeSpan.Zero);
        var visitId = Guid.CreateVersion7();

        var captured = new CapturedVisit(
            VisitId: visitId,
            OutletId: outletId,
            PlannedVisitId: plannedVisitId,
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

        var applied = await AsTenant.RunAsync(fixture, fixture.AdminAccessToken, services => services
            .GetRequiredService<IVisitIngest>()
            .IngestAsync(captured, AsTenant.SubjectOf(fixture.AdminAccessToken)));

        Assert.Equal(VisitIngestRefusal.None, applied.Refusal);

        if (!audited) return;

        var weights = await WeightingAsync(client);

        var audit = new CapturedAudit(
            Guid.CreateVersion7(),
            visitId,
            checkedIn.AddMinutes(5),
            weights,
            CategoryFacings: 40,
            Availability: [new CapturedAvailability(Guid.CreateVersion7(), AvailabilityStatus.Present)],
            Facings: [new CapturedFacings(Guid.CreateVersion7(), 30)],
            Prices: [new CapturedPrice(Guid.CreateVersion7(), 1099, 1099, "RON")]);

        var stored = await AsTenant.RunAsync(fixture, fixture.AdminAccessToken, services => services
            .GetRequiredService<IAuditIngest>()
            .IngestAsync(audit, AsTenant.SubjectOf(fixture.AdminAccessToken)));

        Assert.Equal(AuditIngestRefusal.None, stored.Refusal);
    }

    private static async Task<int> WeightingAsync(HttpClient client)
    {
        var drafted = await client.PostAsJsonAsync("/api/config/score-weights", new ScoreWeightSetRequest([
            new ScoreWeightRequest(ScorePillar.Availability, 50m),
            new ScoreWeightRequest(ScorePillar.ShareOfShelf, 30m),
            new ScoreWeightRequest(ScorePillar.PriceCompliance, 20m),
        ]));

        Assert.Equal(HttpStatusCode.Created, drafted.StatusCode);

        var version = (await drafted.Content.ReadFromJsonAsync<ScoreWeightSetResponse>())!.Version;

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsync($"/api/config/score-weights/{version}/publish", null)).StatusCode);

        return version;
    }

    private static async Task<Guid> TerritoryAsync(HttpClient client)
    {
        var unit = await client.PostAsJsonAsync("/api/org/units", new OrgUnitRequest(Unique("Unit")));
        var unitId = (await unit.Content.ReadFromJsonAsync<OrgUnitResponse>())!.Id;

        var territory = await client.PostAsJsonAsync(
            "/api/org/territories", new TerritoryRequest(Unique("Terr"), unitId));

        Assert.Equal(HttpStatusCode.Created, territory.StatusCode);

        return (await territory.Content.ReadFromJsonAsync<TerritoryResponse>())!.Id;
    }

    private static async Task<Guid> OutletAsync(HttpClient client, Guid territoryId)
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));
        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var created = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, Location: Shop));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var outletId = (await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id;

        var assigned = await client.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/outlets", new AssignOutletsRequest([outletId]));

        Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);

        return outletId;
    }

    private static async Task<string> RepAsync(HttpClient client)
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
