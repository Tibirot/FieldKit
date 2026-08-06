using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Org;
using FieldKit.Modules.Outlets;

namespace FieldKit.Server.Tests;

using static Refusals;

/// <summary>
/// Territories and the outlets in them (<c>ORG-03</c>, <c>ORG-05</c>).
/// </summary>
/// <remarks>
/// The first feature that spans two modules in both directions: Organization owns the mapping and
/// asks Outlets — through <c>IOutletCatalog</c>, never its schema — which outlets are real.
/// </remarks>
[Collection(ServerCollection.Name)]
public class TerritoryTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..20];

    private async Task<Guid> OrgUnitAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/org/units", new OrgUnitRequest(Unique("Region"), null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrgUnitResponse>())!.Id;
    }

    private async Task<TerritoryResponse> TerritoryAsync(HttpClient client, Guid? orgUnitId = null)
    {
        var unit = orgUnitId ?? await OrgUnitAsync(client);
        var response = await client.PostAsJsonAsync(
            "/api/org/territories", new TerritoryRequest(Unique("Territory"), unit));

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<TerritoryResponse>())!;
    }

    private async Task<Guid> OutletAsync(HttpClient client, string? code = null)
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        // The body on failure: a 403 has an empty one, so without this the test dies on a JSON
        // parse error that says nothing about the missing permission which actually caused it.
        Assert.True(
            channel.StatusCode == HttpStatusCode.Created,
            $"channel: {channel.StatusCode}: {await channel.Content.ReadAsStringAsync()}");

        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var response = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(code ?? Unique("OUT"), "Corner Shop", channelId, null, null, Zone));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    [Fact]
    public async Task A_territory_belongs_to_an_org_unit_that_exists()
    {
        // Required rather than optional: BR-ORG-4 says a supervisor sees the territories under their
        // branch, so a territory under no branch would be visible to nobody by that rule.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await client.PostAsJsonAsync(
            "/api/org/territories", new TerritoryRequest(Unique("Orphan"), Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Outlets_are_assigned_and_come_back_named_through_the_contract()
    {
        // The seam: Organization stores outlet ids and asks Outlets what they are called. The names
        // arriving at all is what says the contract is wired rather than the ids being echoed back.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var territory = await TerritoryAsync(client);
        var outletId = await OutletAsync(client);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync(
                $"/api/org/territories/{territory.Id}/outlets", new AssignOutletsRequest([outletId])))
                .StatusCode);

        var members = await client.GetFromJsonAsync<List<TerritoryOutletResponse>>(
            $"/api/org/territories/{territory.Id}/outlets");

        var member = Assert.Single(members!);
        Assert.Equal(outletId, member.OutletId);
        Assert.Equal("Corner Shop", member.Name);
        Assert.False(string.IsNullOrWhiteSpace(member.Code));
        Assert.True(member.IsOpen);
    }

    [Fact]
    public async Task An_outlet_belongs_to_exactly_one_territory()
    {
        // BR-ORG-1 / ORG-05. Refused rather than silently moved: a territory's membership is a rep's
        // offline data scope (BR-ORG-3), so reassigning an outlet changes what somebody's device
        // downloads tomorrow morning. Same two-step this module already requires for moving a
        // position, and for the same reason — the audit trail should show both halves.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var first = await TerritoryAsync(client);
        var second = await TerritoryAsync(client);
        var outletId = await OutletAsync(client);

        await client.PostAsJsonAsync(
            $"/api/org/territories/{first.Id}/outlets", new AssignOutletsRequest([outletId]));

        var contested = await client.PostAsJsonAsync(
            $"/api/org/territories/{second.Id}/outlets", new AssignOutletsRequest([outletId]));

        Assert.Equal(HttpStatusCode.Conflict, contested.StatusCode);

        // …and after removing it from the first, the second accepts it.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/org/territories/{first.Id}/outlets/{outletId}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync(
                $"/api/org/territories/{second.Id}/outlets", new AssignOutletsRequest([outletId])))
                .StatusCode);
    }

    [Fact]
    public async Task The_outlets_already_taken_are_named_by_code()
    {
        // The message is the whole answer — a client that only renders messages still has to be able
        // to say which outlets to free up first. A list of GUIDs satisfies that shape and tells a
        // human nothing; the code is what is on the outlet list, in their spreadsheet, and above the
        // door.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var first = await TerritoryAsync(client);
        var second = await TerritoryAsync(client);
        var code = Unique("TAKEN");
        var outletId = await OutletAsync(client, code);

        await client.PostAsJsonAsync(
            $"/api/org/territories/{first.Id}/outlets", new AssignOutletsRequest([outletId]));

        var contested = await client.PostAsJsonAsync(
            $"/api/org/territories/{second.Id}/outlets", new AssignOutletsRequest([outletId]));

        var problem = Assert.Single(await ProblemsOf(contested));

        Assert.Equal("outletIds", problem.Field);
        Assert.Contains(code, problem.Message);
        Assert.DoesNotContain(outletId.ToString(), problem.Message);
    }
    [Fact]
    public async Task Assigning_the_same_outlet_twice_is_not_an_error()
    {
        // Idempotent, so a retry after a dropped response does the right thing rather than 409-ing
        // against work it already did.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var territory = await TerritoryAsync(client);
        var outletId = await OutletAsync(client);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await client.PostAsJsonAsync(
                    $"/api/org/territories/{territory.Id}/outlets", new AssignOutletsRequest([outletId])))
                    .StatusCode);
        }

        var members = await client.GetFromJsonAsync<List<TerritoryOutletResponse>>(
            $"/api/org/territories/{territory.Id}/outlets");

        Assert.Single(members!);
    }

    [Fact]
    public async Task An_outlet_this_tenant_does_not_have_is_rejected_as_a_set()
    {
        // Validated through IOutletCatalog, not by reading the outlets schema — and the unknown ids
        // come back so the caller can see which ones, rather than being told the batch failed.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var territory = await TerritoryAsync(client);
        var real = await OutletAsync(client);
        var invented = Guid.NewGuid();

        var response = await client.PostAsJsonAsync(
            $"/api/org/territories/{territory.Id}/outlets", new AssignOutletsRequest([real, invented]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Nothing was assigned — the batch is rejected whole, so a partial write cannot leave the
        // caller guessing which half landed.
        var members = await client.GetFromJsonAsync<List<TerritoryOutletResponse>>(
            $"/api/org/territories/{territory.Id}/outlets");

        Assert.Empty(members!);
    }

    [Fact]
    public async Task A_territory_holding_outlets_cannot_be_deleted()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var territory = await TerritoryAsync(client);
        var outletId = await OutletAsync(client);

        await client.PostAsJsonAsync(
            $"/api/org/territories/{territory.Id}/outlets", new AssignOutletsRequest([outletId]));

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await client.DeleteAsync($"/api/org/territories/{territory.Id}")).StatusCode);

        // …and once emptied it goes, so the refusal is about the outlets rather than deletion being
        // broken.
        await client.DeleteAsync($"/api/org/territories/{territory.Id}/outlets/{outletId}");

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/org/territories/{territory.Id}")).StatusCode);
    }

    [Fact]
    public async Task An_org_unit_holding_territories_cannot_be_deleted()
    {
        // Third guard of the same shape as child units and positions. A territory attached to a
        // deleted unit would be under no branch, and so seen by nobody under BR-ORG-4.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var unitId = await OrgUnitAsync(client);
        await TerritoryAsync(client, unitId);

        Assert.Equal(
            HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/org/units/{unitId}")).StatusCode);
    }

    [Fact]
    public async Task A_closed_outlet_still_resolves_but_says_so()
    {
        // The contract returns closed outlets deliberately: a territory that contained one must
        // still be able to say so. What "closed" should stop is the caller's decision, and journey
        // generation is where that decision belongs (BR-OUT-4).
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var territory = await TerritoryAsync(client);
        var outletId = await OutletAsync(client);

        await client.PostAsJsonAsync(
            $"/api/org/territories/{territory.Id}/outlets", new AssignOutletsRequest([outletId]));
        await client.PostAsJsonAsync(
            $"/api/outlets/{outletId}/status", new OutletStatusRequest(OutletStatus.Closed, "Lease ended"));

        var members = await client.GetFromJsonAsync<List<TerritoryOutletResponse>>(
            $"/api/org/territories/{territory.Id}/outlets");

        var member = Assert.Single(members!);
        Assert.Equal(outletId, member.OutletId);
        Assert.False(member.IsOpen);
    }

    [Fact]
    public async Task Reading_territories_and_deciding_their_outlets_are_different_capabilities()
    {
        // `viewer` holds territory:read and not territory:write.
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/org/territories")).StatusCode);

        var write = await viewer.PostAsJsonAsync(
            "/api/org/territories", new TerritoryRequest(Unique("Nope"), Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task One_tenants_territories_are_invisible_to_another()
    {
        using var tenantA = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var mine = await TerritoryAsync(tenantA);

        var visibleToB = await tenantB.GetFromJsonAsync<List<TerritoryResponse>>("/api/org/territories");
        Assert.DoesNotContain(visibleToB!, territory => territory.Id == mine.Id);

        // `rep-b` deliberately holds territory:write, so this is 404 from the query filter rather
        // than 403 from the permission check — otherwise the assertion proves nothing.
        var byId = await tenantB.GetAsync($"/api/org/territories/{mine.Id}/outlets");
        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);
    }

    [Fact]
    public async Task An_outlet_from_another_tenant_cannot_be_assigned()
    {
        // The cross-module half of isolation: the id is real, but IOutletCatalog resolves it inside
        // the *caller's* tenant, so it simply does not exist here. Nothing in Organization had to
        // know that — which is the point of asking through the contract.
        using var tenantA = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var theirOutlet = await OutletAsync(tenantB);
        var myTerritory = await TerritoryAsync(tenantA);

        var response = await tenantA.PostAsJsonAsync(
            $"/api/org/territories/{myTerritory.Id}/outlets", new AssignOutletsRequest([theirOutlet]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
