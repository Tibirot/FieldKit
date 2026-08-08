using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Journey;
using FieldKit.Modules.Outlets;

namespace FieldKit.Server.Tests;

/// <summary>
/// Call frequency: the segment default, the outlet override, and which one wins (<c>JRN-01</c>).
/// </summary>
/// <remarks>
/// <para>
/// The ladder is the thing worth testing. <c>BR-PRD-2</c> resolves a price outlet → channel →
/// default and this resolves a frequency outlet → segment; both are "the most specific rule that
/// names this shop wins", and both fail in the same quiet way — by answering with the wrong rung and
/// looking entirely plausible.
/// </para>
/// <para>
/// The admin token is used throughout: it holds <c>journey:write</c> and <c>outlet:write</c>, which
/// is the pair a fixture needs. Note that this is the opposite of the Products tests, where the
/// admin deliberately holds no <c>product:*</c>.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class CallFrequencyTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private async Task<Guid> ChannelAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        return (await response.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    private async Task<Guid> OutletAsync(HttpClient client, string? segment = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(
                Unique("OUT"), "Corner Shop", await ChannelAsync(client), Zone, segment));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    private static async Task<List<ResolvedFrequencyResponse>> ResolveAsync(
        HttpClient client, params Guid[] outletIds)
    {
        var query = string.Join("&", outletIds.Select(id => $"outletId={id}"));

        return (await client.GetFromJsonAsync<List<ResolvedFrequencyResponse>>(
            $"/api/journey/frequencies/resolve?{query}"))!;
    }

    [Fact]
    public async Task An_outlet_takes_the_frequency_of_the_segment_it_is_in()
    {
        // The default, and the reason there is one: a tenant says "A-grade shops are visited weekly"
        // once rather than once per shop.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var segment = Unique("SEG");
        var outletId = await OutletAsync(client, segment);

        var set = await client.PutAsJsonAsync(
            $"/api/journey/frequencies/segments/{segment}", new FrequencyRequest(1, 7));

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        var resolved = Assert.Single(await ResolveAsync(client, outletId));

        Assert.Equal(1, resolved.VisitsPerCycle);
        Assert.Equal(7, resolved.CycleLengthDays);
        Assert.Equal(nameof(FrequencySource.Segment), resolved.Source);
    }

    [Fact]
    public async Task An_outlets_own_rule_beats_the_segment_it_is_in()
    {
        // The specificity ladder, and the case the whole feature exists for: the flagship in a
        // B-grade segment is visited weekly even though B-grade means monthly.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var segment = Unique("SEG");
        var outletId = await OutletAsync(client, segment);

        await client.PutAsJsonAsync(
            $"/api/journey/frequencies/segments/{segment}", new FrequencyRequest(1, 28));
        await client.PutAsJsonAsync(
            $"/api/journey/frequencies/outlets/{outletId}", new FrequencyRequest(4, 28));

        var resolved = Assert.Single(await ResolveAsync(client, outletId));

        Assert.Equal(4, resolved.VisitsPerCycle);
        Assert.Equal(nameof(FrequencySource.Outlet), resolved.Source);
    }

    [Fact]
    public async Task Removing_an_override_falls_back_to_the_segment_rather_than_to_nothing()
    {
        // The point of a ladder. Deleting the exception restores the rule it was an exception to,
        // which is why removing an override is a delete and not "set it back to the default by hand".
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var segment = Unique("SEG");
        var outletId = await OutletAsync(client, segment);

        await client.PutAsJsonAsync(
            $"/api/journey/frequencies/segments/{segment}", new FrequencyRequest(1, 28));
        await client.PutAsJsonAsync(
            $"/api/journey/frequencies/outlets/{outletId}", new FrequencyRequest(4, 28));

        var removed = await client.DeleteAsync($"/api/journey/frequencies/outlets/{outletId}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        var resolved = Assert.Single(await ResolveAsync(client, outletId));

        Assert.Equal(1, resolved.VisitsPerCycle);
        Assert.Equal(nameof(FrequencySource.Segment), resolved.Source);
    }

    [Fact]
    public async Task An_outlet_nobody_has_configured_resolves_to_nothing_rather_than_to_zero()
    {
        // Absent, not zero. "Nobody has said how often to visit this shop" is a gap in configuration
        // and "never visit it" is a decision; conflating them would hide the first behind the second,
        // and there would be no screen that could tell an admin which shops they had missed.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outletId = await OutletAsync(client, segment: Unique("SEG"));

        Assert.Empty(await ResolveAsync(client, outletId));
    }

    [Fact]
    public async Task An_outlet_with_no_segment_at_all_resolves_to_nothing()
    {
        // Segment is optional on an outlet (OUT-01), so a shop nobody has graded has no default to
        // inherit — and that has to be the same quiet "unconfigured" as above, not an error.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outletId = await OutletAsync(client, segment: null);

        Assert.Empty(await ResolveAsync(client, outletId));
    }

    [Fact]
    public async Task A_segment_rule_matches_the_outlet_whatever_case_either_was_typed_in()
    {
        // Free text on both sides, so "A" and "a" are the same grade to everyone except a string
        // comparer. Matching is lenient; storage keeps whatever the tenant typed.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var segment = Unique("Seg");
        var outletId = await OutletAsync(client, segment.ToUpperInvariant());

        await client.PutAsJsonAsync(
            $"/api/journey/frequencies/segments/{segment.ToLowerInvariant()}",
            new FrequencyRequest(2, 14));

        var resolved = Assert.Single(await ResolveAsync(client, outletId));

        Assert.Equal(2, resolved.VisitsPerCycle);
    }

    [Fact]
    public async Task Setting_a_segment_twice_edits_one_rule_rather_than_making_a_second()
    {
        // PUT keyed by the segment, so saving twice has set it once. Without the case-insensitive
        // lookup this would create a second rule and resolution would then have two answers.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var segment = Unique("Seg");

        await client.PutAsJsonAsync(
            $"/api/journey/frequencies/segments/{segment}", new FrequencyRequest(1, 7));
        await client.PutAsJsonAsync(
            $"/api/journey/frequencies/segments/{segment.ToUpperInvariant()}", new FrequencyRequest(3, 7));

        var rules = (await client.GetFromJsonAsync<List<SegmentFrequencyResponse>>(
            "/api/journey/frequencies/segments"))!;

        var mine = rules.Where(rule => string.Equals(rule.Segment, segment, StringComparison.OrdinalIgnoreCase)).ToList();

        var only = Assert.Single(mine);
        Assert.Equal(3, only.VisitsPerCycle);

        // And it kept the casing it was created with, rather than adopting the last caller's.
        Assert.Equal(segment, only.Segment);
    }

    [Theory]
    [InlineData(0, 7, "visitsPerCycle")]
    [InlineData(-1, 7, "visitsPerCycle")]
    [InlineData(1, 0, "cycleLengthDays")]
    [InlineData(1, 366, "cycleLengthDays")]
    public async Task Numbers_that_are_not_a_frequency_are_refused_by_name(
        int visits, int cycleDays, string field)
    {
        // Named per field because the admin typed two numbers and only one of them is wrong. Zero
        // visits is refused rather than stored as "never" — see CallFrequency for why that is not a
        // state this type is willing to represent.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await client.PutAsJsonAsync(
            $"/api/journey/frequencies/segments/{Unique("SEG")}",
            new FrequencyRequest(visits, cycleDays));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problems = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        Assert.Contains(
            problems.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("field").GetString() == field);
    }

    [Fact]
    public async Task An_override_for_an_outlet_this_tenant_does_not_have_is_refused()
    {
        // The outlet id is not a foreign key — Outlets is another schema (AT-1) — so this is the
        // only thing standing between a typo and a rule that resolves against nothing forever.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await client.PutAsJsonAsync(
            $"/api/journey/frequencies/outlets/{Guid.CreateVersion7()}", new FrequencyRequest(1, 7));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problems = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        Assert.Contains(
            problems.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("code").GetString() == "journey.frequency.unknownOutlet");
    }

    [Fact]
    public async Task Another_tenants_outlet_is_not_an_outlet_this_one_can_write_a_rule_about()
    {
        // The same refusal as above, reached the interesting way: the id is real, and the only thing
        // that makes it unknown is the tenant filter on the other side of the contract.
        using var tenantA = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var outletA = await OutletAsync(tenantA, Unique("SEG"));

        var response = await tenantB.PutAsJsonAsync(
            $"/api/journey/frequencies/outlets/{outletA}", new FrequencyRequest(1, 7));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task One_tenants_segment_rules_never_resolve_for_anothers_outlets()
    {
        // Segments are free text, so two tenants routinely use the same labels — "A" is "A"
        // everywhere. Nothing but the tenant filter keeps one tenant's grading off another's shops.
        using var tenantA = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var shared = Unique("SEG");

        await tenantA.PutAsJsonAsync(
            $"/api/journey/frequencies/segments/{shared}", new FrequencyRequest(4, 28));

        var outletB = await OutletAsync(tenantB, shared);

        Assert.Empty(await ResolveAsync(tenantB, outletB));

        // …and B setting its own rule for the same label does not disturb A's.
        await tenantB.PutAsJsonAsync(
            $"/api/journey/frequencies/segments/{shared}", new FrequencyRequest(1, 28));

        var outletA = await OutletAsync(tenantA, shared);
        var resolvedForA = Assert.Single(await ResolveAsync(tenantA, outletA));

        Assert.Equal(4, resolvedForA.VisitsPerCycle);
    }

    [Fact]
    public async Task Resolving_several_outlets_answers_for_each_from_its_own_rung()
    {
        // Generation resolves a rep's whole territory at once, so the bulk path is the real one —
        // and mixing rungs in a single answer is where a per-outlet loop would have been rewritten
        // into something subtly wrong.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var segment = Unique("SEG");
        var bySegment = await OutletAsync(client, segment);
        var byOverride = await OutletAsync(client, segment);
        var unconfigured = await OutletAsync(client, segment: null);

        await client.PutAsJsonAsync(
            $"/api/journey/frequencies/segments/{segment}", new FrequencyRequest(1, 28));
        await client.PutAsJsonAsync(
            $"/api/journey/frequencies/outlets/{byOverride}", new FrequencyRequest(4, 28));

        var resolved = await ResolveAsync(client, bySegment, byOverride, unconfigured);

        Assert.Equal(2, resolved.Count);
        Assert.Equal(nameof(FrequencySource.Segment), resolved.Single(row => row.OutletId == bySegment).Source);
        Assert.Equal(nameof(FrequencySource.Outlet), resolved.Single(row => row.OutletId == byOverride).Source);
        Assert.DoesNotContain(resolved, row => row.OutletId == unconfigured);
    }

    [Fact]
    public async Task Offers_no_way_to_change_a_frequency_to_a_caller_who_may_only_read()
    {
        // The read-only token holds journey:read and not journey:write.
        using var reader = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        var listed = await reader.GetAsync("/api/journey/frequencies/segments");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        var attempted = await reader.PutAsJsonAsync(
            $"/api/journey/frequencies/segments/{Unique("SEG")}", new FrequencyRequest(1, 7));

        Assert.Equal(HttpStatusCode.Forbidden, attempted.StatusCode);
    }
}
