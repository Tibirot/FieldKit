using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.Infrastructure.Outbox;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Visit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// Ending a visit, and sealing it (<c>VIS-05</c>, <c>BR-VIS-3/4/5</c>) — W7 slice 9.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the end of the visit that says no</b>, and the contrast with check-in is the point.
/// <c>BR-VIS-2</c> refuses to keep a rep out of a shop; <c>BR-VIS-3</c> refuses to let a visit be
/// filed as done while the work it was configured for is not. Nothing is lost by refusing here —
/// the rep is still in the shop, still checked in, and the refusal names the steps.
/// </para>
/// <para>
/// After it, <c>BR-VIS-4</c>: the visit is sealed and every write path refuses. That is what makes
/// it safe to push through Sync with no conflict story (<c>B7</c>), so the assertions are about
/// routes that used to work and now do not.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class VisitCheckOutTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    private static readonly Coordinates Shop = new(44.4638, 26.0946);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    private static async Task<Guid> OutletAsync(HttpClient client, params VisitStepRequest[] steps)
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        if (steps.Length > 0)
        {
            await client.PutAsJsonAsync(
                $"/api/config/visit-workflows/{channelId}",
                new VisitWorkflowRequest(PresenceExpected: true, steps));
        }

        var created = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, Location: Shop));

        return (await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    private static async Task<VisitDetailResponse> CheckInAsync(HttpClient client, Guid outletId)
    {
        var response = await client.PostAsJsonAsync(
            "/api/visits/check-in", new CheckInRequest(outletId, Shop.Latitude, Shop.Longitude));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!;
    }

    private static Task<HttpResponseMessage> CheckOutAsync(
        HttpClient client,
        Guid visitId,
        VisitOutcome outcome = VisitOutcome.Productive,
        string? reason = null,
        double? latitude = null,
        double? longitude = null) =>
        client.PostAsJsonAsync(
            $"/api/visits/{visitId}/check-out",
            new CheckOutRequest(outcome, reason, latitude, longitude));

    [Fact]
    public async Task A_rep_finishes_and_the_visit_records_how_long_it_took()
    {
        using var client = Admin();

        var outletId = await OutletAsync(client);
        var detail = await CheckInAsync(client, outletId);

        var response = await CheckOutAsync(
            client, detail.Visit.Id, latitude: Shop.Latitude, longitude: Shop.Longitude);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var visit = (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;

        Assert.Equal("CheckedOut", visit.Status);
        Assert.Equal("Productive", visit.Outcome);
        Assert.NotNull(visit.CheckedOutAtUtc);
        Assert.Equal(Shop.Latitude, visit.CheckOutLatitude);

        // BR-VIS-5: check-out minus check-in, and nothing else. Derived rather than stored, so this
        // is really asserting that the two timestamps agree with the number.
        Assert.NotNull(visit.TimeOnSiteSeconds);
        Assert.Equal(
            (visit.CheckedOutAtUtc!.Value - visit.CheckedInAtUtc).TotalSeconds,
            visit.TimeOnSiteSeconds!.Value,
            3);
    }

    [Fact]
    public async Task A_visit_that_took_seconds_is_recorded_rather_than_questioned()
    {
        // BR-VIS-5 is explicit that an abnormally short visit is a reporting fact and never a block.
        // Asserted because "surely we should stop that" is exactly the instinct this rule refuses,
        // and the threshold that would decide "abnormal" is a Phase 3 reporting decision (VIS-10)
        // against a population this system does not have.
        using var client = Admin();

        var outletId = await OutletAsync(client);
        var detail = await CheckInAsync(client, outletId);

        var response = await CheckOutAsync(client, detail.Visit.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var visit = (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;

        Assert.InRange(visit.TimeOnSiteSeconds!.Value, 0, 60);
    }

    [Fact]
    public async Task A_mandatory_step_still_open_holds_the_door()
    {
        // BR-VIS-3, and the refusal names what is outstanding: the rep is still in the shop at this
        // point, and a list is the difference between finishing the job and walking back in for it.
        using var client = Admin();

        var outletId = await OutletAsync(
            client,
            new VisitStepRequest(VisitStepType.Audit, true, "Shelf check"),
            new VisitStepRequest(VisitStepType.Order, true, "Take the order"));

        var detail = await CheckInAsync(client, outletId);

        var refused = await CheckOutAsync(client, detail.Visit.Id);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var problem = Assert.Single(await Refusals.ProblemsOf(refused));

        Assert.Equal("visit.checkOut.mandatoryStepsOpen", problem.Code);
        Assert.Equal("Shelf check, Take the order", problem.Args!["steps"]);

        // And it is still open — a refused check-out changes nothing.
        var reread = await client.GetFromJsonAsync<VisitDetailResponse>(
            $"/api/visits/{detail.Visit.Id}");

        Assert.Equal("InProgress", reread!.Visit.Status);
    }

    [Fact]
    public async Task Doing_the_mandatory_work_opens_it_again()
    {
        // The other half of BR-VIS-3. The refusal above must be a formality once the work is done,
        // not a second thing to argue with.
        using var client = Admin();

        var outletId = await OutletAsync(
            client,
            new VisitStepRequest(VisitStepType.Audit, true, "Shelf check"),
            new VisitStepRequest(VisitStepType.Note, false, "Anything else"));

        var detail = await CheckInAsync(client, outletId);
        var mandatory = Assert.Single(detail.OpenMandatorySteps);

        await client.PostAsJsonAsync(
            $"/api/visits/{detail.Visit.Id}/steps/{mandatory.Id}/complete",
            new CompleteStepRequest());

        var response = await CheckOutAsync(client, detail.Visit.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The optional step is still pending, and that is allowed. BR-VIS-3 is about mandatory work.
        var after = (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!;

        Assert.Equal("Pending", after.Steps.Single(step => !step.Mandatory).Status);
    }

    [Fact]
    public async Task A_visit_that_came_to_nothing_says_why()
    {
        using var client = Admin();

        var outletId = await OutletAsync(client);
        var detail = await CheckInAsync(client, outletId);

        var refused = await CheckOutAsync(client, detail.Visit.Id, VisitOutcome.NonProductive);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(
            "visit.checkOut.reasonRequired",
            Assert.Single(await Refusals.ProblemsOf(refused)).Code);

        var accepted = await CheckOutAsync(
            client, detail.Visit.Id, VisitOutcome.NonProductive, "  Store closed for refit.  ");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var visit = (await accepted.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;

        Assert.Equal("NonProductive", visit.Outcome);
        Assert.Equal("Store closed for refit.", visit.OutcomeReason);
    }

    [Fact]
    public async Task A_reason_on_a_productive_visit_is_not_recorded()
    {
        // Same rule the geofence override reason follows: kept where it means something. "Why was
        // nothing sold" is the reporting fact, and a sentence attached to a productive call would
        // put noise in the column a supervisor reads that answer from.
        using var client = Admin();

        var outletId = await OutletAsync(client);
        var detail = await CheckInAsync(client, outletId);

        var response = await CheckOutAsync(
            client, detail.Visit.Id, VisitOutcome.Productive, "Sold three cases");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit.OutcomeReason);
    }

    [Fact]
    public async Task Nothing_is_asked_about_where_the_rep_was_when_they_left()
    {
        // VIS-05 captures a check-out point as a cheap counter against a visit that was never
        // really worked — but it is captured, not judged. A rep who has done the job and walked to
        // the car has not done anything wrong, and a second override prompt at the door would be
        // the flag that fires on ordinary work.
        using var client = Admin();

        var outletId = await OutletAsync(client);
        var detail = await CheckInAsync(client, outletId);

        var response = await CheckOutAsync(
            client, detail.Visit.Id, latitude: 44.4838, longitude: 26.0946);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var visit = (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;

        Assert.Equal(44.4838, visit.CheckOutLatitude);
        Assert.Equal("Productive", visit.Outcome);
    }

    [Fact]
    public async Task Leaving_without_saying_where_is_allowed()
    {
        using var client = Admin();

        var outletId = await OutletAsync(client);
        var detail = await CheckInAsync(client, outletId);

        var response = await CheckOutAsync(client, detail.Visit.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!
            .Visit.CheckOutLatitude);
    }

    [Fact]
    public async Task Half_a_position_is_refused_at_this_end_too()
    {
        using var client = Admin();

        var outletId = await OutletAsync(client);
        var detail = await CheckInAsync(client, outletId);

        var refused = await CheckOutAsync(client, detail.Visit.Id, latitude: Shop.Latitude);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // The code names *this* end of the visit: half a position at the door and half a position on
        // the way out are different client bugs.
        Assert.Equal(
            "visit.checkOut.halfPosition",
            Assert.Single(await Refusals.ProblemsOf(refused)).Code);
    }

    [Fact]
    public async Task A_sealed_visit_cannot_be_checked_out_again()
    {
        using var client = Admin();

        var outletId = await OutletAsync(client);
        var detail = await CheckInAsync(client, outletId);

        var first = await CheckOutAsync(client, detail.Visit.Id);
        var sealedAt = (await first.Content.ReadFromJsonAsync<VisitDetailResponse>())!
            .Visit.CheckedOutAtUtc;

        var again = await CheckOutAsync(client, detail.Visit.Id, VisitOutcome.NonProductive, "Nope");

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(
            "visit.checkOut.alreadyCheckedOut",
            Assert.Single(await Refusals.ProblemsOf(again)).Code);

        var reread = await client.GetFromJsonAsync<VisitDetailResponse>(
            $"/api/visits/{detail.Visit.Id}");

        Assert.Equal("Productive", reread!.Visit.Outcome);
        Assert.Null(reread.Visit.OutcomeReason);
        Assert.NotNull(sealedAt);
    }

    [Fact]
    public async Task A_sealed_visit_cannot_have_its_steps_worked()
    {
        // BR-VIS-4, and the write path most likely to be reached by accident: a device that pushes
        // a queued step completion after the visit has already gone. It is refused rather than
        // applied, because a sealed visit that quietly grows a completed step is a visit whose
        // record disagrees with the one that was already reported.
        using var client = Admin();

        var outletId = await OutletAsync(
            client, new VisitStepRequest(VisitStepType.Note, false, "Anything else"));

        var detail = await CheckInAsync(client, outletId);

        await CheckOutAsync(client, detail.Visit.Id);

        var late = await client.PostAsJsonAsync(
            $"/api/visits/{detail.Visit.Id}/steps/{detail.Steps.Single().Id}/complete",
            new CompleteStepRequest("Something I forgot"));

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal(
            "visit.step.visitSealed",
            Assert.Single(await Refusals.ProblemsOf(late)).Code);

        var reread = await client.GetFromJsonAsync<VisitDetailResponse>(
            $"/api/visits/{detail.Visit.Id}");

        Assert.Equal("Pending", reread!.Steps.Single().Status);
    }

    [Fact]
    public async Task A_sealed_visit_answers_the_same_way_for_a_step_that_never_existed()
    {
        // The seal is checked before the step is looked up, so a late write cannot use this route
        // to find out what was on somebody's visit.
        using var client = Admin();

        var outletId = await OutletAsync(client);
        var detail = await CheckInAsync(client, outletId);

        await CheckOutAsync(client, detail.Visit.Id);

        var late = await client.PostAsJsonAsync(
            $"/api/visits/{detail.Visit.Id}/steps/{Guid.NewGuid()}/complete",
            new CompleteStepRequest());

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal(
            "visit.step.visitSealed",
            Assert.Single(await Refusals.ProblemsOf(late)).Code);
    }

    [Fact]
    public async Task Checking_out_announces_the_visit_exactly_once()
    {
        // VisitCompleted goes to reporting, Journey and Sync — none of which exist yet (W8, Phase
        // 3). The same shape JourneyPublished and PriceListPublished had: an event is true whether
        // or not anything is listening, and the outbox row is what proves it was raised.
        using var client = Admin();

        var outletId = await OutletAsync(
            client,
            new VisitStepRequest(VisitStepType.Audit, true, "Shelf check"),
            new VisitStepRequest(VisitStepType.Note, false, "Anything else"));

        var detail = await CheckInAsync(client, outletId);

        Assert.Equal(0, await OutboxCountAsync(detail.Visit.Id));

        // A refused check-out must not announce anything — the mandatory step is still open.
        await CheckOutAsync(client, detail.Visit.Id);
        Assert.Equal(0, await OutboxCountAsync(detail.Visit.Id));

        await client.PostAsJsonAsync(
            $"/api/visits/{detail.Visit.Id}/steps/{detail.OpenMandatorySteps.Single().Id}/complete",
            new CompleteStepRequest());

        await CheckOutAsync(client, detail.Visit.Id, VisitOutcome.NonProductive, "Nobody to see");
        Assert.Equal(1, await OutboxCountAsync(detail.Visit.Id));

        // And the refused second check-out must not announce a second one.
        await CheckOutAsync(client, detail.Visit.Id);
        Assert.Equal(1, await OutboxCountAsync(detail.Visit.Id));

        var announced = await AnnouncementAsync(detail.Visit.Id);

        Assert.Equal("NonProductive", announced.GetProperty("Outcome").GetString());
        Assert.Equal(outletId, announced.GetProperty("OutletId").GetGuid());
        Assert.Equal(2, announced.GetProperty("StepCount").GetInt32());
        Assert.Equal(1, announced.GetProperty("StepsCompleted").GetInt32());

        // No time-on-site field: it is check-out minus check-in, both of which are carried, and a
        // computed duplicate is a second answer that can disagree with the first.
        Assert.False(announced.TryGetProperty("TimeOnSite", out _));
    }

    [Fact]
    public async Task Checking_out_needs_the_same_permission_as_checking_in()
    {
        using var client = Admin();
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        var outletId = await OutletAsync(client);
        var detail = await CheckInAsync(client, outletId);

        var refused = await CheckOutAsync(viewer, detail.Visit.Id);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task Another_tenants_visit_cannot_be_ended()
    {
        using var client = Admin();
        using var otherTenant = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var outletId = await OutletAsync(client);
        var detail = await CheckInAsync(client, outletId);

        var refused = await CheckOutAsync(otherTenant, detail.Visit.Id);

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }

    [Fact]
    public async Task An_outcome_that_is_not_one_of_the_outcomes_is_a_400()
    {
        // api-contracts §3.1: enums travel by name, and a name that is not one of the names is the
        // caller's mistake, not a 500.
        using var client = Admin();

        var outletId = await OutletAsync(client);
        var detail = await CheckInAsync(client, outletId);

        var refused = await client.PostAsJsonAsync(
            $"/api/visits/{detail.Visit.Id}/check-out", new { outcome = "Splendid" });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task A_reason_longer_than_the_column_is_refused_rather_than_truncated()
    {
        using var client = Admin();

        var outletId = await OutletAsync(client);
        var detail = await CheckInAsync(client, outletId);

        var refused = await CheckOutAsync(
            client,
            detail.Visit.Id,
            VisitOutcome.NonProductive,
            new string('x', Visit.MaximumOutcomeReasonLength + 1));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(
            "visit.checkOut.reasonTooLong",
            Assert.Single(await Refusals.ProblemsOf(refused)).Code);
    }

    /// <summary>How many <c>VisitCompleted</c> messages the outbox holds for this visit.</summary>
    private async Task<int> OutboxCountAsync(Guid visitId)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VisitDbContext>();

        // Filtered by type in the database and by payload in memory: Content is jsonb, and Postgres
        // has no LIKE for it. The same shape JourneyPlanTests uses, and for the same reason.
        var payloads = await db.Set<OutboxMessage>()
            .Where(message => message.Type.Contains(nameof(VisitCompleted)))
            .Select(message => message.Content)
            .ToListAsync();

        return payloads.Count(content =>
            content.Contains(visitId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private async Task<JsonElement> AnnouncementAsync(Guid visitId)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VisitDbContext>();

        var payloads = await db.Set<OutboxMessage>()
            .Where(message => message.Type.Contains(nameof(VisitCompleted)))
            .Select(message => message.Content)
            .ToListAsync();

        var mine = payloads.Single(content =>
            content.Contains(visitId.ToString(), StringComparison.OrdinalIgnoreCase));

        return JsonDocument.Parse(mine).RootElement;
    }
}
