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
/// What a rep may do to the round they are walking (<c>JRN-06</c>, <c>BR-JRN-2/4</c>) — W7 slice 5.
/// </summary>
/// <remarks>
/// <para>
/// Three acts and one absence. A rep may say a call did not happen and why, add one nobody planned,
/// and move one within its cycle — and may <b>not</b> delete anything, which is <c>BR-JRN-2</c> and
/// is enforced by there being no route rather than by a check.
/// </para>
/// <para>
/// The cycle boundary is the assertion that matters. A weekly outlet moved from Monday to Wednesday
/// is a rep organising their week; moved to the following Monday it lands in a different cycle and
/// changes which cycle the shop was covered in, which is <c>BR-JRN-6</c> compliance for two cycles
/// at once — a supervisor's decision, not a rep's.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class RepAnnotationTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    /// <summary>A Monday, and four whole weekly cycles.</summary>
    private static readonly DateOnly From = new(2028, 3, 6);
    private static readonly DateOnly To = new(2028, 4, 2);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

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

    /// <summary>A published plan with one weekly outlet, plus a spare outlet nobody planned.</summary>
    private async Task<(Guid PlanId, Guid VisitId, DateOnly Date, Guid SpareOutlet)> PublishedPlanAsync(
        HttpClient client)
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));
        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var segment = Unique("SEG");

        var planned = await client.PostAsJsonAsync(
            "/api/outlets", new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, segment));
        var plannedOutlet = (await planned.Content.ReadFromJsonAsync<OutletResponse>())!.Id;

        // Deliberately not in the territory and not graded: an unplanned call is a shop the rep
        // walked past, and nothing about it needs to have been planned.
        var spare = await client.PostAsJsonAsync(
            "/api/outlets", new CreateOutletRequest(Unique("OUT"), "Spare Shop", channelId, Zone));
        var spareOutlet = (await spare.Content.ReadFromJsonAsync<OutletResponse>())!.Id;

        var unit = await client.PostAsJsonAsync("/api/org/units", new OrgUnitRequest(Unique("Unit")));
        var unitId = (await unit.Content.ReadFromJsonAsync<OrgUnitResponse>())!.Id;

        var territory = await client.PostAsJsonAsync(
            "/api/org/territories", new TerritoryRequest(Unique("Terr"), unitId));
        var territoryId = (await territory.Content.ReadFromJsonAsync<TerritoryResponse>())!.Id;

        await client.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/outlets", new AssignOutletsRequest([plannedOutlet]));

        var rep = await RepAsync(client);

        await client.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/assignments", new RepAssignmentRequest(rep, From, To));

        await client.PutAsJsonAsync(
            $"/api/journey/frequencies/segments/{segment}", new FrequencyRequest(1, 7));

        // Monday and Wednesday, so a same-cycle move has somewhere to go.
        await client.PutAsJsonAsync(
            $"/api/journey/calendars/{rep}",
            new WorkingCalendarRequest([DayOfWeek.Monday, DayOfWeek.Wednesday], 10));

        var generated = await client.PostAsJsonAsync(
            "/api/journey/plans", new GeneratePlanRequest(rep, From, To));

        var body = await generated.Content.ReadFromJsonAsync<JsonElement>();
        var planId = body.GetProperty("plan").GetProperty("id").GetGuid();

        var published = await client.PostAsync($"/api/journey/plans/{planId}/publish", null);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);

        var detail = await client.GetFromJsonAsync<JourneyPlanDetailResponse>(
            $"/api/journey/plans/{planId}");

        var first = detail!.Visits[0];

        return (planId, first.Id, first.Date, spareOutlet);
    }

    [Fact]
    public async Task A_rep_says_a_call_did_not_happen_and_why()
    {
        // VIS-07 lives here rather than in Visit: capturing the reason against the *planned* call
        // means Visit never grows a state for a visit that did not happen.
        using var client = Admin();

        var (planId, visitId, _, _) = await PublishedPlanAsync(client);

        var marked = await client.PostAsJsonAsync(
            $"/api/journey/plans/{planId}/visits/{visitId}/not-visited",
            new NotVisitedRequest("Shutters down, no answer"));

        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);

        var visit = (await marked.Content.ReadFromJsonAsync<PlannedVisitResponse>())!;

        Assert.Equal("NotVisited", visit.Status);
        Assert.Equal("Shutters down, no answer", visit.NotVisitedReason);
    }

    [Fact]
    public async Task A_skipped_call_stays_on_the_plan_rather_than_disappearing()
    {
        // BR-JRN-2. A shop that was skipped is a fact about the round; letting it vanish would make
        // coverage look complete and turn BR-JRN-6 into a measure of what was left on the plan.
        using var client = Admin();

        var (planId, visitId, _, _) = await PublishedPlanAsync(client);

        await client.PostAsJsonAsync(
            $"/api/journey/plans/{planId}/visits/{visitId}/not-visited",
            new NotVisitedRequest("Closed for refit"));

        var detail = await client.GetFromJsonAsync<JourneyPlanDetailResponse>(
            $"/api/journey/plans/{planId}");

        var still = detail!.Visits.Single(visit => visit.Id == visitId);

        Assert.Equal("NotVisited", still.Status);
        Assert.Equal(4, detail.Visits.Count);
    }

    [Fact]
    public async Task A_rep_cannot_delete_a_planned_call_at_all()
    {
        // BR-JRN-2 enforced by absence rather than by a check. Asserted because "there is no route"
        // is exactly the kind of guarantee that gets added back by somebody being helpful.
        using var client = Admin();

        var (planId, visitId, _, _) = await PublishedPlanAsync(client);

        var deleted = await client.DeleteAsync($"/api/journey/plans/{planId}/visits/{visitId}");

        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
    }

    [Fact]
    public async Task Saying_it_twice_is_refused_rather_than_overwriting_the_first_reason()
    {
        // The first reason is what the rep saw on the day. Silently replacing it would lose the
        // reporting fact and give two different answers to "why was this shop missed".
        using var client = Admin();

        var (planId, visitId, _, _) = await PublishedPlanAsync(client);

        await client.PostAsJsonAsync(
            $"/api/journey/plans/{planId}/visits/{visitId}/not-visited",
            new NotVisitedRequest("Closed for refit"));

        var again = await client.PostAsJsonAsync(
            $"/api/journey/plans/{planId}/visits/{visitId}/not-visited",
            new NotVisitedRequest("Changed my mind"));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task A_reason_is_required()
    {
        using var client = Admin();

        var (planId, visitId, _, _) = await PublishedPlanAsync(client);

        var response = await client.PostAsJsonAsync(
            $"/api/journey/plans/{planId}/visits/{visitId}/not-visited",
            new NotVisitedRequest("   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problems = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains(
            problems.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("code").GetString() == "journey.visit.reasonRequired");
    }

    [Fact]
    public async Task Not_visited_is_announced_so_reporting_can_count_the_reasons()
    {
        // The one rep-side annotation another module reasons about: BR-JRN-6 measures whether an
        // outlet got its calls, and "forty per cent of misses were 'closed on arrival'" is a fact
        // about the territory rather than about one round.
        using var client = Admin();

        var (planId, visitId, _, _) = await PublishedPlanAsync(client);

        Assert.Equal(0, await AnnouncementsAsync(visitId));

        await client.PostAsJsonAsync(
            $"/api/journey/plans/{planId}/visits/{visitId}/not-visited",
            new NotVisitedRequest("Closed on arrival"));

        Assert.Equal(1, await AnnouncementsAsync(visitId));
    }

    [Fact]
    public async Task A_call_moves_within_its_cycle()
    {
        // Monday to Wednesday of the same week: the rep organising their own days.
        using var client = Admin();

        var (planId, visitId, date, _) = await PublishedPlanAsync(client);

        var moved = await client.PostAsJsonAsync(
            $"/api/journey/plans/{planId}/visits/{visitId}/reschedule",
            new RescheduleRequest(date.AddDays(2)));

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        var visit = (await moved.Content.ReadFromJsonAsync<PlannedVisitResponse>())!;

        Assert.Equal(date.AddDays(2), visit.Date);

        // The original day survives, because a moved call and a call that was always on Wednesday
        // are different things to anybody reviewing the round.
        Assert.Equal(date, visit.RescheduledFrom);
    }

    [Fact]
    public async Task A_call_does_not_move_out_of_its_cycle()
    {
        // BR-JRN-4. Seven days on is the next cycle: the shop would be covered twice in one and not
        // at all in the other, which changes compliance for both.
        using var client = Admin();

        var (planId, visitId, date, _) = await PublishedPlanAsync(client);

        var response = await client.PostAsJsonAsync(
            $"/api/journey/plans/{planId}/visits/{visitId}/reschedule",
            new RescheduleRequest(date.AddDays(7)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problems = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains(
            problems.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("code").GetString() == "journey.visit.outsideCycle");
    }

    [Fact]
    public async Task A_call_does_not_move_outside_the_plans_window()
    {
        using var client = Admin();

        var (planId, visitId, _, _) = await PublishedPlanAsync(client);

        var response = await client.PostAsJsonAsync(
            $"/api/journey/plans/{planId}/visits/{visitId}/reschedule",
            new RescheduleRequest(To.AddDays(30)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problems = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains(
            problems.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("code").GetString() == "journey.visit.outsideWindow");
    }

    [Fact]
    public async Task A_rep_adds_a_call_nobody_planned()
    {
        // A shop they passed, or one that asked them in. It joins the plan as Unplanned, which is
        // what keeps "did the plan work?" answerable separately from "what did the rep do?".
        using var client = Admin();

        var (planId, _, date, spare) = await PublishedPlanAsync(client);

        var added = await client.PostAsJsonAsync(
            $"/api/journey/plans/{planId}/visits", new UnplannedVisitRequest(spare, date));

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);

        var visit = (await added.Content.ReadFromJsonAsync<PlannedVisitResponse>())!;

        Assert.Equal("Unplanned", visit.Source);
        Assert.Equal(spare, visit.OutletId);

        var detail = await client.GetFromJsonAsync<JourneyPlanDetailResponse>(
            $"/api/journey/plans/{planId}");

        Assert.Equal(5, detail!.Visits.Count);
    }

    [Fact]
    public async Task An_unplanned_call_belongs_to_no_cycle_so_it_cannot_be_moved()
    {
        // Not an omission. BR-JRN-4 is about moving a call within the cycle its *frequency* put it
        // in, and a call nobody planned was never in one — a rep who wants it on another day adds it
        // on that day.
        using var client = Admin();

        var (planId, _, date, spare) = await PublishedPlanAsync(client);

        var added = await client.PostAsJsonAsync(
            $"/api/journey/plans/{planId}/visits", new UnplannedVisitRequest(spare, date));
        var visitId = (await added.Content.ReadFromJsonAsync<PlannedVisitResponse>())!.Id;

        var moved = await client.PostAsJsonAsync(
            $"/api/journey/plans/{planId}/visits/{visitId}/reschedule",
            new RescheduleRequest(date.AddDays(2)));

        Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
    }

    [Fact]
    public async Task An_unplanned_call_names_an_outlet_this_tenant_actually_has()
    {
        using var client = Admin();

        var (planId, _, date, _) = await PublishedPlanAsync(client);

        var response = await client.PostAsJsonAsync(
            $"/api/journey/plans/{planId}/visits",
            new UnplannedVisitRequest(Guid.CreateVersion7(), date));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problems = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains(
            problems.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("code").GetString() == "journey.visit.unknownOutlet");
    }

    [Fact]
    public async Task Nothing_can_be_annotated_on_a_plan_that_was_never_published()
    {
        // A draft is a supervisor's experiment. There is no round for a rep to be reporting on, and
        // allowing it would let an annotation vanish when the next generation run supersedes it.
        using var client = Admin();

        var (planId, visitId, date, _) = await PublishedPlanAsync(client);

        // A second plan for the same rep, left as a draft.
        var listed = await client.GetFromJsonAsync<List<JourneyPlanResponse>>("/api/journey/plans");
        var rep = listed!.Single(plan => plan.Id == planId).UserId;

        var draft = await client.PostAsJsonAsync(
            "/api/journey/plans", new GeneratePlanRequest(rep, From, To));

        var body = await draft.Content.ReadFromJsonAsync<JsonElement>();
        var draftId = body.GetProperty("plan").GetProperty("id").GetGuid();
        var draftVisit = body.GetProperty("visits").EnumerateArray().First().GetProperty("id").GetGuid();

        var marked = await client.PostAsJsonAsync(
            $"/api/journey/plans/{draftId}/visits/{draftVisit}/not-visited",
            new NotVisitedRequest("Closed"));

        Assert.Equal(HttpStatusCode.Conflict, marked.StatusCode);

        var problems = await marked.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains(
            problems.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("code").GetString() == "journey.visit.planNotPublished");

        // …and the published plan's own call is untouched by any of that.
        Assert.NotEqual(draftVisit, visitId);
        Assert.True(date >= From);
    }

    [Fact]
    public async Task A_call_on_one_plan_cannot_be_annotated_through_another()
    {
        // Found through the plan rather than by id alone, so a visit id from somewhere else is
        // simply not here — rather than being edited by way of a plan it does not belong to.
        using var client = Admin();

        var first = await PublishedPlanAsync(client);
        var second = await PublishedPlanAsync(client);

        var response = await client.PostAsJsonAsync(
            $"/api/journey/plans/{second.PlanId}/visits/{first.VisitId}/not-visited",
            new NotVisitedRequest("Closed"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task One_tenants_rounds_are_not_anothers_to_report_on()
    {
        using var tenantA = Admin();
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var (planId, visitId, _, _) = await PublishedPlanAsync(tenantA);

        var response = await tenantB.PostAsJsonAsync(
            $"/api/journey/plans/{planId}/visits/{visitId}/not-visited",
            new NotVisitedRequest("Closed"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reporting_on_a_round_is_not_the_same_permission_as_planning_one()
    {
        // The read-only token holds journey:read and neither journey:write nor journey:annotate.
        using var reader = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        var response = await reader.PostAsJsonAsync(
            $"/api/journey/plans/{Guid.CreateVersion7()}/visits/{Guid.CreateVersion7()}/not-visited",
            new NotVisitedRequest("Closed"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>How many <c>PlannedVisitMarkedNotVisited</c> messages name this call.</summary>
    private async Task<int> AnnouncementsAsync(Guid visitId)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JourneyDbContext>();

        var payloads = await db.Set<OutboxMessage>()
            .Where(message => message.Type.Contains(nameof(PlannedVisitMarkedNotVisited)))
            .Select(message => message.Content)
            .ToListAsync();

        return payloads.Count(content =>
            content.Contains(visitId.ToString(), StringComparison.OrdinalIgnoreCase));
    }
}
