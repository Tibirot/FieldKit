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
            new CreateOutletRequest(code ?? Unique("OUT"), "Corner Shop", channelId, "A", "Veridian Group"));

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
    public async Task An_outlet_must_name_a_channel_this_tenant_has()
    {
        // BR-OUT-1: an outlet without a channel cannot be given an assortment, a price list or a
        // visit workflow, so there is no useful state where one exists unclassified.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await client.PostAsJsonAsync(
            "/api/outlets", new CreateOutletRequest(Unique("OUT"), "No Channel", Guid.NewGuid(), null, null));

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
            "/api/outlets", new CreateOutletRequest(code, "Same code", channel.Id, null, null));

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
            $"/api/outlets/{outlet.Id}", new UpdateOutletRequest("Renamed", channel.Id, "B", null));

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
                $"/api/outlets/{outlet.Id}/status", new OutletStatusRequest(OutletStatus.Closed))).StatusCode);

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
            $"/api/outlets/{shut.Id}/status", new OutletStatusRequest(OutletStatus.Closed));

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
                "/api/outlets", new CreateOutletRequest("X", "Nope", Guid.NewGuid(), null, null))).StatusCode);
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
            $"/api/outlets/{mine.Id}", new UpdateOutletRequest("Hijacked", channel.Id, null, null));
        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);

        // A's channel is not B's either, so B cannot even classify against it.
        var stolenChannel = await tenantB.PostAsJsonAsync(
            "/api/outlets", new CreateOutletRequest("B-1", "Theirs", channel.Id, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, stolenChannel.StatusCode);
    }
}
