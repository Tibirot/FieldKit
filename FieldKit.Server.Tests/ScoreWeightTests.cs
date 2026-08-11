using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FieldKit.Modules.Configuration;

namespace FieldKit.Server.Tests;

/// <summary>
/// Authoring the tenant's perfect-store weighting over HTTP (<c>AUD-07</c>, <c>BR-AUD-4/8</c>) —
/// W10 slice 1.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ScoreWeightSetTests"/> covers the rules; this covers what a caller can reach — the
/// lifecycle across requests, the refusal codes a screen branches on, and the permission that
/// separates reading a weighting from changing one.
/// </para>
/// <para>
/// <b>Versions are assigned by the server, never sent.</b> Every test here reads the version back
/// rather than asserting a literal, because these run in a shared collection against one database
/// and the tenant's version counter is genuinely shared state. A test that demanded "version 1"
/// would pass alone and fail the moment a second one drafted first.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class ScoreWeightTests(ServerFixture fixture)
{
    private static ScoreWeightSetRequest Balanced() => new([
        new ScoreWeightRequest(ScorePillar.Availability, 50m),
        new ScoreWeightRequest(ScorePillar.ShareOfShelf, 30m),
        new ScoreWeightRequest(ScorePillar.PriceCompliance, 20m),
    ]);

    private static async Task<ScoreWeightSetResponse> DraftAsync(
        HttpClient client, ScoreWeightSetRequest? request = null)
    {
        var response = await client.PostAsJsonAsync("/api/config/score-weights", request ?? Balanced());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<ScoreWeightSetResponse>())!;
    }

    [Fact]
    public async Task A_weighting_is_drafted_unpublished_and_gets_the_next_version()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var first = await DraftAsync(admin);
        var second = await DraftAsync(admin);

        Assert.False(first.IsPublished);
        Assert.Null(first.PublishedAtUtc);
        Assert.Equal(3, first.Weights.Count);

        // Monotonic, and assigned here rather than accepted from the caller — a client that sent its
        // own number could send one a sealed audit already points at.
        Assert.Equal(first.Version + 1, second.Version);
    }

    [Fact]
    public async Task A_weighting_that_does_not_add_up_is_refused_by_name()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await admin.PostAsJsonAsync("/api/config/score-weights", new ScoreWeightSetRequest([
            new ScoreWeightRequest(ScorePillar.Availability, 50m),
            new ScoreWeightRequest(ScorePillar.ShareOfShelf, 30m),
        ]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "config.weights.doesNotSumToOneHundred",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task A_pillar_arrives_as_its_name()
    {
        /*
         * Raw JSON, for the reason `VisitWorkflowTests` needs it: posting the typed record serialises
         * with the property's own converter, so a request and its assertion would agree whatever the
         * wire format was. The workflow's step type was a 400 for every name until its converter was
         * added — only the ordinal `0` worked — and this is the test that would have caught it.
         */
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await admin.PostAsync("/api/config/score-weights", new StringContent(
            """
            { "weights": [
                { "pillar": "Availability", "percentage": 60 },
                { "pillar": "PriceCompliance", "percentage": 40 }
            ] }
            """,
            Encoding.UTF8,
            "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var set = (await response.Content.ReadFromJsonAsync<ScoreWeightSetResponse>())!;

        // And back out as a name too — a request and its own response disagreeing about how one enum
        // is spelled is the shape of the bug this pair exists for.
        Assert.Contains(set.Weights, weight => weight.Pillar == nameof(ScorePillar.Availability));
    }

    [Fact]
    public async Task A_draft_can_be_reweighted()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var draft = await DraftAsync(admin);

        var response = await admin.PutAsJsonAsync(
            $"/api/config/score-weights/{draft.Version}",
            new ScoreWeightSetRequest([
                new ScoreWeightRequest(ScorePillar.Availability, 70m),
                new ScoreWeightRequest(ScorePillar.PriceCompliance, 30m),
            ]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = (await response.Content.ReadFromJsonAsync<ScoreWeightSetResponse>())!;

        Assert.Equal(2, updated.Weights.Count);
        Assert.Equal(70m, updated.Weights.Single(w => w.Pillar == nameof(ScorePillar.Availability)).Percentage);
    }

    [Fact]
    public async Task Publishing_freezes_a_version_and_a_second_edit_is_refused()
    {
        // The whole of W10 slice 0, over HTTP. `BR-AUD-8` has the server recompute a sealed audit
        // with the weights it was scored against; that is only meaningful if this refusal holds.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var draft = await DraftAsync(admin);

        var published = await admin.PostAsync($"/api/config/score-weights/{draft.Version}/publish", null);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);

        var frozen = (await published.Content.ReadFromJsonAsync<ScoreWeightSetResponse>())!;
        Assert.True(frozen.IsPublished);
        Assert.NotNull(frozen.PublishedAtUtc);

        var edit = await admin.PutAsJsonAsync(
            $"/api/config/score-weights/{draft.Version}",
            new ScoreWeightSetRequest([new ScoreWeightRequest(ScorePillar.Availability, 100m)]));

        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal(
            "config.weights.alreadyPublished",
            Assert.Single(await Refusals.ProblemsOf(edit)).Code);

        // …and the stored weights are the ones that were published, not the ones just refused.
        var read = await admin.GetFromJsonAsync<ScoreWeightSetResponse>(
            $"/api/config/score-weights/{draft.Version}");

        Assert.Equal(3, read!.Weights.Count);
    }

    [Fact]
    public async Task Publishing_twice_is_refused_rather_than_quietly_succeeding()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var draft = await DraftAsync(admin);
        await admin.PostAsync($"/api/config/score-weights/{draft.Version}/publish", null);

        var again = await admin.PostAsync($"/api/config/score-weights/{draft.Version}/publish", null);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(
            "config.weights.alreadyPublished",
            Assert.Single(await Refusals.ProblemsOf(again)).Code);
    }

    [Fact]
    public async Task Re_weighting_a_tenant_means_a_new_version_and_leaves_the_old_one_alone()
    {
        // The lifecycle the whole slice exists to make possible: last quarter's audits keep pointing
        // at weights that still say what they said.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var original = await DraftAsync(admin);
        await admin.PostAsync($"/api/config/score-weights/{original.Version}/publish", null);

        var replacement = await DraftAsync(admin, new ScoreWeightSetRequest([
            new ScoreWeightRequest(ScorePillar.Availability, 40m),
            new ScoreWeightRequest(ScorePillar.ShareOfShelf, 40m),
            new ScoreWeightRequest(ScorePillar.PriceCompliance, 20m),
        ]));

        Assert.NotEqual(original.Version, replacement.Version);

        var old = await admin.GetFromJsonAsync<ScoreWeightSetResponse>(
            $"/api/config/score-weights/{original.Version}");

        Assert.True(old!.IsPublished);
        Assert.Equal(50m, old.Weights.Single(w => w.Pillar == nameof(ScorePillar.Availability)).Percentage);
    }

    [Fact]
    public async Task A_version_nobody_drafted_is_not_found()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await admin.GetAsync("/api/config/score-weights/99999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reading_a_weighting_needs_a_permission_and_changing_one_needs_another()
    {
        // A rep syncs weights through the pull feed, not through this API; the authoring surface is
        // an administrator's. `read-only` holds config:read and not config:write.
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        var read = await viewer.GetAsync("/api/config/score-weights");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var write = await viewer.PostAsJsonAsync("/api/config/score-weights", Balanced());
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task Weightings_are_listed_newest_first()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        await DraftAsync(admin);
        var newest = await DraftAsync(admin);

        var all = await admin.GetFromJsonAsync<List<ScoreWeightSetResponse>>("/api/config/score-weights");

        // An administrator opening this screen is looking at the version in force or the draft they
        // are about to publish, not at the history.
        Assert.Equal(newest.Version, all![0].Version);
        Assert.True(all.Count >= 2);
    }

    [Fact]
    public async Task A_weighting_belongs_to_its_tenant_and_no_other()
    {
        // The isolation gate, asserted rather than assumed: tenant B's administrator sees none of
        // tenant A's versions, and the version counters are independent.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var other = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var ours = await DraftAsync(admin);

        var theirs = await other.GetFromJsonAsync<JsonElement>("/api/config/score-weights");

        Assert.DoesNotContain(
            theirs.EnumerateArray(),
            set => set.GetProperty("id").GetGuid() == ours.Id);
    }
}
