using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Visit;

namespace FieldKit.Server.Tests;

/// <summary>
/// Starting a visit (<c>VIS-01</c>, <c>VIS-02</c>, <c>BR-VIS-2</c>) — W7 slice 7.
/// </summary>
/// <remarks>
/// <para>
/// The rules themselves are pinned by <see cref="GeofencingTests"/>, which needs no database. What is
/// asserted here is everything a pure function cannot see: that the rep on the visit comes from the
/// token, that the geofence comes from the outlet and the presence policy from its channel, and that
/// the refusal a rep meets when they are somewhere else is a <i>question</i> — one that lets the
/// visit through the moment it is answered.
/// </para>
/// <para>
/// The check-in route is the first write in the system a field rep holds themselves rather than
/// inherits from an administrator, so the permission it sits behind is asserted too.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class VisitCheckInTests(ServerFixture fixture)
{
    private const string CheckIn = "/api/visits/check-in";
    private const string Zone = "Europe/Bucharest";

    /// <summary>A shop on Calea Dorobanți, Bucharest, and a doorway to stand in.</summary>
    private static readonly Coordinates Shop = new(44.4638, 26.0946);

    /// <summary>Roughly 2.2 km north of it — outside any plausible radius.</summary>
    private static readonly Coordinates ElseWhere = new(44.4838, 26.0946);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    private static async Task<Guid> ChannelAsync(HttpClient client, bool presenceExpected = true)
    {
        var created = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        var channelId = (await created.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        if (!presenceExpected)
        {
            var set = await client.PutAsJsonAsync(
                $"/api/config/visit-workflows/{channelId}",
                new VisitWorkflowRequest(PresenceExpected: false, []));

            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        }

        return channelId;
    }

    private static async Task<Guid> OutletAsync(
        HttpClient client, Coordinates? at, bool presenceExpected = true)
    {
        var channelId = await ChannelAsync(client, presenceExpected);

        var created = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, Location: at));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    [Fact]
    public async Task A_rep_standing_in_the_shop_checks_in()
    {
        using var client = Admin();

        var outletId = await OutletAsync(client, Shop);

        var response = await client.PostAsJsonAsync(
            CheckIn, new CheckInRequest(outletId, Shop.Latitude, Shop.Longitude));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var visit = (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;

        Assert.Equal("InProgress", visit.Status);
        Assert.Equal(outletId, visit.OutletId);
        Assert.True(visit.WasInsideGeofence);
        Assert.Null(visit.GeofenceOverrideReason);
        Assert.Equal(0, visit.CheckInDistanceMetres!.Value, 3);

        // Worked here, not drained from a phone (W9 slice 0). Asserted on the online path as well as
        // the offline one, because a `Source` that is only ever written by the ingest service would
        // make every live visit indistinguishable from a pre-W9 row.
        Assert.Equal(nameof(VisitSource.Live), visit.Source);

        // And the server stored it as it started it — the online path is the case where the two
        // timestamps agree, which is what makes the offline one's disagreement mean something.
        Assert.NotNull(visit.RecordedAtUtc);
        Assert.True(
            (visit.RecordedAtUtc.Value - visit.CheckedInAtUtc).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task The_visit_belongs_to_whoever_the_token_says_it_does()
    {
        // VIS-01. The request has no field for the rep, and this is the assertion that keeps it that
        // way: a visit is a statement about who was where, and a caller able to name somebody else is
        // a caller able to name the wrong person.
        using var client = Admin();

        var outletId = await OutletAsync(client, Shop);

        var response = await client.PostAsJsonAsync(
            CheckIn, new CheckInRequest(outletId, Shop.Latitude, Shop.Longitude));

        var visit = (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;

        Assert.False(string.IsNullOrWhiteSpace(visit.UserId));

        // Sending a rep anyway changes nothing — the extra property is ignored, not honoured.
        var impersonated = await client.PostAsJsonAsync(CheckIn, new
        {
            outletId,
            latitude = Shop.Latitude,
            longitude = Shop.Longitude,
            userId = "somebody-else",
        });

        var second = (await impersonated.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;

        Assert.Equal(visit.UserId, second.UserId);
    }

    [Fact]
    public async Task A_rep_somewhere_else_is_asked_why_rather_than_turned_away()
    {
        // BR-VIS-2, and the assertion the whole module hangs on. The 400 is a question: it names the
        // field to fill in, and carries the distance so the rep can see what the system thinks.
        using var client = Admin();

        var outletId = await OutletAsync(client, Shop);

        var refused = await client.PostAsJsonAsync(
            CheckIn, new CheckInRequest(outletId, ElseWhere.Latitude, ElseWhere.Longitude));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        var error = problem.GetProperty("errors")[0];

        Assert.Equal("overrideReason", error.GetProperty("field").GetString());
        Assert.Equal("visit.checkIn.overrideReasonRequired", error.GetProperty("code").GetString());

        var distance = double.Parse(error.GetProperty("args").GetProperty("distanceMetres").GetString()!);
        Assert.InRange(distance, 2_000, 2_400);
        Assert.Equal("150", error.GetProperty("args").GetProperty("radiusMetres").GetString());
    }

    [Fact]
    public async Task Saying_why_lets_the_same_check_in_through()
    {
        // The other half of BR-VIS-2, and the half that is easy to lose: the refusal above must not
        // become a wall. Same outlet, same position, one sentence added — and the visit starts.
        using var client = Admin();

        var outletId = await OutletAsync(client, Shop);

        var response = await client.PostAsJsonAsync(
            CheckIn,
            new CheckInRequest(
                outletId, ElseWhere.Latitude, ElseWhere.Longitude,
                OverrideReason: "Owner asked me to meet him at the depot"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var visit = (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;

        Assert.False(visit.WasInsideGeofence);
        Assert.Equal("Owner asked me to meet him at the depot", visit.GeofenceOverrideReason);

        // And the number is kept, not just the verdict — a supervisor reviewing this sees how far.
        Assert.InRange(visit.CheckInDistanceMetres!.Value, 2_000, 2_400);
    }

    [Fact]
    public async Task A_reason_nobody_needed_is_not_recorded()
    {
        // Otherwise "how many overrides this month" counts typing rather than exceptions, and the
        // one number a supervisor uses to spot a problem stops meaning anything.
        using var client = Admin();

        var outletId = await OutletAsync(client, Shop);

        var response = await client.PostAsJsonAsync(
            CheckIn,
            new CheckInRequest(
                outletId, Shop.Latitude, Shop.Longitude, OverrideReason: "Just in case"));

        var visit = (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;

        Assert.True(visit.WasInsideGeofence);
        Assert.Null(visit.GeofenceOverrideReason);
    }

    [Fact]
    public async Task A_channel_that_does_not_expect_presence_asks_nothing()
    {
        // The reason IVisitWorkflow was built a slice early. A telesales call is legitimately not at
        // the outlet; demanding a reason would record an exception every single time.
        using var client = Admin();

        var outletId = await OutletAsync(client, Shop, presenceExpected: false);

        var response = await client.PostAsJsonAsync(
            CheckIn, new CheckInRequest(outletId, ElseWhere.Latitude, ElseWhere.Longitude));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var visit = (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;

        Assert.Null(visit.GeofenceOverrideReason);

        // The position is still recorded. Not asking for an explanation is not the same as not
        // looking — VIS-02 captures where the rep was either way.
        Assert.Equal(ElseWhere.Latitude, visit.CheckInLatitude);
    }

    [Fact]
    public async Task An_outlet_nobody_has_placed_asks_nothing_either()
    {
        // Coordinates are optional on an outlet (OUT-01). Making a rep explain a gap in master data
        // would blame them for it.
        using var client = Admin();

        var outletId = await OutletAsync(client, at: null);

        var response = await client.PostAsJsonAsync(
            CheckIn, new CheckInRequest(outletId, Shop.Latitude, Shop.Longitude));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var visit = (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;

        Assert.Null(visit.CheckInDistanceMetres);
        Assert.Null(visit.GeofenceOverrideReason);
        Assert.False(visit.WasInsideGeofence);
    }

    [Fact]
    public async Task A_device_with_no_fix_has_to_explain_itself()
    {
        // The one case where nothing can be measured and a reason is still required — and it is also
        // how a check-in would be faked, so it must not be the easy way through.
        using var client = Admin();

        var outletId = await OutletAsync(client, Shop);

        var refused = await client.PostAsJsonAsync(CheckIn, new CheckInRequest(outletId));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("visit.checkIn.overrideReasonRequired", Assert.Single(await Refusals.ProblemsOf(refused)).Code);

        var accepted = await client.PostAsJsonAsync(
            CheckIn, new CheckInRequest(outletId, OverrideReason: "No signal inside the mall"));

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);

        var visit = (await accepted.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;

        Assert.Null(visit.CheckInLatitude);
        Assert.Null(visit.CheckInDistanceMetres);
        Assert.Equal("No signal inside the mall", visit.GeofenceOverrideReason);
    }

    [Fact]
    public async Task Half_a_position_is_refused()
    {
        using var client = Admin();

        var outletId = await OutletAsync(client, Shop);

        var refused = await client.PostAsJsonAsync(
            CheckIn, new CheckInRequest(outletId, Latitude: Shop.Latitude));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("visit.checkIn.halfPosition", Assert.Single(await Refusals.ProblemsOf(refused)).Code);
    }

    [Theory]
    [InlineData(91d, 26d, "visit.checkIn.latitudeOutOfRange")]
    [InlineData(44d, 181d, "visit.checkIn.longitudeOutOfRange")]
    public async Task A_position_that_is_not_on_the_earth_is_refused(
        double latitude, double longitude, string code)
    {
        using var client = Admin();

        var outletId = await OutletAsync(client, Shop);

        var refused = await client.PostAsJsonAsync(
            CheckIn, new CheckInRequest(outletId, latitude, longitude));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(code, Assert.Single(await Refusals.ProblemsOf(refused)).Code);
    }

    [Fact]
    public async Task An_outlet_from_another_tenant_is_no_outlet_at_all()
    {
        // The tenant filter is on the geofence query, and "no such outlet" is what it must look like
        // from here — an existence check that answered differently would leak the id.
        using var otherTenant = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);
        using var client = Admin();

        var theirs = await OutletAsync(otherTenant, Shop);

        var refused = await client.PostAsJsonAsync(
            CheckIn, new CheckInRequest(theirs, Shop.Latitude, Shop.Longitude));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("visit.checkIn.unknownOutlet", Assert.Single(await Refusals.ProblemsOf(refused)).Code);
    }

    [Fact]
    public async Task An_unknown_outlet_is_refused_before_anything_is_written()
    {
        using var client = Admin();

        var refused = await client.PostAsJsonAsync(
            CheckIn, new CheckInRequest(Guid.NewGuid(), Shop.Latitude, Shop.Longitude));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("visit.checkIn.unknownOutlet", Assert.Single(await Refusals.ProblemsOf(refused)).Code);
    }

    [Fact]
    public async Task Reading_visits_needs_permission_and_checking_in_needs_more()
    {
        // The first rep-held write in the system. The viewer has visit:read, so this separates the
        // two rather than testing that an unprivileged caller is refused everything.
        using var admin = Admin();
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        var outletId = await OutletAsync(admin, Shop);

        var created = await admin.PostAsJsonAsync(
            CheckIn, new CheckInRequest(outletId, Shop.Latitude, Shop.Longitude));

        var visit = (await created.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;

        var read = await viewer.GetAsync($"/api/visits/{visit.Id}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var written = await viewer.PostAsJsonAsync(
            CheckIn, new CheckInRequest(outletId, Shop.Latitude, Shop.Longitude));

        Assert.Equal(HttpStatusCode.Forbidden, written.StatusCode);
    }

    [Fact]
    public async Task A_visit_is_only_visible_to_the_tenant_that_recorded_it()
    {
        using var client = Admin();
        using var otherTenant = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var outletId = await OutletAsync(client, Shop);

        var created = await client.PostAsJsonAsync(
            CheckIn, new CheckInRequest(outletId, Shop.Latitude, Shop.Longitude));

        var visit = (await created.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;

        var read = await otherTenant.GetAsync($"/api/visits/{visit.Id}");

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    [Fact]
    public async Task A_visit_can_fulfil_a_planned_call_or_no_call_at_all()
    {
        // JRN-06's unplanned call: no plan is ever invented to hang a visit off, and a shop the rep
        // walked past is an ordinary visit.
        using var client = Admin();

        var outletId = await OutletAsync(client, Shop);

        var response = await client.PostAsJsonAsync(
            CheckIn, new CheckInRequest(outletId, Shop.Latitude, Shop.Longitude));

        Assert.Null((await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit.PlannedVisitId);

        // A planned call it cannot claim is refused rather than stored. This assertion used to run
        // the other way — until W7 slice 9b the id was taken on trust, and a fabricated one would
        // have surfaced first in a coverage report as a call that was made. What a *claimable* call
        // looks like is PlannedCallTests, which needs a published plan to make one.
        var refused = await client.PostAsJsonAsync(
            CheckIn,
            new CheckInRequest(
                outletId, Shop.Latitude, Shop.Longitude, PlannedVisitId: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(
            "visit.checkIn.unknownPlannedCall",
            Assert.Single(await Refusals.ProblemsOf(refused)).Code);
    }

    [Fact]
    public async Task A_reason_longer_than_the_column_is_refused_rather_than_truncated()
    {
        using var client = Admin();

        var outletId = await OutletAsync(client, Shop);

        var refused = await client.PostAsJsonAsync(
            CheckIn,
            new CheckInRequest(
                outletId, ElseWhere.Latitude, ElseWhere.Longitude,
                OverrideReason: new string('x', Visit.MaximumOverrideReasonLength + 1)));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("visit.checkIn.reasonTooLong", Assert.Single(await Refusals.ProblemsOf(refused)).Code);
    }
}
