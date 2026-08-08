using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Outlets;

namespace FieldKit.Server.Tests;

/// <summary>
/// The per-channel visit workflow (<c>VIS-03</c>, <c>BR-VIS-2</c>) — W7 slice 6.
/// </summary>
/// <remarks>
/// <para>
/// Built one slice ahead of its consumer on purpose: <c>BR-VIS-2</c>'s override rule cannot be
/// written without somewhere to ask whether presence was expected, so check-in depends on this
/// rather than the other way round. These tests are therefore the whole of its coverage until
/// slice 7 — the same position <c>RepScopeTests</c> was in.
/// </para>
/// <para>
/// The assertions that matter are about the <i>default</i>. A channel nobody has configured has to
/// answer, and answer the safe way round: presence expected, so an off-site check-in is recorded
/// rather than silently accepted.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class VisitWorkflowTests(ServerFixture fixture)
{
    private const string Workflows = "/api/config/visit-workflows";

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    private static async Task<Guid> ChannelAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        return (await created.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    private static Task<HttpResponseMessage> SetAsync(
        HttpClient client, Guid channelId, bool presenceExpected, params VisitStepRequest[] steps) =>
        client.PutAsJsonAsync(
            $"{Workflows}/{channelId}", new VisitWorkflowRequest(presenceExpected, steps));

    [Fact]
    public async Task A_channel_nobody_configured_still_answers_and_expects_presence()
    {
        // The contract promises never to return null, and this is why: check-in asks whether
        // presence was expected and gets an answer, rather than asking whether anybody configured
        // this channel and then having to decide what that means.
        //
        // Expected rather than not, because the two mistakes are not equal. Presence expected on a
        // remote channel records an exception for every ordinary call — annoying, and visible.
        // Presence not expected on a store channel silently stops recording the one thing BR-VIS-2
        // exists to capture.
        using var client = Admin();

        var channelId = await ChannelAsync(client);

        var workflow = await client.GetFromJsonAsync<VisitWorkflowResponse>($"{Workflows}/{channelId}");

        Assert.True(workflow!.PresenceExpected);
        Assert.Empty(workflow.Steps);
    }

    [Fact]
    public async Task Steps_come_back_in_the_order_they_were_sent()
    {
        // The order is the position in the submitted list, not a number the caller supplies. A
        // client that sends its own can send 1, 2, 2, 7 — and then every consumer has to decide what
        // a gap or a tie means.
        using var client = Admin();

        var channelId = await ChannelAsync(client);

        var response = await SetAsync(
            client, channelId, presenceExpected: true,
            new VisitStepRequest(VisitStepType.Audit, true, "Shelf check"),
            new VisitStepRequest(VisitStepType.Order, false, "Take an order"),
            new VisitStepRequest(VisitStepType.Note, false, "Anything else"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var workflow = (await response.Content.ReadFromJsonAsync<VisitWorkflowResponse>())!;

        Assert.Equal([1, 2, 3], workflow.Steps.Select(step => step.Order));
        Assert.Equal(["Shelf check", "Take an order", "Anything else"], workflow.Steps.Select(step => step.Label));
        Assert.Equal(["Audit", "Order", "Note"], workflow.Steps.Select(step => step.Type));
    }

    [Fact]
    public async Task Mandatory_is_a_property_of_the_step_rather_than_of_its_type()
    {
        // The same kind of work is required in one channel and optional in another — an audit is the
        // job in modern trade and a courtesy in a kiosk. BR-VIS-3 gates check-out on the flag, so it
        // has to be per step or the rule cannot express what a tenant means.
        using var client = Admin();

        var channelId = await ChannelAsync(client);

        var response = await SetAsync(
            client, channelId, presenceExpected: true,
            new VisitStepRequest(VisitStepType.Audit, true, "Shelf check"),
            new VisitStepRequest(VisitStepType.Audit, false, "Competitor prices"));

        var workflow = (await response.Content.ReadFromJsonAsync<VisitWorkflowResponse>())!;

        Assert.Equal([true, false], workflow.Steps.Select(step => step.Mandatory));
    }

    [Fact]
    public async Task Setting_a_workflow_again_replaces_the_sequence_rather_than_adding_to_it()
    {
        // Wholesale, like a role's permissions and an outlet's contacts. A patch would need the
        // caller to know the current order to say anything about it, and two admins editing one
        // channel would interleave into a sequence neither designed.
        using var client = Admin();

        var channelId = await ChannelAsync(client);

        await SetAsync(
            client, channelId, presenceExpected: true,
            new VisitStepRequest(VisitStepType.Audit, true, "Shelf check"),
            new VisitStepRequest(VisitStepType.Order, false, "Take an order"));

        var response = await SetAsync(
            client, channelId, presenceExpected: false,
            new VisitStepRequest(VisitStepType.Survey, true, "Quarterly questionnaire"));

        var workflow = (await response.Content.ReadFromJsonAsync<VisitWorkflowResponse>())!;

        Assert.Single(workflow.Steps);
        Assert.Equal("Quarterly questionnaire", workflow.Steps[0].Label);
        Assert.Equal(1, workflow.Steps[0].Order);
        Assert.False(workflow.PresenceExpected);
    }

    [Fact]
    public async Task A_remote_channel_says_presence_is_not_expected()
    {
        // BR-VIS-2's assumption, and the whole reason this contract exists before check-in does. A
        // phone call is legitimately not at the outlet, so demanding an override reason would record
        // an exception where nothing exceptional happened — and a flag that fires on ordinary work
        // is a flag supervisors learn to ignore.
        using var client = Admin();

        var channelId = await ChannelAsync(client);

        await SetAsync(
            client, channelId, presenceExpected: false,
            new VisitStepRequest(VisitStepType.Order, false, "Take the order"));

        var workflow = await client.GetFromJsonAsync<VisitWorkflowResponse>($"{Workflows}/{channelId}");

        Assert.False(workflow!.PresenceExpected);
    }

    [Fact]
    public async Task A_workflow_with_no_steps_is_allowed_because_a_presence_call_is_a_real_thing()
    {
        // Check in, check out. Refusing it would force an admin to invent a step to describe the
        // simplest possible visit.
        using var client = Admin();

        var channelId = await ChannelAsync(client);

        var response = await SetAsync(client, channelId, presenceExpected: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var workflow = (await response.Content.ReadFromJsonAsync<VisitWorkflowResponse>())!;

        Assert.Empty(workflow.Steps);
    }

    [Fact]
    public async Task Deleting_a_workflow_returns_the_channel_to_the_default()
    {
        // Rather than leaving visits in it unworkable. There is no "no workflow" state a visit has
        // to handle, which is what makes deleting one safe.
        using var client = Admin();

        var channelId = await ChannelAsync(client);

        await SetAsync(
            client, channelId, presenceExpected: false,
            new VisitStepRequest(VisitStepType.Order, true, "Take the order"));

        var deleted = await client.DeleteAsync($"{Workflows}/{channelId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var workflow = await client.GetFromJsonAsync<VisitWorkflowResponse>($"{Workflows}/{channelId}");

        Assert.True(workflow!.PresenceExpected);
        Assert.Empty(workflow.Steps);
    }

    [Fact]
    public async Task A_step_with_no_label_is_refused_and_the_message_says_which_one()
    {
        // Indexed, because an admin looking at eight steps cannot work out which one "a step needs a
        // label" is about.
        using var client = Admin();

        var channelId = await ChannelAsync(client);

        var response = await SetAsync(
            client, channelId, presenceExpected: true,
            new VisitStepRequest(VisitStepType.Audit, true, "Shelf check"),
            new VisitStepRequest(VisitStepType.Note, false, "   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problems = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains(
            problems.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("field").GetString() == "steps[1].label");
    }

    [Fact]
    public async Task A_workflow_longer_than_a_rep_could_work_is_refused()
    {
        using var client = Admin();

        var channelId = await ChannelAsync(client);

        var steps = Enumerable.Range(0, 31)
            .Select(index => new VisitStepRequest(VisitStepType.Task, false, $"Step {index}"))
            .ToArray();

        var response = await SetAsync(client, channelId, presenceExpected: true, steps);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problems = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains(
            problems.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("code").GetString() == "config.workflow.tooManySteps");
    }

    [Fact]
    public async Task A_workflow_for_a_channel_this_tenant_does_not_have_is_refused()
    {
        // Otherwise it sits in the list looking configured, and no visit ever resolves it.
        using var client = Admin();

        var response = await SetAsync(
            client, Guid.CreateVersion7(), presenceExpected: true,
            new VisitStepRequest(VisitStepType.Order, false, "Take an order"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problems = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains(
            problems.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("code").GetString() == "config.workflow.unknownChannel");
    }

    [Fact]
    public async Task A_step_type_that_is_not_one_is_the_callers_mistake()
    {
        // The enum travels by name, so a name that is not one of the names is a 400 rather than a
        // 500 — the same rule every other enum on this API follows.
        using var client = Admin();

        var channelId = await ChannelAsync(client);

        var response = await client.PutAsync(
            $"{Workflows}/{channelId}",
            new StringContent(
                """{"presenceExpected":true,"steps":[{"type":"Interpretive dance","mandatory":false,"label":"x"}]}""",
                System.Text.Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task One_tenants_workflow_never_answers_for_anothers_channel()
    {
        // Channel ids are per tenant, so nothing about the argument says whose it is — only the
        // query filter does. Tenant B asking about A's channel gets the default rather than A's
        // policy, which matters: A's channel might be remote-capable, and inheriting that would
        // switch off B's override recording.
        using var tenantA = Admin();
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var channelId = await ChannelAsync(tenantA);

        await SetAsync(
            tenantA, channelId, presenceExpected: false,
            new VisitStepRequest(VisitStepType.Order, true, "Take the order"));

        var seenByB = await tenantB.GetFromJsonAsync<VisitWorkflowResponse>($"{Workflows}/{channelId}");

        Assert.True(seenByB!.PresenceExpected);
        Assert.Empty(seenByB.Steps);
    }

    [Fact]
    public async Task Offers_no_way_to_change_a_workflow_to_a_caller_who_may_only_read()
    {
        using var reader = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        var listed = await reader.GetAsync(Workflows);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        var attempted = await reader.PutAsJsonAsync(
            $"{Workflows}/{Guid.CreateVersion7()}",
            new VisitWorkflowRequest(true, [new VisitStepRequest(VisitStepType.Note, false, "x")]));

        Assert.Equal(HttpStatusCode.Forbidden, attempted.StatusCode);
    }
}
