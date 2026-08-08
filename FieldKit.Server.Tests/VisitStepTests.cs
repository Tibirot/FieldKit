using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Visit;

namespace FieldKit.Server.Tests;

/// <summary>
/// The steps a visit is worked through, and what holds it open (<c>VIS-03</c>, <c>VIS-04</c>,
/// <c>BR-VIS-3</c>) — W7 slice 8.
/// </summary>
/// <remarks>
/// <para>
/// The assertion this file exists for is the <b>snapshot</b>: a visit's steps are copied at check-in
/// and an admin editing the channel workflow afterwards changes nothing about a visit already in
/// progress. Everything else here is a consequence of that.
/// </para>
/// <para>
/// <c>BR-VIS-3</c> itself — no check-out while a mandatory step is open — is only half testable in
/// this slice, because check-out arrives in the next one. What is asserted here is the half that
/// exists: the visit says which mandatory steps are still open, on every response, so the rule can
/// be enforced at the door without the rep having been surprised by it.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class VisitStepTests(ServerFixture fixture)
{
    private const string CheckIn = "/api/visits/check-in";
    private const string Zone = "Europe/Bucharest";

    private static readonly Coordinates Shop = new(44.4638, 26.0946);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    /// <summary>An outlet whose channel is worked through <paramref name="steps"/>.</summary>
    private static async Task<(Guid OutletId, Guid ChannelId)> OutletAsync(
        HttpClient client, params VisitStepRequest[] steps)
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        if (steps.Length > 0)
        {
            var set = await client.PutAsJsonAsync(
                $"/api/config/visit-workflows/{channelId}",
                new VisitWorkflowRequest(PresenceExpected: true, steps));

            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        }

        var created = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, Location: Shop));

        return ((await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id, channelId);
    }

    private static async Task<VisitDetailResponse> CheckInAsync(HttpClient client, Guid outletId)
    {
        var response = await client.PostAsJsonAsync(
            CheckIn, new CheckInRequest(outletId, Shop.Latitude, Shop.Longitude));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!;
    }

    private static Task<HttpResponseMessage> CompleteAsync(
        HttpClient client, Guid visitId, Guid stepId, string? notes = null) =>
        client.PostAsJsonAsync(
            $"/api/visits/{visitId}/steps/{stepId}/complete", new CompleteStepRequest(notes));

    [Fact]
    public async Task Checking_in_lays_out_the_channels_steps_in_order()
    {
        using var client = Admin();

        var (outletId, _) = await OutletAsync(
            client,
            new VisitStepRequest(VisitStepType.Audit, true, "Shelf check"),
            new VisitStepRequest(VisitStepType.Order, true, "Take the order"),
            new VisitStepRequest(VisitStepType.Note, false, "Anything else"));

        var detail = await CheckInAsync(client, outletId);

        Assert.Equal(3, detail.Steps.Count);
        Assert.Equal([1, 2, 3], detail.Steps.Select(step => step.Order));
        Assert.Equal(["Audit", "Order", "Note"], detail.Steps.Select(step => step.Type));
        Assert.All(detail.Steps, step => Assert.Equal("Pending", step.Status));

        // The rep sees what stands between them and the door from the first screen, not at the door.
        Assert.Equal(2, detail.OpenMandatorySteps.Count);
    }

    [Fact]
    public async Task Editing_the_workflow_does_not_reach_back_into_a_visit_already_running()
    {
        // The assertion this slice is really about. A rep who checked in at ten must not be refused
        // check-out for a step that did not exist when they started — nor released from one they
        // were told was compulsory. BR-VIS-6's snapshot rule, applied to the one piece of reference
        // data that decides whether a visit can end.
        using var client = Admin();

        var (outletId, channelId) = await OutletAsync(
            client, new VisitStepRequest(VisitStepType.Task, false, "Check the chiller is lit"));

        var detail = await CheckInAsync(client, outletId);

        var rewritten = await client.PutAsJsonAsync(
            $"/api/config/visit-workflows/{channelId}",
            new VisitWorkflowRequest(
                PresenceExpected: true,
                [
                    new VisitStepRequest(VisitStepType.Audit, true, "New mandatory audit"),
                    new VisitStepRequest(VisitStepType.Order, true, "New mandatory order"),
                ]));

        Assert.Equal(HttpStatusCode.OK, rewritten.StatusCode);

        var reread = await client.GetFromJsonAsync<VisitDetailResponse>(
            $"/api/visits/{detail.Visit.Id}");

        var step = Assert.Single(reread!.Steps);

        Assert.Equal("Check the chiller is lit", step.Label);
        Assert.False(step.Mandatory);
        Assert.Empty(reread.OpenMandatorySteps);

        // And the *next* visit gets the new workflow — the snapshot is per visit, not a freeze.
        var next = await CheckInAsync(client, outletId);

        Assert.Equal(2, next.Steps.Count);
        Assert.Equal(2, next.OpenMandatorySteps.Count);
    }

    [Fact]
    public async Task Completing_the_mandatory_steps_empties_what_is_outstanding()
    {
        using var client = Admin();

        var (outletId, _) = await OutletAsync(
            client,
            new VisitStepRequest(VisitStepType.Audit, true, "Shelf check"),
            new VisitStepRequest(VisitStepType.Task, false, "Chiller lit"));

        var detail = await CheckInAsync(client, outletId);

        var mandatory = Assert.Single(detail.OpenMandatorySteps);

        var completed = await CompleteAsync(client, detail.Visit.Id, mandatory.Id);

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

        var after = (await completed.Content.ReadFromJsonAsync<VisitDetailResponse>())!;

        Assert.Empty(after.OpenMandatorySteps);
        Assert.Equal("Completed", after.Steps.Single(step => step.Id == mandatory.Id).Status);
        Assert.NotNull(after.Steps.Single(step => step.Id == mandatory.Id).CompletedAtUtc);

        // The optional one is left where it was. There is no Skipped: a step nobody did is pending,
        // and BR-VIS-3 does not care about it.
        Assert.Equal("Pending", after.Steps.Single(step => !step.Mandatory).Status);
    }

    [Fact]
    public async Task An_optional_step_left_undone_holds_nothing_open()
    {
        using var client = Admin();

        var (outletId, _) = await OutletAsync(
            client, new VisitStepRequest(VisitStepType.Note, false, "Anything else"));

        var detail = await CheckInAsync(client, outletId);

        Assert.Single(detail.Steps);
        Assert.Empty(detail.OpenMandatorySteps);
    }

    [Fact]
    public async Task A_channel_nobody_configured_gives_a_visit_with_no_steps()
    {
        // Check in, check out. A real visit — a presence call — and not a misconfiguration, which is
        // why IVisitWorkflow returns an empty list rather than null for an unconfigured channel.
        using var client = Admin();

        var (outletId, _) = await OutletAsync(client);

        var detail = await CheckInAsync(client, outletId);

        Assert.Empty(detail.Steps);
        Assert.Empty(detail.OpenMandatorySteps);
    }

    [Fact]
    public async Task Doing_a_step_twice_is_refused_rather_than_restamped()
    {
        // The first completion's timestamp is a fact about the rep's day. Overwriting it would make
        // time-on-step a measure of the last edit — the same reasoning that refuses a second
        // not-visited reason in Journey.
        using var client = Admin();

        var (outletId, _) = await OutletAsync(
            client, new VisitStepRequest(VisitStepType.Task, true, "Chiller lit"));

        var detail = await CheckInAsync(client, outletId);
        var step = Assert.Single(detail.Steps);

        var first = await CompleteAsync(client, detail.Visit.Id, step.Id);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Read the stamp back from the database rather than from the response: Postgres stores
        // microseconds and .NET counts hundred-nanosecond ticks, so the in-memory value the write
        // returned is not bit-for-bit the stored one. Comparing stored-to-stored is what "it was
        // not restamped" actually means.
        var stamped = (await client.GetFromJsonAsync<VisitDetailResponse>(
            $"/api/visits/{detail.Visit.Id}"))!.Steps.Single().CompletedAtUtc;

        var again = await CompleteAsync(client, detail.Visit.Id, step.Id);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(
            "visit.step.alreadyCompleted",
            Assert.Single(await Refusals.ProblemsOf(again)).Code);

        var reread = await client.GetFromJsonAsync<VisitDetailResponse>(
            $"/api/visits/{detail.Visit.Id}");

        Assert.Equal(stamped, reread!.Steps.Single().CompletedAtUtc);
    }

    [Fact]
    public async Task A_note_step_needs_a_note()
    {
        // A note step *is* its text (VIS-06). One ticked with nothing written is a step that was
        // ticked, which is the thing the requirement exists to avoid.
        using var client = Admin();

        var (outletId, _) = await OutletAsync(
            client, new VisitStepRequest(VisitStepType.Note, true, "What did you see"));

        var detail = await CheckInAsync(client, outletId);
        var step = Assert.Single(detail.Steps);

        var empty = await CompleteAsync(client, detail.Visit.Id, step.Id, "   ");

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(
            "visit.step.noteRequired",
            Assert.Single(await Refusals.ProblemsOf(empty)).Code);

        var written = await CompleteAsync(
            client, detail.Visit.Id, step.Id, "  Competitor ran a 2+1 on the top shelf.  ");

        Assert.Equal(HttpStatusCode.OK, written.StatusCode);

        var after = (await written.Content.ReadFromJsonAsync<VisitDetailResponse>())!;

        Assert.Equal("Competitor ran a 2+1 on the top shelf.", after.Steps.Single().Notes);
    }

    [Fact]
    public async Task Other_step_types_take_notes_or_leave_them()
    {
        using var client = Admin();

        var (outletId, _) = await OutletAsync(
            client, new VisitStepRequest(VisitStepType.Task, true, "Chiller lit"));

        var detail = await CheckInAsync(client, outletId);
        var step = Assert.Single(detail.Steps);

        var completed = await CompleteAsync(client, detail.Visit.Id, step.Id);

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Null((await completed.Content.ReadFromJsonAsync<VisitDetailResponse>())!
            .Steps.Single().Notes);
    }

    [Fact]
    public async Task A_note_longer_than_the_column_is_refused_rather_than_truncated()
    {
        using var client = Admin();

        var (outletId, _) = await OutletAsync(
            client, new VisitStepRequest(VisitStepType.Note, false, "Anything else"));

        var detail = await CheckInAsync(client, outletId);
        var step = Assert.Single(detail.Steps);

        var refused = await CompleteAsync(
            client, detail.Visit.Id, step.Id, new string('x', VisitStep.MaximumNotesLength + 1));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(
            "visit.step.notesTooLong",
            Assert.Single(await Refusals.ProblemsOf(refused)).Code);
    }

    [Fact]
    public async Task A_step_from_another_visit_is_no_step_at_all()
    {
        // The step id is not enough to name a step: it has to be on the visit the route named.
        // Otherwise one rep could complete another's work by knowing a guid.
        using var client = Admin();

        var (outletId, _) = await OutletAsync(
            client, new VisitStepRequest(VisitStepType.Task, true, "Chiller lit"));

        var mine = await CheckInAsync(client, outletId);
        var theirs = await CheckInAsync(client, outletId);

        var crossed = await CompleteAsync(client, mine.Visit.Id, theirs.Steps.Single().Id);

        Assert.Equal(HttpStatusCode.NotFound, crossed.StatusCode);

        var reread = await client.GetFromJsonAsync<VisitDetailResponse>(
            $"/api/visits/{theirs.Visit.Id}");

        Assert.Equal("Pending", reread!.Steps.Single().Status);
    }

    [Fact]
    public async Task Another_tenants_visit_cannot_have_its_steps_worked()
    {
        using var client = Admin();
        using var otherTenant = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var (outletId, _) = await OutletAsync(
            client, new VisitStepRequest(VisitStepType.Task, true, "Chiller lit"));

        var detail = await CheckInAsync(client, outletId);

        var crossed = await CompleteAsync(
            otherTenant, detail.Visit.Id, detail.Steps.Single().Id);

        Assert.Equal(HttpStatusCode.NotFound, crossed.StatusCode);
    }

    [Fact]
    public async Task Working_a_step_needs_the_same_permission_as_checking_in()
    {
        // Doing the work and saying you did it are the same act, so they sit behind the same
        // permission — a viewer who could tick a rep's steps could complete a visit they never made.
        using var client = Admin();
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        var (outletId, _) = await OutletAsync(
            client, new VisitStepRequest(VisitStepType.Task, true, "Chiller lit"));

        var detail = await CheckInAsync(client, outletId);

        var refused = await CompleteAsync(viewer, detail.Visit.Id, detail.Steps.Single().Id);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task A_visit_that_does_not_exist_has_no_steps_to_complete()
    {
        using var client = Admin();

        var refused = await CompleteAsync(client, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }
}
