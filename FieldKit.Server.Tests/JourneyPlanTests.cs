using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.Infrastructure.Outbox;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Journey;
using FieldKit.Modules.Org;
using FieldKit.Modules.Outlets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// Generating, publishing and reading back a rep's plan (<c>JRN-04</c>) — W7 slice 4.
/// </summary>
/// <remarks>
/// <para>
/// The rules of generation are pinned by <see cref="JourneyGenerationTests"/> against the pure
/// function, not here. What these cover is everything that cannot reach: that the right inputs are
/// gathered from three modules, that a draft is not a rep's work until it is published, that
/// publishing is announced once, and that tenants stay apart.
/// </para>
/// <para>
/// The fixture is the expensive part — a rep, a territory holding outlets, frequencies and a
/// calendar are all needed before a plan can contain anything at all, which is itself the shape of
/// the feature: four things have to be configured before a supervisor gets a plan.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class JourneyPlanTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    /// <summary>A Monday, and a four-week window — a whole number of weekly cycles.</summary>
    private static readonly DateOnly From = new(2027, 3, 1);
    private static readonly DateOnly To = new(2027, 3, 28);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

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

    private async Task<Guid> OutletAsync(HttpClient client, Guid channelId, string segment)
    {
        var created = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, segment));

        return (await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    /// <summary>
    /// A rep who covers <paramref name="outletCount"/> outlets, works Mondays, and has a frequency.
    /// </summary>
    /// <remarks>
    /// Everything a plan needs, assembled the way an admin would: a territory holding the shops, an
    /// assignment putting the rep on it, a segment frequency, and a working calendar.
    /// </remarks>
    private async Task<(string Rep, List<Guid> Outlets, string Segment)> ScenarioAsync(
        HttpClient client, int outletCount, int visitsPerCycle = 1, int cycleDays = 7)
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));
        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var segment = Unique("SEG");
        var outletIds = new List<Guid>();

        for (var index = 0; index < outletCount; index++)
        {
            outletIds.Add(await OutletAsync(client, channelId, segment));
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

        await client.PutAsJsonAsync(
            $"/api/journey/frequencies/segments/{segment}",
            new FrequencyRequest(visitsPerCycle, cycleDays));

        await client.PutAsJsonAsync(
            $"/api/journey/calendars/{rep}",
            new WorkingCalendarRequest([DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday], 10));

        return (rep, outletIds, segment);
    }

    private static async Task<JsonElement> GenerateAsync(
        HttpClient client, string rep, DateOnly? from = null, DateOnly? to = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/journey/plans", new GeneratePlanRequest(rep, from ?? From, to ?? To));

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task A_plan_holds_the_calls_the_reps_territory_and_frequency_add_up_to()
    {
        // Three outlets, weekly, four weeks: twelve calls. Every input comes from a different place —
        // the territory from Organization, the shops from Outlets, the frequency and calendar from
        // here — which is the thing this test covers and the pure generator's tests cannot.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var (rep, outlets, _) = await ScenarioAsync(client, outletCount: 3);
        var generated = await GenerateAsync(client, rep);

        Assert.Equal(12, generated.GetProperty("plan").GetProperty("visitCount").GetInt32());
        Assert.Equal("Draft", generated.GetProperty("plan").GetProperty("status").GetString());
        Assert.Empty(generated.GetProperty("shortfalls").EnumerateArray());

        var planned = generated.GetProperty("visits").EnumerateArray()
            .Select(visit => visit.GetProperty("outletId").GetGuid())
            .ToHashSet();

        Assert.Equal(outlets.ToHashSet(), planned);
    }

    [Fact]
    public async Task A_generated_plan_is_a_draft_until_somebody_publishes_it()
    {
        // The whole point of the slice. Generation is cheap and repeatable, so a generated plan is
        // an experiment — not something a device should be downloading.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var (rep, _, _) = await ScenarioAsync(client, outletCount: 1);
        var generated = await GenerateAsync(client, rep);
        var planId = generated.GetProperty("plan").GetProperty("id").GetGuid();

        var published = await client.PostAsync($"/api/journey/plans/{planId}/publish", null);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);

        var after = (await published.Content.ReadFromJsonAsync<JourneyPlanResponse>())!;

        Assert.Equal("Published", after.Status);
        Assert.NotNull(after.PublishedAtUtc);
    }

    [Fact]
    public async Task Publishing_twice_is_refused_rather_than_quietly_doing_nothing()
    {
        // A second publish is either a double-click or somebody expecting it to re-announce a
        // changed plan. The second is a misunderstanding worth correcting: a published plan does not
        // change, because the rep may already have walked half of it.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var (rep, _, _) = await ScenarioAsync(client, outletCount: 1);
        var planId = (await GenerateAsync(client, rep)).GetProperty("plan").GetProperty("id").GetGuid();

        await client.PostAsync($"/api/journey/plans/{planId}/publish", null);
        var again = await client.PostAsync($"/api/journey/plans/{planId}/publish", null);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var problems = await again.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains(
            problems.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("code").GetString() == "journey.plan.alreadyPublished");
    }

    [Fact]
    public async Task Publishing_announces_the_plan_exactly_once()
    {
        // JourneyPublished goes to Sync, which does not exist yet (W8) — the same shape
        // PriceListPublished had. An event is true whether or not anything is listening, and the
        // outbox row is what proves it was raised.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var (rep, _, _) = await ScenarioAsync(client, outletCount: 1);
        var planId = (await GenerateAsync(client, rep)).GetProperty("plan").GetProperty("id").GetGuid();

        Assert.Equal(0, await OutboxCountAsync(planId));

        await client.PostAsync($"/api/journey/plans/{planId}/publish", null);
        Assert.Equal(1, await OutboxCountAsync(planId));

        // The refused second publish must not raise a second one.
        await client.PostAsync($"/api/journey/plans/{planId}/publish", null);
        Assert.Equal(1, await OutboxCountAsync(planId));
    }

    [Fact]
    public async Task The_announcement_carries_how_big_the_plan_is()
    {
        // `VisitCount` is what lets Sync decide whether to pull, so a confident zero would be worse
        // than no number at all. It is also the field most likely to be quietly wrong: the count
        // comes from a child collection, and the publish endpoint has to have loaded it.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var (rep, _, _) = await ScenarioAsync(client, outletCount: 2);
        var generated = await GenerateAsync(client, rep);
        var planId = generated.GetProperty("plan").GetProperty("id").GetGuid();
        var expected = generated.GetProperty("plan").GetProperty("visitCount").GetInt32();

        Assert.Equal(8, expected);

        await client.PostAsync($"/api/journey/plans/{planId}/publish", null);

        var announced = await AnnouncedVisitCountAsync(planId);

        Assert.Equal(expected, announced);
    }

    [Fact]
    public async Task A_closed_outlet_is_reported_as_excluded_rather_than_planned()
    {
        // BR-JRN-5 reaching all the way through: the fact comes from Outlets, the rule from the pure
        // generator, and the answer to a supervisor from this response.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var (rep, outlets, _) = await ScenarioAsync(client, outletCount: 2);

        var closed = await client.PostAsJsonAsync(
            $"/api/outlets/{outlets[0]}/status",
            new OutletStatusRequest(OutletStatus.Closed, "Lease ended"));
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        var generated = await GenerateAsync(client, rep);

        var excluded = Assert.Single(generated.GetProperty("excluded").EnumerateArray());

        Assert.Equal(outlets[0], excluded.GetProperty("outletId").GetGuid());
        Assert.Equal("Closed", excluded.GetProperty("reason").GetString());

        Assert.DoesNotContain(
            generated.GetProperty("visits").EnumerateArray(),
            visit => visit.GetProperty("outletId").GetGuid() == outlets[0]);
    }

    [Fact]
    public async Task A_shortfall_survives_publication_and_an_exclusion_does_not()
    {
        // The storage split, and the reasoning behind it. A shortfall is a statement about *this*
        // plan against the capacity it had, and nothing else records it. An exclusion is recoverable
        // at any time from the screens that own it.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        // Four calls a week each, three outlets, one working day a week that holds two.
        var (rep, _, _) = await ScenarioAsync(client, outletCount: 3, visitsPerCycle: 4);

        await client.PutAsJsonAsync(
            $"/api/journey/calendars/{rep}", new WorkingCalendarRequest([DayOfWeek.Monday], 2));

        var generated = await GenerateAsync(client, rep);
        var planId = generated.GetProperty("plan").GetProperty("id").GetGuid();

        Assert.NotEmpty(generated.GetProperty("shortfalls").EnumerateArray());

        await client.PostAsync($"/api/journey/plans/{planId}/publish", null);

        var stored = await client.GetFromJsonAsync<JourneyPlanDetailResponse>(
            $"/api/journey/plans/{planId}");

        Assert.NotEmpty(stored!.Shortfalls);
        Assert.All(stored.Shortfalls, shortfall => Assert.True(shortfall.Planned < shortfall.Required));
    }

    [Fact]
    public async Task A_rep_who_covers_nothing_gets_an_empty_plan_rather_than_a_failure()
    {
        // A rep between assignments. Empty is the honest answer — an error would say something is
        // broken, and nothing is.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var rep = await RepAsync(client);
        var generated = await GenerateAsync(client, rep);

        Assert.Equal(0, generated.GetProperty("plan").GetProperty("visitCount").GetInt32());
        Assert.Empty(generated.GetProperty("shortfalls").EnumerateArray());
    }

    [Fact]
    public async Task A_plan_for_somebody_this_tenant_does_not_have_is_refused()
    {
        // Otherwise it produces an empty plan, which reads as "this rep covers nothing" — a coverage
        // problem rather than a typo, and the two are fixed in different places.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await client.PostAsJsonAsync(
            "/api/journey/plans", new GeneratePlanRequest(Guid.NewGuid().ToString(), From, To));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problems = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains(
            problems.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("code").GetString() == "journey.plan.unknownUser");
    }

    [Theory]
    [InlineData("2027-03-28", "2027-03-01", "journey.plan.windowBackwards")]
    [InlineData("2027-03-01", "2030-03-01", "journey.plan.windowTooLong")]
    public async Task A_window_nobody_should_plan_is_refused(string from, string to, string code)
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var rep = await RepAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/journey/plans",
            new GeneratePlanRequest(rep, DateOnly.Parse(from), DateOnly.Parse(to)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problems = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains(
            problems.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("code").GetString() == code);
    }

    [Fact]
    public async Task One_tenants_plans_are_invisible_to_another()
    {
        using var tenantA = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var (rep, _, _) = await ScenarioAsync(tenantA, outletCount: 1);
        var planId = (await GenerateAsync(tenantA, rep)).GetProperty("plan").GetProperty("id").GetGuid();

        var seenByB = await tenantB.GetAsync($"/api/journey/plans/{planId}");
        Assert.Equal(HttpStatusCode.NotFound, seenByB.StatusCode);

        // …and B cannot publish it either, which is the one that would have had a side effect.
        var publishedByB = await tenantB.PostAsync($"/api/journey/plans/{planId}/publish", null);
        Assert.Equal(HttpStatusCode.NotFound, publishedByB.StatusCode);
    }

    [Fact]
    public async Task Offers_no_way_to_generate_or_publish_to_a_caller_who_may_only_read()
    {
        using var reader = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        var listed = await reader.GetAsync("/api/journey/plans");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        var attempted = await reader.PostAsJsonAsync(
            "/api/journey/plans", new GeneratePlanRequest(Guid.NewGuid().ToString(), From, To));

        Assert.Equal(HttpStatusCode.Forbidden, attempted.StatusCode);
    }

    /// <summary>How many <c>JourneyPublished</c> messages the outbox holds for this plan.</summary>
    private async Task<int> OutboxCountAsync(Guid planId)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JourneyDbContext>();

        // Filtered by type in the database and by payload in memory: Content is jsonb, and Postgres
        // has no LIKE for it. The same shape UserAdministrationTests uses, and for the same reason.
        var payloads = await db.Set<OutboxMessage>()
            .Where(message => message.Type.Contains(nameof(JourneyPublished)))
            .Select(message => message.Content)
            .ToListAsync();

        return payloads.Count(content => content.Contains(planId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The <c>VisitCount</c> the outbox message for this plan carries.</summary>
    private async Task<int> AnnouncedVisitCountAsync(Guid planId)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JourneyDbContext>();

        var payloads = await db.Set<OutboxMessage>()
            .Where(message => message.Type.Contains(nameof(JourneyPublished)))
            .Select(message => message.Content)
            .ToListAsync();

        var mine = payloads.Single(content =>
            content.Contains(planId.ToString(), StringComparison.OrdinalIgnoreCase));

        return JsonDocument.Parse(mine).RootElement.GetProperty("VisitCount").GetInt32();
    }
}
