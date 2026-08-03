using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Org;

namespace FieldKit.Server.Tests;

/// <summary>
/// Positions and the management line derived from them (<c>ORG-02</c>).
/// </summary>
[Collection(ServerCollection.Name)]
public class PositionTests(ServerFixture fixture)
{
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..24];

    private async Task<OrgUnitResponse> UnitAsync(HttpClient client, string name, Guid? parentId = null)
    {
        var response = await client.PostAsJsonAsync("/api/org/units", new OrgUnitRequest(name, parentId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrgUnitResponse>())!;
    }

    /// <summary>
    /// A user this tenant actually has. Positions reference IAM through <c>IUserDirectory</c>, so a
    /// fabricated id is rejected — the fixture has to create a real profile to attach to.
    /// </summary>
    private async Task<string> UserAsync(HttpClient client)
    {
        var subjectId = Guid.NewGuid().ToString();
        var roles = await client.GetFromJsonAsync<List<RoleResponse>>("/api/iam/roles");
        var anyRole = roles!.First(role => role.IsSystemTemplate).Id;

        var response = await client.PostAsJsonAsync("/api/iam/users", new
        {
            subjectId,
            email = $"{Guid.NewGuid():N}@fieldkit.local",
            displayName = "Fixture Person",
            locale = "en-GB",
            timeZone = "Europe/Bucharest",
            roleIds = new[] { anyRole },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return subjectId;
    }

    [Fact]
    public async Task A_user_can_be_placed_in_the_hierarchy()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var unit = await UnitAsync(client, Unique("Area"));
        var userId = await UserAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/org/positions", new PositionRequest(userId, unit.Id, "Supervisor"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<PositionResponse>();
        Assert.Equal(userId, created!.UserId);
        Assert.Equal(unit.Id, created.OrgUnitId);
        Assert.Equal("Supervisor", created.Title);
    }

    [Fact]
    public async Task A_position_must_name_a_user_and_a_unit_this_tenant_has()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var unit = await UnitAsync(client, Unique("Area"));
        var userId = await UserAsync(client);

        // Unknown user — resolved through IUserDirectory, not by reading IAM's tables.
        var unknownUser = await client.PostAsJsonAsync(
            "/api/org/positions", new PositionRequest(Guid.NewGuid().ToString(), unit.Id, "Rep"));
        Assert.Equal(HttpStatusCode.BadRequest, unknownUser.StatusCode);

        var unknownUnit = await client.PostAsJsonAsync(
            "/api/org/positions", new PositionRequest(userId, Guid.NewGuid(), "Rep"));
        Assert.Equal(HttpStatusCode.BadRequest, unknownUnit.StatusCode);
    }

    [Fact]
    public async Task The_same_user_cannot_hold_two_positions_in_one_unit()
    {
        // Across units is allowed — covering two areas is ordinary. Twice in the same unit says
        // nothing the title cannot, and would double that unit in their scope.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var first = await UnitAsync(client, Unique("First"));
        var second = await UnitAsync(client, Unique("Second"));
        var userId = await UserAsync(client);

        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/org/positions", new PositionRequest(userId, first.Id, "Lead")))
                .StatusCode);

        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/org/positions", new PositionRequest(userId, second.Id, "Cover")))
                .StatusCode);

        var duplicate = await client.PostAsJsonAsync(
            "/api/org/positions", new PositionRequest(userId, first.Id, "Lead again"));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task The_management_line_runs_up_and_the_visibility_scope_runs_down()
    {
        // The derivation ORG-02 asks for, and the two directions answer different questions: someone
        // reports up through their ancestors and sees down through their descendants.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var country = await UnitAsync(client, Unique("Country"));
        var region = await UnitAsync(client, Unique("Region"), country.Id);
        var area = await UnitAsync(client, Unique("Area"), region.Id);
        var teamA = await UnitAsync(client, Unique("TeamA"), area.Id);
        var teamB = await UnitAsync(client, Unique("TeamB"), area.Id);
        var elsewhere = await UnitAsync(client, Unique("Elsewhere"), country.Id);

        var userId = await UserAsync(client);
        await client.PostAsJsonAsync("/api/org/positions", new PositionRequest(userId, area.Id, "Supervisor"));

        var scope = await client.GetFromJsonAsync<UserScopeResponse>($"/api/org/users/{userId}/scope");

        Assert.Equal([region.Id, country.Id], scope!.ManagementLine);

        Assert.Contains(area.Id, scope.VisibleUnitIds);
        Assert.Contains(teamA.Id, scope.VisibleUnitIds);
        Assert.Contains(teamB.Id, scope.VisibleUnitIds);

        // The point of a scope: it stops somewhere. A sibling branch and everything above are out.
        Assert.DoesNotContain(elsewhere.Id, scope.VisibleUnitIds);
        Assert.DoesNotContain(region.Id, scope.VisibleUnitIds);
        Assert.DoesNotContain(country.Id, scope.VisibleUnitIds);
    }

    [Fact]
    public async Task Two_positions_in_one_branch_do_not_double_the_line()
    {
        // Someone covering an area and one of its teams reports through the same units either way —
        // the line is a path, not a concatenation.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var region = await UnitAsync(client, Unique("Region"));
        var area = await UnitAsync(client, Unique("Area"), region.Id);
        var team = await UnitAsync(client, Unique("Team"), area.Id);

        var userId = await UserAsync(client);
        await client.PostAsJsonAsync("/api/org/positions", new PositionRequest(userId, area.Id, "Supervisor"));
        await client.PostAsJsonAsync("/api/org/positions", new PositionRequest(userId, team.Id, "Covering"));

        var scope = await client.GetFromJsonAsync<UserScopeResponse>($"/api/org/users/{userId}/scope");

        Assert.Equal(scope!.ManagementLine.Distinct(), scope.ManagementLine);
        Assert.Equal(scope.VisibleUnitIds.Distinct(), scope.VisibleUnitIds);
        Assert.Equal(2, scope.Positions.Count);
    }

    [Fact]
    public async Task A_unit_cannot_be_deleted_while_someone_holds_a_position_in_it()
    {
        // Deleting it would remove that person from the org chart as a side effect of tidying up.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var unit = await UnitAsync(client, Unique("Staffed"));
        var userId = await UserAsync(client);

        var created = await (await client.PostAsJsonAsync(
            "/api/org/positions", new PositionRequest(userId, unit.Id, "Rep")))
            .Content.ReadFromJsonAsync<PositionResponse>();

        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/org/units/{unit.Id}")).StatusCode);

        // …and once the position goes, the unit does. Otherwise the refusal above could just be
        // deletion being broken.
        Assert.Equal(
            HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/org/positions/{created!.Id}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/org/units/{unit.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_position_cannot_be_moved_by_editing_it()
    {
        // Moving someone changes who they report through and what they see. That is a different act
        // from a typo in their title, and the audit trail should show both halves.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var here = await UnitAsync(client, Unique("Here"));
        var there = await UnitAsync(client, Unique("There"));
        var userId = await UserAsync(client);

        var created = await (await client.PostAsJsonAsync(
            "/api/org/positions", new PositionRequest(userId, here.Id, "Rep")))
            .Content.ReadFromJsonAsync<PositionResponse>();

        var moved = await client.PutAsJsonAsync(
            $"/api/org/positions/{created!.Id}", new PositionRequest(userId, there.Id, "Rep"));
        Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);

        var retitled = await client.PutAsJsonAsync(
            $"/api/org/positions/{created.Id}", new PositionRequest(userId, here.Id, "Senior Rep"));
        Assert.Equal(HttpStatusCode.OK, retitled.StatusCode);
    }

    [Fact]
    public async Task Reading_the_staffing_and_changing_it_are_different_capabilities()
    {
        // `viewer` holds position:read and not position:write.
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/org/positions")).StatusCode);

        var write = await viewer.PostAsJsonAsync(
            "/api/org/positions", new PositionRequest(Guid.NewGuid().ToString(), Guid.NewGuid(), "Nope"));

        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task One_tenants_positions_are_invisible_to_another()
    {
        using var tenantA = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var unit = await UnitAsync(tenantA, Unique("Private"));
        var userId = await UserAsync(tenantA);

        var mine = await (await tenantA.PostAsJsonAsync(
            "/api/org/positions", new PositionRequest(userId, unit.Id, "Supervisor")))
            .Content.ReadFromJsonAsync<PositionResponse>();

        var visibleToB = await tenantB.GetFromJsonAsync<List<PositionResponse>>("/api/org/positions");
        Assert.DoesNotContain(visibleToB!, position => position.Id == mine!.Id);

        // `rep-b` deliberately holds position:write, so this is 404 from the query filter rather
        // than 403 from the permission check — otherwise the assertion proves nothing.
        var byId = await tenantB.DeleteAsync($"/api/org/positions/{mine!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);

        // A's user is not resolvable as B's either, so B cannot even name them.
        var scope = await tenantB.GetFromJsonAsync<UserScopeResponse>($"/api/org/users/{userId}/scope");
        Assert.Empty(scope!.Positions);
    }
}
