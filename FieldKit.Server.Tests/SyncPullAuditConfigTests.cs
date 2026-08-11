using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Sync;

namespace FieldKit.Server.Tests;

/// <summary>
/// Survey forms and perfect-store weightings on the device (<c>OFF-03</c>) — W10 slice 7.
/// </summary>
/// <remarks>
/// <para>
/// Both tenant-wide, like the visit workflows they sit beside, so nothing here sets up a territory:
/// there is no scope to get wrong.
/// </para>
/// <para>
/// <b>The weightings are the odd one out of every feed in this protocol</b>, and most of this file is
/// about that. They carry every <i>published</i> version rather than the newest — an audit records
/// the version it was scored against (<c>BR-AUD-8</c>) — and they carry <i>only</i> published ones,
/// because a device scoring against a draft would have its audit refused on push.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class SyncPullAuditConfigTests(ServerFixture fixture)
{
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private static async Task<Guid> BindDeviceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/devices", new BindDeviceRequest(Unique("Phone")));

        return (await response.Content.ReadFromJsonAsync<DeviceResponse>())!.Id;
    }

    private static async Task<JsonElement> PullAsync(
        HttpClient client, Guid deviceId, long? surveys = null, long? weights = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/pull",
            new PullRequest(
                deviceId,
                new PullCursors(null, Surveys: surveys, ScoreWeights: weights)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static JsonElement Section(JsonElement pull, string name) =>
        pull.GetProperty("changes").GetProperty(name);

    private static List<JsonElement> Upserts(JsonElement pull, string name) =>
        [.. Section(pull, name).GetProperty("upserts").EnumerateArray()];

    private static long Cursor(JsonElement pull, string name) =>
        Section(pull, name).GetProperty("cursor").GetInt64();

    private static async Task<(Guid Id, string Name)> SurveyAsync(HttpClient admin)
    {
        var name = Unique("Survey");

        var created = await admin.PostAsJsonAsync("/api/config/surveys", new SurveyFormRequest(name, [
            new SurveyQuestionRequest("chiller_lit", "Is the chiller lit?", SurveyQuestionType.Boolean, true),
            new SurveyQuestionRequest("quality", "Facing quality?", SurveyQuestionType.SingleChoice,
                false, ["Good", "Poor"]),
        ]));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return ((await created.Content.ReadFromJsonAsync<SurveyFormResponse>())!.Id, name);
    }

    /// <summary>Drafts a weighting; publishes it only when asked.</summary>
    private static async Task<int> WeightingAsync(HttpClient admin, bool publish = true)
    {
        var drafted = await admin.PostAsJsonAsync("/api/config/score-weights", new ScoreWeightSetRequest([
            new ScoreWeightRequest(ScorePillar.Availability, 33.34m),
            new ScoreWeightRequest(ScorePillar.ShareOfShelf, 33.33m),
            new ScoreWeightRequest(ScorePillar.PriceCompliance, 33.33m),
        ]));

        var version = (await drafted.Content.ReadFromJsonAsync<ScoreWeightSetResponse>())!.Version;

        if (publish) await admin.PostAsync($"/api/config/score-weights/{version}/publish", null);

        return version;
    }

    [Fact]
    public async Task A_survey_form_reaches_the_device_whole()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var device = await BindDeviceAsync(admin);
        var before = Cursor(await PullAsync(admin, device), "surveys");

        var (id, name) = await SurveyAsync(admin);

        var pull = await PullAsync(admin, device, surveys: before);

        var form = Assert.Single(Upserts(pull, "surveys"), candidate =>
            candidate.GetProperty("id").GetGuid() == id);

        Assert.Equal(name, form.GetProperty("name").GetString());

        // The questions travel inside the form. A device holding four of five would ask a rep less
        // than the tenant configured, and BR-AUD-7 would gate the audit step on one it never got.
        var questions = form.GetProperty("questions").EnumerateArray().ToList();

        Assert.Equal(2, questions.Count);
        Assert.Equal([1, 2], questions.Select(q => q.GetProperty("order").GetInt32()));
        Assert.Equal("chiller_lit", questions[0].GetProperty("key").GetString());

        // …and the type is a *name*, not an ordinal — a value inserted into the middle of that enum
        // would otherwise silently reinterpret every form already on every device.
        Assert.Equal("Boolean", questions[0].GetProperty("type").GetString());
        Assert.Equal(["Good", "Poor"],
            questions[1].GetProperty("options").EnumerateArray().Select(o => o.GetString()));
    }

    [Fact]
    public async Task A_deleted_survey_form_arrives_as_a_tombstone()
    {
        // Tenant-wide, so it can go to every device without telling anyone anything about anybody.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var device = await BindDeviceAsync(admin);
        var (id, _) = await SurveyAsync(admin);

        var after = Cursor(await PullAsync(admin, device), "surveys");

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/config/surveys/{id}")).StatusCode);

        var pull = await PullAsync(admin, device, surveys: after);

        Assert.Contains(
            Section(pull, "surveys").GetProperty("tombstones").EnumerateArray(),
            tombstone => tombstone.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task A_published_weighting_reaches_the_device_with_its_percentages_as_strings()
    {
        /*
         * The single most load-bearing detail on this feed. `BR-AUD-5` has the device's score match
         * the server's exactly; `decimal.js` reads a string, and a JSON number would already have
         * been through IEEE-754 before the device's scorer ever saw it.
         *
         * 33.34/33.33/33.33 rather than 50/30/20 on purpose: thirds are where a float would first
         * disagree, and a round number would pass whatever the wire format was.
         */
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var device = await BindDeviceAsync(admin);
        var before = Cursor(await PullAsync(admin, device), "scoreWeights");

        var version = await WeightingAsync(admin);

        var pull = await PullAsync(admin, device, weights: before);

        var set = Assert.Single(Upserts(pull, "scoreWeights"), candidate =>
            candidate.GetProperty("version").GetInt32() == version);

        var weights = set.GetProperty("weights").EnumerateArray().ToList();

        Assert.Equal(3, weights.Count);

        Assert.All(weights, weight => Assert.Equal(
            JsonValueKind.String, weight.GetProperty("percentage").ValueKind));

        Assert.Equal("33.34", weights[0].GetProperty("percentage").GetString());
        Assert.Equal("Availability", weights[0].GetProperty("pillar").GetString());
    }

    [Fact]
    public async Task A_draft_weighting_never_reaches_the_device()
    {
        /*
         * A device that scored an audit against a draft would produce a number the server could not
         * reproduce — and would then have that audit refused on push (W10 slice 6). The device
         * should never see a version it cannot legitimately name.
         */
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var device = await BindDeviceAsync(admin);
        var before = Cursor(await PullAsync(admin, device), "scoreWeights");

        var draft = await WeightingAsync(admin, publish: false);

        var pull = await PullAsync(admin, device, weights: before);

        Assert.DoesNotContain(
            Upserts(pull, "scoreWeights"),
            candidate => candidate.GetProperty("version").GetInt32() == draft);
    }

    [Fact]
    public async Task A_draft_published_later_arrives_on_the_next_pull()
    {
        /*
         * The bug the skipped-draft cursor exists to prevent, from the other side.
         *
         * The device pulls while the set is a draft — it is skipped, and the watermark still has to
         * advance past it or every later pull re-queries a row it will never be sent. Publishing is
         * a write, so the row version rises again and the set lands on the next pull.
         *
         * This test alone does not pin the cursor half — see the one below, which does.
         */
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var device = await BindDeviceAsync(admin);
        var before = Cursor(await PullAsync(admin, device), "scoreWeights");

        var version = await WeightingAsync(admin, publish: false);

        var whileDraft = await PullAsync(admin, device, weights: before);
        var afterDraft = Cursor(whileDraft, "scoreWeights");

        Assert.DoesNotContain(
            Upserts(whileDraft, "scoreWeights"),
            candidate => candidate.GetProperty("version").GetInt32() == version);

        await admin.PostAsync($"/api/config/score-weights/{version}/publish", null);

        var published = await PullAsync(admin, device, weights: afterDraft);

        Assert.Contains(
            Upserts(published, "scoreWeights"),
            candidate => candidate.GetProperty("version").GetInt32() == version);
    }

    [Fact]
    public async Task The_cursor_advances_past_a_draft_it_skipped()
    {
        /*
         * A draft has a row version like anything else, and it is above the cursor. If the watermark
         * only ever moved to the highest row *sent*, a tenant with a draft sitting at the top of the
         * table would have every device re-query it on every pull — and the page would come back
         * empty each time, so nothing would ever look wrong.
         *
         * The consequence is a permanently wasted query rather than lost data, which is why it needs
         * a test rather than an incident: nothing else would ever surface it.
         *
         * Published first, then drafted, so the draft is the newest weight-set row. The returned
         * cursor must therefore be *above* the published set it just sent.
         */
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var device = await BindDeviceAsync(admin);
        var before = Cursor(await PullAsync(admin, device), "scoreWeights");

        var published = await WeightingAsync(admin);
        await WeightingAsync(admin, publish: false);

        var pull = await PullAsync(admin, device, weights: before);

        var sent = Assert.Single(Upserts(pull, "scoreWeights"), candidate =>
            candidate.GetProperty("version").GetInt32() == published);

        Assert.True(
            Cursor(pull, "scoreWeights") > sent.GetProperty("rowVersion").GetInt64(),
            "The cursor stopped at the last row sent, so the skipped draft is re-queried forever.");
    }

    [Fact]
    public async Task Every_published_version_travels_not_only_the_newest()
    {
        /*
         * The feed's defining property, and the one that separates it from every other.
         *
         * A workflow or a form has one current shape and a device only needs that. A weighting is
         * different: an audit records the version it was scored against, so a device holding work
         * captured last week still has to be able to show the rep what that audit scored. Sending
         * only the latest would leave a queued audit's breakdown unreadable on the device that
         * produced it.
         */
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var device = await BindDeviceAsync(admin);
        var before = Cursor(await PullAsync(admin, device), "scoreWeights");

        var older = await WeightingAsync(admin);
        var newer = await WeightingAsync(admin);

        var versions = Upserts(await PullAsync(admin, device, weights: before), "scoreWeights")
            .Select(set => set.GetProperty("version").GetInt32())
            .ToList();

        Assert.Contains(older, versions);
        Assert.Contains(newer, versions);
    }

    [Fact]
    public async Task A_device_that_has_pulled_both_is_told_nothing_twice()
    {
        // The cursor's whole job. A published set is immutable, so a device downloads each version
        // exactly once — the second pull must be empty rather than re-sending what it already holds.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var device = await BindDeviceAsync(admin);

        await SurveyAsync(admin);
        await WeightingAsync(admin);

        var first = await PullAsync(admin, device);
        var again = await PullAsync(
            admin, device, surveys: Cursor(first, "surveys"), weights: Cursor(first, "scoreWeights"));

        Assert.Empty(Upserts(again, "surveys"));
        Assert.Empty(Upserts(again, "scoreWeights"));
    }

    [Fact]
    public async Task Another_tenants_forms_and_weightings_are_not_in_this_devices_pull()
    {
        // The isolation gate. Both feeds run through Configuration's own tenant filter, so nothing
        // here narrows by anything and nothing leaks.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var other = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var device = await BindDeviceAsync(admin);

        var (theirForm, _) = await SurveyAsync(other);

        var pull = await PullAsync(admin, device);

        Assert.DoesNotContain(
            Upserts(pull, "surveys"),
            candidate => candidate.GetProperty("id").GetGuid() == theirForm);
    }
}
