using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Org;

namespace FieldKit.Server.Tests;

/// <summary>
/// The sales hierarchy (<c>ORG-01</c>) — configurable depth, tenant-chosen labels.
/// </summary>
/// <remarks>
/// The tenant's units are shared state across this class, so every test names its units with a
/// unique prefix rather than asserting on the whole list. A test that counts everything couples
/// itself to whatever ran first.
/// </remarks>
[Collection(ServerCollection.Name)]
public class OrgUnitTests(ServerFixture fixture)
{
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..24];

    private async Task<OrgUnitResponse> CreateAsync(HttpClient client, string name, Guid? parentId = null)
    {
        var response = await client.PostAsJsonAsync("/api/org/units", new OrgUnitRequest(name, parentId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<OrgUnitResponse>())!;
    }

    [Fact]
    public async Task A_hierarchy_can_be_built_to_any_depth()
    {
        // Country → Region → Area → Team, the shape the spec describes — but nothing in the schema
        // knows those labels or that there are four of them. Depth is whatever a tenant builds.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var country = await CreateAsync(client, Unique("Country"));
        var region = await CreateAsync(client, Unique("Region"), country.Id);
        var area = await CreateAsync(client, Unique("Area"), region.Id);
        var team = await CreateAsync(client, Unique("Team"), area.Id);

        Assert.Null(country.ParentId);
        Assert.Equal(country.Id, region.ParentId);
        Assert.Equal(region.Id, area.ParentId);
        Assert.Equal(area.Id, team.ParentId);

        var all = await client.GetFromJsonAsync<List<OrgUnitResponse>>("/api/org/units");
        Assert.All([country, region, area, team], unit => Assert.Contains(all!, u => u.Id == unit.Id));
    }

    [Fact]
    public async Task A_unit_cannot_be_created_under_a_parent_that_does_not_exist()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await client.PostAsJsonAsync(
            "/api/org/units", new OrgUnitRequest(Unique("Orphan"), Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Names_are_unique_among_siblings_and_only_among_siblings()
    {
        // The rule that keeps a tenant from having to encode ancestry in every leaf's name: "North"
        // under Romania and "North" under Poland are different teams, and both are fine.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var romania = await CreateAsync(client, Unique("Romania"));
        var poland = await CreateAsync(client, Unique("Poland"));

        var north = Unique("North");
        await CreateAsync(client, north, romania.Id);
        await CreateAsync(client, north, poland.Id); // same name, different parent — allowed

        var duplicate = await client.PostAsJsonAsync("/api/org/units", new OrgUnitRequest(north, romania.Id));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task A_unit_cannot_be_moved_into_its_own_branch()
    {
        // The check that keeps a hierarchy a hierarchy. Without it the whole branch detaches from
        // every root: the rows remain, every foreign key still resolves, and no traversal reaches
        // them again. Nothing in the database can catch this — every parent still exists.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var region = await CreateAsync(client, Unique("Region"));
        var area = await CreateAsync(client, Unique("Area"), region.Id);
        var team = await CreateAsync(client, Unique("Team"), area.Id);

        // Two levels down, so this fails only if the walk goes further than the immediate child.
        var response = await client.PutAsJsonAsync(
            $"/api/org/units/{region.Id}", new OrgUnitRequest(region.Name, team.Id));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var unchanged = await client.GetFromJsonAsync<List<OrgUnitResponse>>("/api/org/units");
        Assert.Null(Assert.Single(unchanged!, unit => unit.Id == region.Id).ParentId);
    }

    [Fact]
    public async Task A_unit_cannot_be_its_own_parent()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var unit = await CreateAsync(client, Unique("Self"));

        var response = await client.PutAsJsonAsync(
            $"/api/org/units/{unit.Id}", new OrgUnitRequest(unit.Name, unit.Id));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_unit_can_be_renamed_and_moved_in_one_call()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var first = await CreateAsync(client, Unique("First"));
        var second = await CreateAsync(client, Unique("Second"));
        var child = await CreateAsync(client, Unique("Child"), first.Id);

        var renamed = Unique("Renamed");
        var response = await client.PutAsJsonAsync(
            $"/api/org/units/{child.Id}", new OrgUnitRequest(renamed, second.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await response.Content.ReadFromJsonAsync<OrgUnitResponse>();
        Assert.Equal(renamed, updated!.Name);
        Assert.Equal(second.Id, updated.ParentId);
    }

    [Fact]
    public async Task Deleting_a_unit_that_still_has_children_is_refused()
    {
        // Refused rather than cascaded: removing a region should not silently take its areas and
        // teams — and once positions and territories hang off these units, it would take those too.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var parent = await CreateAsync(client, Unique("Parent"));
        var child = await CreateAsync(client, Unique("Child"), parent.Id);

        var refused = await client.DeleteAsync($"/api/org/units/{parent.Id}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        // …and the leaf goes, which is what makes the refusal above about children rather than
        // deletion being broken.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/org/units/{child.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/org/units/{parent.Id}")).StatusCode);
    }

    [Fact]
    public async Task Reading_the_hierarchy_and_redrawing_it_are_different_capabilities()
    {
        // `viewer` holds orgunit:read and not orgunit:write. 403, not 401 — they are authenticated,
        // and telling them to sign in again is a dead end for them and a ticket for someone else.
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/org/units")).StatusCode);

        var write = await viewer.PostAsJsonAsync("/api/org/units", new OrgUnitRequest(Unique("Nope"), null));

        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task One_tenants_hierarchy_is_invisible_to_another()
    {
        using var tenantA = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var mine = await CreateAsync(tenantA, Unique("Private"));

        var visibleToB = await tenantB.GetFromJsonAsync<List<OrgUnitResponse>>("/api/org/units");
        Assert.DoesNotContain(visibleToB!, unit => unit.Id == mine.Id);

        // B cannot reach it by id either. `rep-b` deliberately *holds* orgunit:write, so this is
        // 404 from the query filter rather than 403 from the permission check — without that, the
        // assertion would pass with no isolation at all.
        var byId = await tenantB.PutAsJsonAsync($"/api/org/units/{mine.Id}", new OrgUnitRequest("Hijacked", null));
        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);
    }
}
