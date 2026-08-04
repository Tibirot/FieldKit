using System.Net.Http.Json;
using FieldKit.Modules.Org;
using FieldKit.Modules.Org.Contracts;
using FieldKit.Modules.Outlets;
using FieldKit.Web;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// The territory an outlet carries (<c>ORG-05</c>, <c>BR-OUT-1</c>).
/// </summary>
/// <remarks>
/// Organization owns which territory covers a shop; Outlets shows it. The assertions worth reading
/// are the ones about what happens when the answer is <i>absent</i> — an unassigned outlet and
/// another tenant's outlet must be indistinguishable from here, and for different reasons.
/// </remarks>
[Collection(ServerCollection.Name)]
public class TerritoryDirectoryTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private async Task<Guid> ChannelAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        return (await response.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    private async Task<OutletResponse> OutletAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(
                Unique("OUT"), "Corner Shop", await ChannelAsync(client), null, null, Zone));

        return (await response.Content.ReadFromJsonAsync<OutletResponse>())!;
    }

    /// <summary>Creates a territory under a fresh org unit and puts <paramref name="outletId"/> in it.</summary>
    private async Task<(Guid Id, string Name)> TerritoryAsync(HttpClient client, Guid outletId)
    {
        var unit = await client.PostAsJsonAsync(
            "/api/org/units", new OrgUnitRequest(Unique("Unit"), null));
        var unitId = (await unit.Content.ReadFromJsonAsync<OrgUnitResponse>())!.Id;

        var name = Unique("Territory");
        var territory = await client.PostAsJsonAsync(
            "/api/org/territories", new TerritoryRequest(name, unitId));
        var territoryId = (await territory.Content.ReadFromJsonAsync<TerritoryResponse>())!.Id;

        await client.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/outlets", new AssignOutletsRequest([outletId]));

        return (territoryId, name);
    }

    [Fact]
    public async Task An_outlet_shows_the_territory_that_covers_it()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outlet = await OutletAsync(client);
        Assert.Null(outlet.Territory);

        var (territoryId, name) = await TerritoryAsync(client, outlet.Id);

        var single = await client.GetFromJsonAsync<OutletResponse>($"/api/outlets/{outlet.Id}");
        Assert.Equal(territoryId, single!.Territory?.Id);
        Assert.Equal(name, single.Territory?.Name);

        // And on the list, which is the screen this exists for.
        var listed = (await client.GetFromJsonAsync<PagedList<OutletResponse>>($"/api/outlets?pageSize={Paging.MaxSize}"))!.Items;
        Assert.Equal(territoryId, listed!.Single(row => row.Id == outlet.Id).Territory?.Id);
    }

    [Fact]
    public async Task An_outlet_nobody_covers_yet_simply_has_none()
    {
        // Not an error and not a placeholder. Outlets are created before anyone decides who covers
        // them, so BR-OUT-1's "every outlet has a primary territory" describes a configured tenant
        // rather than a precondition for storing a shop.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outlet = await OutletAsync(client);

        var single = await client.GetFromJsonAsync<OutletResponse>($"/api/outlets/{outlet.Id}");
        Assert.Null(single!.Territory);
    }

    [Fact]
    public async Task Removing_an_outlet_from_a_territory_removes_the_label()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outlet = await OutletAsync(client);
        var (territoryId, _) = await TerritoryAsync(client, outlet.Id);

        await client.DeleteAsync($"/api/org/territories/{territoryId}/outlets/{outlet.Id}");

        var single = await client.GetFromJsonAsync<OutletResponse>($"/api/outlets/{outlet.Id}");
        Assert.Null(single!.Territory);
    }

    [Fact]
    public async Task One_tenants_territories_never_label_anothers_outlets()
    {
        // The interesting isolation case: the lookup crosses a module boundary, so the tenant filter
        // has to hold on Organization's side of it rather than on the side that was asked.
        using var tenantA = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var outletA = await OutletAsync(tenantA);
        await TerritoryAsync(tenantA, outletA.Id);

        var outletB = await OutletAsync(tenantB);

        var seenByB = (await tenantB.GetFromJsonAsync<PagedList<OutletResponse>>($"/api/outlets?pageSize={Paging.MaxSize}"))!.Items;

        Assert.DoesNotContain(seenByB!, row => row.Id == outletA.Id);
        Assert.Null(seenByB!.Single(row => row.Id == outletB.Id).Territory);
    }

    [Fact]
    public async Task The_directory_refuses_to_answer_outside_an_authenticated_request()
    {
        // Found by writing a different test and watching it fail, which is the better finding: a
        // cross-module contract is exactly the thing someone would later reach for from a background
        // job, and it would then read every tenant's memberships at once. The tenant filter needs a
        // request to resolve, so it throws rather than quietly answering for nobody.
        using var scope = fixture.Services.CreateScope();
        var directory = scope.ServiceProvider.GetRequiredService<ITerritoryDirectory>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => directory.ForOutletsAsync([Guid.CreateVersion7()]));

        // Except for the empty ask, which is answered without a query at all — a caller with nothing
        // to look up should not pay a round trip, and there is no tenant-owned data in "nothing".
        Assert.Empty(await directory.ForOutletsAsync([]));
    }
}
