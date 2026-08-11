using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Journey;
using FieldKit.Modules.Journey.Contracts;
using FieldKit.Modules.Org;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Sync;

namespace FieldKit.Server.Tests;

/// <summary>
/// Rep-side annotations pushed from a device (<c>VIS-07</c>, <c>OFF-04</c>) — W9 slice 9.
/// </summary>
/// <remarks>
/// <para>
/// <b>The slice that turns <c>PushedMutation.Type</c> into a discriminator.</b> With one kind of
/// mutation, routing was a guard against nonsense; with four it decides which module's contract runs.
/// So the tests that matter are not "a not-visited call can be pushed" but the two that only appear
/// once there is more than one arm: a batch mixing kinds applies each through its own module, and a
/// payload that does not match its type is refused rather than silently dropped.
/// </para>
/// <para>
/// <b>Every annotation is scoped to the rep in the token</b>, and that is the security property this
/// file exists to pin. A device sends ids it read out of its own round; nothing stops a modified
/// client sending somebody else's. Those tests assert the refusal is indistinguishable from a
/// missing call — a device must not be able to use this as an oracle for another rep's plan.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class SyncPushJourneyTests(ServerFixture fixture)
{
    /*
     * The rep working the device here is the **admin** subject, deliberately, and it is worth saying
     * why because it looks like a mistake.
     *
     * These tests do the one thing no other test in the suite does: they leave a shared rep's calls
     * in `NotVisited`. Written against `fixture.AccessToken` they passed alone and broke three of
     * `SyncPullJourneyTests`, which asserts every call on that rep's round comes back `Planned` — a
     * fair assertion until something annotated the rounds behind it. Nothing pulls as admin, so the
     * annotations land where no other test is reading.
     *
     * The one test that needs a *second* identity — a rep cannot annotate somebody else's round —
     * uses the ordinary rep token for the round it must not be able to touch, and only reads it.
     */

    private static readonly DateOnly From = new(2026, 4, 6);
    private static readonly DateOnly To = new(2026, 4, 30);
    private const string Zone = "Europe/Bucharest";

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private static async Task<Guid> BindDeviceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/devices", new BindDeviceRequest(Unique("Phone")));

        return (await response.Content.ReadFromJsonAsync<DeviceResponse>())!.Id;
    }

    private static async Task<PushResponse> PushAsync(
        HttpClient client, Guid deviceId, params PushedMutation[] mutations)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/push", new PushRequest(deviceId, mutations));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PushResponse>())!;
    }

    private static PushedMutation NotVisited(Guid callId, string reason, Guid? mutationId = null) =>
        new(mutationId ?? Guid.CreateVersion7(), nameof(NotVisitedCall), null,
            NotVisited: new NotVisitedCall(callId, reason));

    private static PushedMutation Reschedule(Guid callId, DateOnly date, Guid? mutationId = null) =>
        new(mutationId ?? Guid.CreateVersion7(), nameof(RescheduledCall), null,
            Rescheduled: new RescheduledCall(callId, date));

    private static PushedMutation Unplanned(Guid outletId, DateOnly date, Guid? mutationId = null) =>
        new(mutationId ?? Guid.CreateVersion7(), nameof(UnplannedCall), null,
            Unplanned: new UnplannedCall(outletId, date));

    // ── not visited ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_call_the_rep_could_not_make_is_recorded_with_their_reason()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var (subject, planId, calls, _) = await RoundAsync(rep, admin);
        var device = await BindDeviceAsync(rep);

        var push = await PushAsync(rep, device, NotVisited(calls[0], "Closed for refurbishment"));

        Assert.Equal("accepted", Assert.Single(push.Results).Status);

        var call = await CallAsync(admin, planId, calls[0]);

        Assert.Equal("NotVisited", call.GetProperty("status").GetString());
        Assert.Equal("Closed for refurbishment", call.GetProperty("notVisitedReason").GetString());
        Assert.NotEqual(string.Empty, subject);
    }

    [Fact]
    public async Task Pushing_the_same_annotation_twice_changes_the_round_once()
    {
        // The retry story, and the reason it is not a refusal. A device that lost the response has no
        // way to tell success from failure, so it re-sends — and the second attempt finds the call
        // already marked. Answering "refused" would strand the mutation in the outbox forever over
        // work that is done.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var (_, planId, calls, _) = await RoundAsync(rep, admin);
        var device = await BindDeviceAsync(rep);

        var first = await PushAsync(rep, device, NotVisited(calls[0], "Closed on arrival"));
        Assert.Equal("accepted", Assert.Single(first.Results).Status);

        // A *different* mutation id, so the ledger does not answer this one: the idempotency being
        // tested is the domain's, which is what covers a ledger entry that was lost.
        var again = await PushAsync(rep, device, NotVisited(calls[0], "A different sentence"));
        Assert.Equal("accepted", Assert.Single(again.Results).Status);

        var call = await CallAsync(admin, planId, calls[0]);

        // The first reason survives. It is what the rep wrote standing at the shop.
        Assert.Equal("Closed on arrival", call.GetProperty("notVisitedReason").GetString());
    }

    [Fact]
    public async Task A_skipped_call_with_nothing_said_is_refused()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var (_, _, calls, _) = await RoundAsync(rep, admin);
        var device = await BindDeviceAsync(rep);

        var push = await PushAsync(rep, device, NotVisited(calls[0], "   "));

        var result = Assert.Single(push.Results);
        Assert.Equal("rejected", result.Status);
        Assert.Equal("journey.visit.reasonRequired", result.Reason);
    }

    [Fact]
    public async Task A_rep_cannot_annotate_another_reps_round_or_learn_that_it_exists()
    {
        // The security property. `admin` owns this round; `rep` sends its call id, which a modified
        // client could do trivially. The refusal has to be the same one a fabricated id gets.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var someoneElse = await SubjectAsync(fixture.CreateAuthenticatedClient(fixture.AccessToken));
        await ScenarioAsync(admin, someoneElse, outletCount: 2);
        var theirPlan = await GenerateAsync(admin, someoneElse);
        await admin.PostAsync($"/api/journey/plans/{theirPlan}/publish", null);
        var theirCalls = await CallsAsync(admin, theirPlan);

        var device = await BindDeviceAsync(rep);

        var theirs = await PushAsync(rep, device, NotVisited(theirCalls[0], "Not mine to report on"));
        var invented = await PushAsync(rep, device, NotVisited(Guid.CreateVersion7(), "No such call"));

        Assert.Equal("journey.visit.unknown", Assert.Single(theirs.Results).Reason);
        Assert.Equal("journey.visit.unknown", Assert.Single(invented.Results).Reason);

        // …and nothing happened to their round.
        var untouched = await CallAsync(admin, theirPlan, theirCalls[0]);
        Assert.Equal("Planned", untouched.GetProperty("status").GetString());
    }

    // ── reschedule ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_call_moved_inside_its_cycle_lands_on_the_new_day()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var (_, planId, calls, _) = await RoundAsync(rep, admin);
        var device = await BindDeviceAsync(rep);

        var call = await CallAsync(admin, planId, calls[0]);
        var was = DateOnly.Parse(call.GetProperty("date").GetString()!);
        var moved = was.AddDays(1);

        var push = await PushAsync(rep, device, Reschedule(calls[0], moved));
        Assert.Equal("accepted", Assert.Single(push.Results).Status);

        var after = await CallAsync(admin, planId, calls[0]);
        Assert.Equal(moved, DateOnly.Parse(after.GetProperty("date").GetString()!));
    }

    [Fact]
    public async Task A_call_moved_outside_the_window_is_refused_by_the_rule_that_owns_it()
    {
        // `BR-JRN-4` is Journey's, and it runs here because Sync applies through the module rather
        // than writing the schema. A push path with its own date arithmetic would be a second
        // implementation of the cycle rule, drifting quietly.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var (_, _, calls, _) = await RoundAsync(rep, admin);
        var device = await BindDeviceAsync(rep);

        var push = await PushAsync(rep, device, Reschedule(calls[0], To.AddMonths(2)));

        var result = Assert.Single(push.Results);
        Assert.Equal("rejected", result.Status);
        Assert.Equal("journey.visit.outsideWindow", result.Reason);
    }

    // ── unplanned ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_call_nobody_planned_joins_the_round_that_covers_the_day()
    {
        // The device sends a shop and a day and no plan, because a pulled round is flat calls with
        // no plan on them. Resolving which round covers the day is Journey's job.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var (_, planId, _, outlets) = await RoundAsync(rep, admin);
        var device = await BindDeviceAsync(rep);

        var before = (await CallsAsync(admin, planId)).Count;

        var push = await PushAsync(rep, device, Unplanned(outlets[0], From.AddDays(1)));
        Assert.Equal("accepted", Assert.Single(push.Results).Status);

        Assert.Equal(before + 1, (await CallsAsync(admin, planId)).Count);
    }

    [Fact]
    public async Task An_unplanned_call_is_not_added_twice_for_one_shop_and_day()
    {
        /*
         * The only annotation that creates a row, and therefore the only one the ledger's window can
         * duplicate. Two different mutation ids, so the ledger answers neither — this is the domain
         * guard, and without it a retry past a lost entry overstates the rep's coverage.
         */
        using var rep = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var (_, planId, _, outlets) = await RoundAsync(rep, admin);
        var device = await BindDeviceAsync(rep);

        var day = From.AddDays(1);
        var before = (await CallsAsync(admin, planId)).Count;

        await PushAsync(rep, device, Unplanned(outlets[0], day));
        var again = await PushAsync(rep, device, Unplanned(outlets[0], day));

        // Accepted, not refused: the work the device asked for is done, and a refusal would keep the
        // mutation in the outbox for good.
        Assert.Equal("accepted", Assert.Single(again.Results).Status);
        Assert.Equal(before + 1, (await CallsAsync(admin, planId)).Count);
    }

    [Fact]
    public async Task An_unplanned_call_at_a_shop_already_planned_for_that_day_is_not_added()
    {
        /*
         * Found by a test I had written wrong: the mixed-batch case below originally used a Wednesday
         * — a working day the generator had already planned this shop for — and the count did not
         * move. The duplicate guard was doing its job against a *planned* call rather than a repeat
         * unplanned one, which I had not thought about when I wrote it.
         *
         * It is the behaviour worth having. The planned call is the record of that shop on that day;
         * adding an unplanned twin would count the same visit twice in the coverage figure
         * `BR-JRN-6` measures. So it is pinned here rather than left as an accident.
         */
        using var rep = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var (_, planId, calls, _) = await RoundAsync(rep, admin);
        var device = await BindDeviceAsync(rep);

        var planned = await CallAsync(admin, planId, calls[0]);
        var shop = planned.GetProperty("outletId").GetGuid();
        var day = DateOnly.Parse(planned.GetProperty("date").GetString()!);
        var before = (await CallsAsync(admin, planId)).Count;

        var push = await PushAsync(rep, device, Unplanned(shop, day));

        Assert.Equal("accepted", Assert.Single(push.Results).Status);
        Assert.Equal(before, (await CallsAsync(admin, planId)).Count);
    }

    [Fact]
    public async Task An_unplanned_call_on_a_day_no_round_covers_is_refused()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var (_, _, _, outlets) = await RoundAsync(rep, admin);
        var device = await BindDeviceAsync(rep);

        var push = await PushAsync(rep, device, Unplanned(outlets[0], To.AddYears(1)));

        var result = Assert.Single(push.Results);
        Assert.Equal("rejected", result.Status);
        Assert.Equal("journey.plan.noneForDate", result.Reason);
    }

    // ── the discriminator itself ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_batch_of_three_kinds_reaches_three_different_rules()
    {
        // What "Type is a discriminator" means, asserted end to end. Before this slice the field had
        // one legal value and the routing was a guard; a batch that mixes kinds is the case that can
        // only pass if each arm calls its own contract.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var (_, planId, calls, outlets) = await RoundAsync(rep, admin);
        var device = await BindDeviceAsync(rep);

        var second = await CallAsync(admin, planId, calls[1]);
        var moved = DateOnly.Parse(second.GetProperty("date").GetString()!).AddDays(1);
        var before = (await CallsAsync(admin, planId)).Count;

        var push = await PushAsync(
            rep,
            device,
            NotVisited(calls[0], "Shut for a stock take"),
            Reschedule(calls[1], moved),
            // A Sunday: the working calendar is Mon/Wed/Fri, so the generator planned nothing
            // here and the duplicate guard has nothing to collide with.
            Unplanned(outlets[0], From.AddDays(6)));

        Assert.Equal(3, push.Results.Count);
        Assert.All(push.Results, result => Assert.Equal("accepted", result.Status));

        Assert.Equal("NotVisited", (await CallAsync(admin, planId, calls[0])).GetProperty("status").GetString());
        Assert.Equal(moved, DateOnly.Parse((await CallAsync(admin, planId, calls[1])).GetProperty("date").GetString()!));
        Assert.Equal(before + 1, (await CallsAsync(admin, planId)).Count);
    }

    [Fact]
    public async Task A_mutation_whose_payload_does_not_match_its_type_is_refused()
    {
        // Rejected rather than ignored, for the reason the unsupported-type arm gives: a device that
        // keeps retrying something the server silently drops never drains.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        await RoundAsync(rep, admin);
        var device = await BindDeviceAsync(rep);

        var push = await PushAsync(
            rep, device, new PushedMutation(Guid.CreateVersion7(), nameof(NotVisitedCall), null));

        var result = Assert.Single(push.Results);
        Assert.Equal("rejected", result.Status);
        Assert.Equal("sync.push.payloadMissing", result.Reason);
    }

    [Fact]
    public async Task A_mutation_carrying_only_its_own_payload_binds()
    {
        /*
         * Posted as **raw JSON** rather than as a serialised `PushedMutation`, and that is the whole
         * point of the test.
         *
         * Every other test here constructs the record, so the serialiser writes `"visit": null`
         * alongside the payload — and they all passed while the live client got a 400 on every push.
         * A real device omits the properties it is not using. `Visit` had no default, which makes it
         * a *required* constructor argument to System.Text.Json, so the batch failed to bind before
         * any of this file's logic ran.
         *
         * Worse than a refusal: a 400 fails the whole batch and the device retries it on every
         * reconnect. The live outbox row had eleven attempts on it when I found this.
         */
        using var rep = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var (_, planId, calls, _) = await RoundAsync(rep, admin);
        var device = await BindDeviceAsync(rep);

        var json = $$"""
        {
          "deviceId": "{{device}}",
          "mutations": [
            {
              "mutationId": "{{Guid.CreateVersion7()}}",
              "type": "NotVisitedCall",
              "notVisited": { "plannedVisitId": "{{calls[0]}}", "reason": "Closed on arrival" }
            }
          ]
        }
        """;

        var response = await rep.PostAsync(
            "/api/sync/push", new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<PushResponse>();
        Assert.Equal("accepted", Assert.Single(body!.Results).Status);

        var call = await CallAsync(admin, planId, calls[0]);
        Assert.Equal("NotVisited", call.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_kind_this_server_does_not_carry_is_still_refused_by_name()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        await RoundAsync(rep, admin);
        var device = await BindDeviceAsync(rep);

        /*
         * `CapturedReturn`, and it should be the last name this test needs.
         *
         * It said `CapturedAudit` until W10 slice 6 and `CapturedOrder` until W11 slice 0 — each
         * time because the server learned to carry the very thing the test assumed it could not.
         * Both expirations were silent: the assertion still passed, having stopped asserting
         * anything. Returns are `ORD-11` / `BR-ORD-8`, **Won't v1** by decision rather than by
         * schedule, so this name cannot be overtaken by the roadmap.
         *
         * W11 slice 0 also found a third copy of the same claim, in `SyncPushTests`, that neither
         * this comment nor the vector file knew about — which is the argument for choosing a name
         * that never needs a sweep over remembering to sweep.
         */
        var push = await PushAsync(
            rep, device, new PushedMutation(Guid.CreateVersion7(), "CapturedReturn", null));

        var result = Assert.Single(push.Results);
        Assert.Equal("rejected", result.Status);
        Assert.Equal("sync.push.typeUnsupported", result.Reason);
    }

    // ── fixture ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A published round for the token's own subject, and the shops behind it.</summary>
    private static async Task<(string Subject, Guid PlanId, List<Guid> Calls, List<Guid> Outlets)> RoundAsync(
        HttpClient rep, HttpClient admin)
    {
        var subject = await SubjectAsync(rep);
        var outlets = await ScenarioAsync(admin, subject, outletCount: 2);

        var planId = await GenerateAsync(admin, subject);
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PostAsync($"/api/journey/plans/{planId}/publish", null)).StatusCode);

        return (subject, planId, await CallsAsync(admin, planId), outlets);
    }

    private static async Task<string> SubjectAsync(HttpClient client)
    {
        var whoami = await client.GetFromJsonAsync<JsonElement>("/api/auth/whoami");

        return whoami.GetProperty("subject").GetString()!;
    }

    private static async Task<List<Guid>> CallsAsync(HttpClient admin, Guid planId)
    {
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/journey/plans/{planId}");

        return [.. detail.GetProperty("visits").EnumerateArray().Select(visit => visit.GetProperty("id").GetGuid())];
    }

    private static async Task<JsonElement> CallAsync(HttpClient admin, Guid planId, Guid callId)
    {
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/journey/plans/{planId}");

        return detail.GetProperty("visits").EnumerateArray()
            .Single(visit => visit.GetProperty("id").GetGuid() == callId);
    }

    private static async Task<Guid> GenerateAsync(HttpClient admin, string subject)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/journey/plans", new GeneratePlanRequest(subject, From, To));

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("plan").GetProperty("id").GetGuid();
    }

    /// <summary>Everything a plan needs for one subject, assembled the way an administrator would.</summary>
    private static async Task<List<Guid>> ScenarioAsync(HttpClient admin, string subject, int outletCount)
    {
        var roles = await admin.GetFromJsonAsync<List<RoleResponse>>("/api/iam/roles");
        await admin.PostAsJsonAsync("/api/iam/users", new
        {
            subjectId = subject,
            email = $"{Guid.NewGuid():N}@fieldkit.local",
            displayName = "Journey Push Rep",
            locale = "en-GB",
            timeZone = Zone,
            roleIds = new[] { roles!.First(role => role.IsSystemTemplate).Id },
        });

        var channel = await admin.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));
        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var segment = Unique("SEG");
        var outletIds = new List<Guid>();

        for (var index = 0; index < outletCount; index++)
        {
            var created = await admin.PostAsJsonAsync(
                "/api/outlets",
                new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, segment));

            outletIds.Add((await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id);
        }

        var unit = await admin.PostAsJsonAsync("/api/org/units", new OrgUnitRequest(Unique("Unit")));
        var unitId = (await unit.Content.ReadFromJsonAsync<OrgUnitResponse>())!.Id;

        var territory = await admin.PostAsJsonAsync(
            "/api/org/territories", new TerritoryRequest(Unique("Terr"), unitId));
        var territoryId = (await territory.Content.ReadFromJsonAsync<TerritoryResponse>())!.Id;

        await admin.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/outlets", new AssignOutletsRequest(outletIds));

        var assigned = await admin.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/assignments",
            new RepAssignmentRequest(subject, new DateOnly(2020, 1, 1), null));
        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);

        await admin.PutAsJsonAsync(
            $"/api/journey/frequencies/segments/{segment}", new FrequencyRequest(1, 7));

        await admin.PutAsJsonAsync(
            $"/api/journey/calendars/{subject}",
            new WorkingCalendarRequest([DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday], 10));

        return outletIds;
    }
}
