using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Journey;
using FieldKit.Modules.Org;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Visit;

namespace FieldKit.Server.Tests;

/// <summary>
/// A visit claiming a planned call has to be claiming a real one (<c>JRN-04</c>) — W7 slice 9b.
/// </summary>
/// <remarks>
/// <para>
/// <c>IJourneyQuery</c>'s whole reason to exist. Check-in has carried a <c>plannedVisitId</c> since
/// slice 7 and took it entirely on trust: nothing in the system would have noticed a fabricated one
/// until it reached a coverage report, where it reads as a call that was made.
/// </para>
/// <para>
/// The assertions are mostly about <b>misses that all look the same</b>. No such call, another rep's
/// call, the right call at the wrong shop and a call on a draft plan are one refusal, so a caller
/// cannot turn check-in into an oracle for what is on somebody else's round.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class PlannedCallTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    private static readonly Coordinates Shop = new(44.4638, 26.0946);

    /// <summary>A Monday, and four whole weekly cycles.</summary>
    private static readonly DateOnly From = new(2028, 5, 1);
    private static readonly DateOnly To = new(2028, 5, 28);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    /// <summary>
    /// The subject the admin token carries — the rep check-in will believe.
    /// </summary>
    /// <remarks>
    /// Check-in takes the rep from the token and never from the body (<c>VIS-01</c>), so a plan this
    /// visit can claim has to be a plan for <i>that</i> subject. Reading it out of the very token the
    /// fixture authenticates with is the only way to line the two up without minting one.
    /// </remarks>
    private static string SubjectOf(string accessToken) =>
        new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims
            .Single(claim => claim.Type is "sub").Value;

    /// <summary>Makes sure IAM has this subject — plan generation refuses a user it does not know.</summary>
    private static async Task EnsureUserAsync(HttpClient client, string subjectId)
    {
        var existing = await client.GetFromJsonAsync<List<UserResponse>>("/api/iam/users");

        if (existing!.Any(user => user.SubjectId == subjectId)) return;

        var roles = await client.GetFromJsonAsync<List<RoleResponse>>("/api/iam/roles");

        var created = await client.PostAsJsonAsync("/api/iam/users", new
        {
            subjectId,
            email = $"{Guid.NewGuid():N}@fieldkit.local",
            displayName = "Fixture Admin",
            locale = "en-GB",
            timeZone = Zone,
            roleIds = new[] { roles!.First(role => role.IsSystemTemplate).Id },
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    private static async Task<Guid> OutletAsync(HttpClient client, Guid channelId) =>
        (await (await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(
                Unique("OUT"), "Corner Shop", channelId, Zone, Unique("SEG")[..8], Location: Shop)))
            .Content.ReadFromJsonAsync<OutletResponse>())!.Id;

    /// <summary>
    /// A published plan for <paramref name="userId"/> with exactly one weekly outlet, and the id of
    /// its first call.
    /// </summary>
    private async Task<(Guid OutletId, Guid PlannedVisitId, Guid PlanId)> PlannedCallAsync(
        HttpClient client, string userId, bool publish = true)
    {
        await EnsureUserAsync(client, userId);

        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));
        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var segment = Unique("SEG")[..8];

        var created = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(
                Unique("OUT"), "Corner Shop", channelId, Zone, segment, Location: Shop));

        var outletId = (await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id;

        var unit = await client.PostAsJsonAsync("/api/org/units", new OrgUnitRequest(Unique("Unit")));
        var unitId = (await unit.Content.ReadFromJsonAsync<OrgUnitResponse>())!.Id;

        var territory = await client.PostAsJsonAsync(
            "/api/org/territories", new TerritoryRequest(Unique("Terr"), unitId));
        var territoryId = (await territory.Content.ReadFromJsonAsync<TerritoryResponse>())!.Id;

        await client.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/outlets", new AssignOutletsRequest([outletId]));

        await client.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/assignments",
            new RepAssignmentRequest(userId, From, To));

        await client.PutAsJsonAsync(
            $"/api/journey/frequencies/segments/{segment}", new FrequencyRequest(1, 7));

        await client.PutAsJsonAsync(
            $"/api/journey/calendars/{userId}",
            new WorkingCalendarRequest([DayOfWeek.Monday, DayOfWeek.Wednesday], 10));

        var generated = await client.PostAsJsonAsync(
            "/api/journey/plans", new GeneratePlanRequest(userId, From, To));

        Assert.Equal(HttpStatusCode.Created, generated.StatusCode);

        var body = await generated.Content.ReadFromJsonAsync<JsonElement>();
        var planId = body.GetProperty("plan").GetProperty("id").GetGuid();

        if (publish)
        {
            var published = await client.PostAsync($"/api/journey/plans/{planId}/publish", null);
            Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        }

        var detail = await client.GetFromJsonAsync<JourneyPlanDetailResponse>(
            $"/api/journey/plans/{planId}");

        // The call for *this* outlet, not the first on the plan. The rep is the token's subject and
        // every test in this class uses it, so by the third test the rep covers several territories
        // and the plan opens with somebody else's shop — which is exactly the mismatch these tests
        // are about, arriving from the wrong direction.
        return (outletId, detail!.Visits.First(visit => visit.OutletId == outletId).Id, planId);
    }

    private static Task<HttpResponseMessage> CheckInAsync(
        HttpClient client, Guid outletId, Guid? plannedVisitId) =>
        client.PostAsJsonAsync(
            "/api/visits/check-in",
            new CheckInRequest(
                outletId, Shop.Latitude, Shop.Longitude, PlannedVisitId: plannedVisitId));

    [Fact]
    public async Task A_rep_works_a_call_that_is_on_their_round()
    {
        using var client = Admin();

        var me = SubjectOf(fixture.AdminAccessToken);
        var (outletId, plannedVisitId, _) = await PlannedCallAsync(client, me);

        var response = await CheckInAsync(client, outletId, plannedVisitId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var visit = (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;

        Assert.Equal(plannedVisitId, visit.PlannedVisitId);
    }

    [Fact]
    public async Task An_unplanned_call_is_still_ordinary()
    {
        // JRN-06. The contract is asked nothing when there is nothing to ask about, and a shop the
        // rep walked past is a visit like any other.
        using var client = Admin();

        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));
        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var response = await CheckInAsync(client, await OutletAsync(client, channelId), null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null((await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit.PlannedVisitId);
    }

    [Fact]
    public async Task A_planned_call_nobody_planned_is_refused()
    {
        // The bug this slice exists to close. Before it, this stored a visit that claimed a call
        // that never existed, and the first thing to notice would have been a coverage report.
        using var client = Admin();

        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));
        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var refused = await CheckInAsync(client, await OutletAsync(client, channelId), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(
            "visit.checkIn.unknownPlannedCall",
            Assert.Single(await Refusals.ProblemsOf(refused)).Code);
    }

    [Fact]
    public async Task Somebody_elses_call_is_refused_the_same_way()
    {
        using var client = Admin();

        var somebodyElse = Guid.NewGuid().ToString();
        var (outletId, theirCall, _) = await PlannedCallAsync(client, somebodyElse);

        var refused = await CheckInAsync(client, outletId, theirCall);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(
            "visit.checkIn.unknownPlannedCall",
            Assert.Single(await Refusals.ProblemsOf(refused)).Code);
    }

    [Fact]
    public async Task The_right_call_at_the_wrong_shop_is_refused()
    {
        // A rep who has their own call in front of them and checks in at the shop next door. The
        // call is real and theirs, and it still is not this visit's.
        using var client = Admin();

        var me = SubjectOf(fixture.AdminAccessToken);
        var (_, plannedVisitId, _) = await PlannedCallAsync(client, me);

        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));
        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var refused = await CheckInAsync(
            client, await OutletAsync(client, channelId), plannedVisitId);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(
            "visit.checkIn.unknownPlannedCall",
            Assert.Single(await Refusals.ProblemsOf(refused)).Code);
    }

    [Fact]
    public async Task A_call_on_a_draft_plan_is_refused_until_it_is_published()
    {
        // A draft is a supervisor's experiment, and the next generation run replaces it wholesale —
        // a visit anchored to a draft call would point at a row that is about to stop existing.
        using var client = Admin();

        var me = SubjectOf(fixture.AdminAccessToken);
        var (outletId, plannedVisitId, planId) = await PlannedCallAsync(client, me, publish: false);

        var refused = await CheckInAsync(client, outletId, plannedVisitId);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(
            "visit.checkIn.unknownPlannedCall",
            Assert.Single(await Refusals.ProblemsOf(refused)).Code);

        await client.PostAsync($"/api/journey/plans/{planId}/publish", null);

        var accepted = await CheckInAsync(client, outletId, plannedVisitId);

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    [Fact]
    public async Task A_call_the_rep_already_reported_as_missed_can_still_be_worked()
    {
        // A rep who found the shutters down, said so, and got in an hour later. BR-JRN-2 keeps the
        // skipped call on the plan rather than deleting it, and refusing the visit here would make
        // the earlier honesty cost them the record of the work they then did.
        using var client = Admin();

        var me = SubjectOf(fixture.AdminAccessToken);
        var (outletId, plannedVisitId, planId) = await PlannedCallAsync(client, me);

        var marked = await client.PostAsJsonAsync(
            $"/api/journey/plans/{planId}/visits/{plannedVisitId}/not-visited",
            new NotVisitedRequest("Shutters down, no answer"));

        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);

        var response = await CheckInAsync(client, outletId, plannedVisitId);

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);
    }

    [Fact]
    public async Task Another_tenant_cannot_claim_this_tenants_call()
    {
        // Belt and braces over the tenant filter: the contract's query runs inside the caller's
        // tenant, so another tenant's check-in sees no plan at all — and it is refused before the
        // outlet check would have refused it, so this asserts the order as much as the rule.
        using var client = Admin();
        using var otherTenant = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var me = SubjectOf(fixture.AdminAccessToken);
        var (_, plannedVisitId, _) = await PlannedCallAsync(client, me);

        var channel = await otherTenant.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));
        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var refused = await CheckInAsync(
            otherTenant, await OutletAsync(otherTenant, channelId), plannedVisitId);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(
            "visit.checkIn.unknownPlannedCall",
            Assert.Single(await Refusals.ProblemsOf(refused)).Code);
    }
}
