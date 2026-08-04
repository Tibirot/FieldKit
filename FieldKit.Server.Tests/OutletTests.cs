using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Outlets;

namespace FieldKit.Server.Tests;

/// <summary>
/// The outlet base and the vocabulary it is classified by (<c>OUT-01</c>, <c>OUT-04</c>).
/// </summary>
[Collection(ServerCollection.Name)]
public class OutletTests(ServerFixture fixture)
{
    /// <summary>A real IANA zone — the field is required, and validated against the runtime.</summary>
    private const string Zone = "Europe/Bucharest";

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..20];

    private async Task<ChannelResponse> ChannelAsync(HttpClient client, string? name = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(name ?? Unique("Channel")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChannelResponse>())!;
    }

    private async Task<OutletResponse> OutletAsync(HttpClient client, Guid channelId, string? code = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(code ?? Unique("OUT"), "Corner Shop", channelId, "A", "Veridian Group", Zone));

        // The body on failure, not just the code: the host runs in Development, so a 500 carries the
        // exception — and a bare "expected Created, actual InternalServerError" costs a debug cycle
        // to learn what any CI log already knew.
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<OutletResponse>())!;
    }

    [Fact]
    public async Task An_outlet_is_created_classified_and_active()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var channel = await ChannelAsync(client);
        var outlet = await OutletAsync(client, channel.Id);

        // The channel name comes back alongside the id: the id is what a client edits with, the name
        // is what it renders, and returning only the id would make every table fetch the channel
        // list to caption itself.
        Assert.Equal(channel.Id, outlet.ChannelId);
        Assert.Equal(channel.Name, outlet.ChannelName);
        Assert.Equal(OutletStatus.Active, outlet.Status);
        Assert.Equal("A", outlet.Segment);
    }

    [Fact]
    public async Task An_outlet_carries_an_address_a_location_and_its_contacts()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var channel = await ChannelAsync(client);
        var response = await client.PostAsJsonAsync("/api/outlets", new CreateOutletRequest(
            Unique("OUT"),
            "Mega Image Dorobanți",
            channel.Id,
            "A",
            "Mega Image",
            Zone,
            new Address("Calea Dorobanți 172", "București", "010578", "RO"),
            new Coordinates(44.4638, 26.0946),
            [new OutletContact("Ana Ionescu", "Store manager", "+40 721 000 000", "ana@example.ro")]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var outlet = await response.Content.ReadFromJsonAsync<OutletResponse>();
        Assert.Equal("010578", outlet!.Address!.PostalCode);
        Assert.Equal(44.4638, outlet.Location!.Latitude);
        Assert.Equal("Ana Ionescu", Assert.Single(outlet.Contacts).Name);
        Assert.Equal(Zone, outlet.TimeZoneId);
    }

    [Fact]
    public async Task The_time_zone_is_required_and_checked_against_the_runtime()
    {
        // Not cosmetic: a visit's business day and a promotion's validity both resolve in this zone
        // (BR-PRD-6), so an unknown one is a wrong answer waiting to be given. Checked against the
        // runtime rather than a regex, because "Europe/Bucuresti" is well-formed and does not exist.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var channel = await ChannelAsync(client);

        foreach (var bad in new[] { "", "Europe/Bucuresti", "GMT+2" })
        {
            var response = await client.PostAsJsonAsync(
                "/api/outlets",
                new CreateOutletRequest(Unique("OUT"), "Bad zone", channel.Id, null, null, bad));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Contacts_are_replaced_wholesale_and_an_empty_list_removes_them()
    {
        // Wholesale rather than deltas: a patch needs the caller to know the current state, and two
        // people editing one outlet would interleave silently. It also gives erasure a trivial
        // shape — these are personal data (B8), and an empty list deletes the rows rather than
        // flagging them.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var channel = await ChannelAsync(client);
        var outlet = await OutletAsync(client, channel.Id);

        var withContacts = await client.PutAsJsonAsync($"/api/outlets/{outlet.Id}", new UpdateOutletRequest(
            outlet.Name, channel.Id, null, null, Zone,
            Contacts: [new OutletContact("Ana", "Buyer", null, null), new OutletContact("Bogdan", null, null, null)]));

        Assert.Equal(2, (await withContacts.Content.ReadFromJsonAsync<OutletResponse>())!.Contacts.Count);

        var erased = await client.PutAsJsonAsync($"/api/outlets/{outlet.Id}", new UpdateOutletRequest(
            outlet.Name, channel.Id, null, null, Zone, Contacts: []));

        Assert.Empty((await erased.Content.ReadFromJsonAsync<OutletResponse>())!.Contacts);
    }

    [Fact]
    public async Task Coordinates_are_optional_but_always_have_to_be_a_real_place()
    {
        // Two rules that are easy to conflate and are not the same thing. Whether an outlet *has*
        // coordinates is optional — onboarding data routinely arrives without them. Whether a
        // supplied pair is a point on the earth is not a policy any tenant chooses: latitude 91 is
        // meaningless for every kind of outlet and every kind of visit.
        //
        // What *is* a policy lives in Visit, not here: BR-VIS-2 allows an out-of-geofence check-in
        // with a recorded override reason, and remote-capable visit types skip even that.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var channel = await ChannelAsync(client);

        foreach (var offEarth in new[] { new Coordinates(91, 26), new Coordinates(44, 200) })
        {
            var refused = await client.PostAsJsonAsync(
                "/api/outlets",
                new CreateOutletRequest(Unique("OUT"), "Off earth", channel.Id, null, null, Zone,
                    Location: offEarth));

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsJsonAsync(
                "/api/outlets",
                new CreateOutletRequest(Unique("OUT"), "No location", channel.Id, null, null, Zone)))
                .StatusCode);
    }

    [Fact]
    public async Task An_outlet_must_name_a_channel_this_tenant_has()
    {
        // BR-OUT-1: an outlet without a channel cannot be given an assortment, a price list or a
        // visit workflow, so there is no useful state where one exists unclassified.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await client.PostAsJsonAsync(
            "/api/outlets", new CreateOutletRequest(Unique("OUT"), "No Channel", Guid.NewGuid(), null, null, Zone));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Codes_are_unique_within_the_tenant()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var channel = await ChannelAsync(client);
        var code = Unique("OUT");
        await OutletAsync(client, channel.Id, code);

        var duplicate = await client.PostAsJsonAsync(
            "/api/outlets", new CreateOutletRequest(code, "Same code", channel.Id, null, null, Zone));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task An_outlets_code_is_not_editable()
    {
        // The code is how an import recognises a location it has already seen. If an edit could
        // change it, the next import would create a duplicate instead of updating the original —
        // so the update contract simply has no field for it.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var channel = await ChannelAsync(client);
        var outlet = await OutletAsync(client, channel.Id);

        var response = await client.PutAsJsonAsync(
            $"/api/outlets/{outlet.Id}", new UpdateOutletRequest("Renamed", channel.Id, "B", null, Zone));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await response.Content.ReadFromJsonAsync<OutletResponse>();
        Assert.Equal(outlet.Code, updated!.Code);
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("B", updated.Segment);
        Assert.Null(updated.Banner);
    }

    [Fact]
    public async Task An_outlet_moves_between_active_and_inactive()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var channel = await ChannelAsync(client);
        var outlet = await OutletAsync(client, channel.Id);

        var deactivated = await client.PostAsJsonAsync(
            $"/api/outlets/{outlet.Id}/status", new OutletStatusRequest(OutletStatus.Inactive));
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.Equal(
            OutletStatus.Inactive,
            (await deactivated.Content.ReadFromJsonAsync<OutletResponse>())!.Status);

        var reactivated = await client.PostAsJsonAsync(
            $"/api/outlets/{outlet.Id}/status", new OutletStatusRequest(OutletStatus.Active));
        Assert.Equal(HttpStatusCode.OK, reactivated.StatusCode);
        Assert.Equal(
            OutletStatus.Active,
            (await reactivated.Content.ReadFromJsonAsync<OutletResponse>())!.Status);
    }

    [Fact]
    public async Task Every_transition_is_recorded_and_the_trail_starts_at_creation()
    {
        // Neither Inactive nor Closed deletes anything — but before this table the *evidence* of a
        // transition was lost anyway: the outlet's audit stamps are overwritten by the next ordinary
        // edit, so an outlet closed in March and renamed in April read as though nobody had ever
        // closed it. This is what makes BR-OUT-4's "retains history" literally true.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var channel = await ChannelAsync(client);
        var outlet = await OutletAsync(client, channel.Id);

        await client.PostAsJsonAsync(
            $"/api/outlets/{outlet.Id}/status", new OutletStatusRequest(OutletStatus.Inactive, "Refurbishment"));
        await client.PostAsJsonAsync(
            $"/api/outlets/{outlet.Id}/status", new OutletStatusRequest(OutletStatus.Active));
        await client.PostAsJsonAsync(
            $"/api/outlets/{outlet.Id}/status", new OutletStatusRequest(OutletStatus.Closed, "Lease ended"));

        // …then an ordinary edit, which is what used to destroy the record of the closure.
        await client.PutAsJsonAsync(
            $"/api/outlets/{outlet.Id}", new UpdateOutletRequest("Renamed after closing", channel.Id, null, null, Zone));

        var history = await client.GetFromJsonAsync<List<OutletStatusChangeResponse>>(
            $"/api/outlets/{outlet.Id}/status-history");

        // The whole sequence, newest first, rather than spot checks: asserting each transition's
        // `from` is what catches a trail that records where an outlet went but not where it came
        // from — which reads fine one row at a time and is useless to reconstruct from.
        //
        // The oldest entry has a null `from`, so "no history" can never be mistaken for "the
        // history was lost".
        Assert.Equal(
            [
                (OutletStatus.Active, OutletStatus.Closed),
                (OutletStatus.Inactive, OutletStatus.Active),
                (OutletStatus.Active, OutletStatus.Inactive),
                (null, OutletStatus.Active),
            ],
            history!.Select(change => (change.From, change.To)));

        Assert.Equal("Lease ended", history[0].Reason);
        Assert.False(string.IsNullOrWhiteSpace(history[0].ChangedBy));

        // The reason from the *first* deactivation survives the later ones — the point of a trail
        // rather than a pair of columns holding only the latest.
        Assert.Contains(history, change => change.Reason == "Refurbishment");
    }

    [Fact]
    public async Task Closing_requires_a_reason_and_a_no_op_records_nothing()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var channel = await ChannelAsync(client);
        var outlet = await OutletAsync(client, channel.Id);

        // Irreversible, and removes the outlet from every future journey — so "why" is the question
        // an auditor will ask, and the person who can answer it is the one doing it now.
        var unexplained = await client.PostAsJsonAsync(
            $"/api/outlets/{outlet.Id}/status", new OutletStatusRequest(OutletStatus.Closed));
        Assert.Equal(HttpStatusCode.BadRequest, unexplained.StatusCode);

        // A no-op is accepted but leaves no entry: a trail full of rows where nothing happened is
        // harder to read than one without them.
        await client.PostAsJsonAsync(
            $"/api/outlets/{outlet.Id}/status", new OutletStatusRequest(OutletStatus.Active));

        var history = await client.GetFromJsonAsync<List<OutletStatusChangeResponse>>(
            $"/api/outlets/{outlet.Id}/status-history");

        Assert.Single(history!); // creation only
    }

    [Fact]
    public async Task Closing_an_outlet_is_permanent()
    {
        // What makes Closed mean anything beyond Inactive. A status that can be walked back is just
        // a long-lived Inactive, and BR-OUT-4's "excluded from new journeys, keeps its history"
        // would be a preference rather than a fact.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var channel = await ChannelAsync(client);
        var outlet = await OutletAsync(client, channel.Id);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsJsonAsync(
                $"/api/outlets/{outlet.Id}/status",
                new OutletStatusRequest(OutletStatus.Closed, "Permanently shut"))).StatusCode);

        foreach (var attempt in new[] { OutletStatus.Active, OutletStatus.Inactive })
        {
            var reopened = await client.PostAsJsonAsync(
                $"/api/outlets/{outlet.Id}/status", new OutletStatusRequest(attempt));

            Assert.Equal(HttpStatusCode.Conflict, reopened.StatusCode);
        }
    }

    [Fact]
    public async Task Asking_for_the_status_an_outlet_already_has_is_not_an_error()
    {
        // Idempotent, because the caller is a back-office screen and a retry after a dropped
        // response should not read as a failure.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var channel = await ChannelAsync(client);
        var outlet = await OutletAsync(client, channel.Id);

        var response = await client.PostAsJsonAsync(
            $"/api/outlets/{outlet.Id}/status", new OutletStatusRequest(OutletStatus.Active));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_outlet_base_can_be_filtered_by_channel_and_by_status()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var mine = await ChannelAsync(client);
        var other = await ChannelAsync(client);

        var open = await OutletAsync(client, mine.Id);
        var shut = await OutletAsync(client, mine.Id);
        var elsewhere = await OutletAsync(client, other.Id);

        await client.PostAsJsonAsync(
            $"/api/outlets/{shut.Id}/status", new OutletStatusRequest(OutletStatus.Closed, "Lease ended"));

        var byChannel = await client.GetFromJsonAsync<List<OutletResponse>>(
            $"/api/outlets?channelId={mine.Id}");
        Assert.Contains(byChannel!, outlet => outlet.Id == open.Id);
        Assert.DoesNotContain(byChannel!, outlet => outlet.Id == elsewhere.Id);

        var active = await client.GetFromJsonAsync<List<OutletResponse>>(
            $"/api/outlets?channelId={mine.Id}&status={OutletStatus.Active}");
        Assert.Contains(active!, outlet => outlet.Id == open.Id);
        Assert.DoesNotContain(active!, outlet => outlet.Id == shut.Id);
    }

    [Fact]
    public async Task A_channel_in_use_cannot_be_deleted()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var channel = await ChannelAsync(client);
        var outlet = await OutletAsync(client, channel.Id);

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await client.DeleteAsync($"/api/outlets/channels/{channel.Id}")).StatusCode);

        // A spare channel with nothing classified as it deletes, so the refusal above is about use
        // rather than deletion being broken.
        var unused = await ChannelAsync(client);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/outlets/channels/{unused.Id}")).StatusCode);

        Assert.NotNull(outlet);
    }

    [Fact]
    public async Task A_channel_can_be_renamed_without_reclassifying_anything()
    {
        // Everything that keys off a channel keys off its id, so the label is safe to change. That
        // is the whole reason channel is reference data rather than a string on the outlet.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var channel = await ChannelAsync(client);
        var outlet = await OutletAsync(client, channel.Id);

        var renamed = Unique("Renamed");
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PutAsJsonAsync($"/api/outlets/channels/{channel.Id}", new ChannelRequest(renamed)))
                .StatusCode);

        var after = await client.GetFromJsonAsync<OutletResponse>($"/api/outlets/{outlet.Id}");
        Assert.Equal(channel.Id, after!.ChannelId);
        Assert.Equal(renamed, after.ChannelName);
    }

    [Fact]
    public async Task Maintaining_outlets_and_owning_the_classification_are_different_capabilities()
    {
        // `rep` holds outlet:read + channel:read and neither write. Someone walking the round can
        // see the base and the vocabulary; changing either is a back-office act.
        using var rep = fixture.CreateAuthenticatedClient();

        Assert.Equal(HttpStatusCode.OK, (await rep.GetAsync("/api/outlets")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rep.GetAsync("/api/outlets/channels")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await rep.PostAsJsonAsync("/api/outlets/channels", new ChannelRequest("Nope"))).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await rep.PostAsJsonAsync(
                "/api/outlets", new CreateOutletRequest("X", "Nope", Guid.NewGuid(), null, null, Zone))).StatusCode);
    }

    [Fact]
    public async Task One_tenants_outlets_are_invisible_to_another()
    {
        using var tenantA = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var channel = await ChannelAsync(tenantA);
        var mine = await OutletAsync(tenantA, channel.Id);

        var visibleToB = await tenantB.GetFromJsonAsync<List<OutletResponse>>("/api/outlets");
        Assert.DoesNotContain(visibleToB!, outlet => outlet.Id == mine.Id);

        // `rep-b` deliberately holds outlet:write, so this is 404 from the query filter rather than
        // 403 from the permission check — otherwise the assertion proves nothing.
        var byId = await tenantB.PutAsJsonAsync(
            $"/api/outlets/{mine.Id}", new UpdateOutletRequest("Hijacked", channel.Id, null, null, Zone));
        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);

        // A's channel is not B's either, so B cannot even classify against it.
        var stolenChannel = await tenantB.PostAsJsonAsync(
            "/api/outlets", new CreateOutletRequest("B-1", "Theirs", channel.Id, null, null, Zone));
        Assert.Equal(HttpStatusCode.BadRequest, stolenChannel.StatusCode);
    }
}
