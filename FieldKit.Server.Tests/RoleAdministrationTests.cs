using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Iam;

namespace FieldKit.Server.Tests;

/// <summary>
/// Roles administration (<c>IAM-04</c>) and the permission catalogue it validates against, driven
/// over HTTP through the real host.
/// </summary>
[Collection(ServerCollection.Name)]
public class RoleAdministrationTests(ServerFixture fixture)
{
    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    private static RoleRequest NewRole(params string[] permissions) =>
        new($"Role {Guid.NewGuid():N}"[..12], permissions);

    [Fact]
    public async Task The_catalogue_lists_permissions_from_every_composed_module()
    {
        using var client = Admin();

        var catalogue = await client.GetFromJsonAsync<List<PermissionResponse>>("/api/iam/permissions");

        Assert.NotNull(catalogue);
        var names = catalogue!.Select(entry => entry.Name).ToList();

        // Contributed by two different modules — which is the property that matters. A catalogue
        // built from one assembly would list IAM's and quietly omit Products'.
        Assert.Contains("product:write", names);
        Assert.Contains("role:write", names);

        // Every entry carries a description: it is what an admin composing a role actually reads.
        Assert.All(catalogue, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Description)));
    }

    [Fact]
    public async Task A_role_can_be_created_read_back_and_deleted()
    {
        using var client = Admin();
        var request = NewRole("product:read");

        var created = await client.PostAsJsonAsync("/api/iam/roles", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var role = await created.Content.ReadFromJsonAsync<RoleResponse>();
        Assert.NotNull(role);
        Assert.Equal(["product:read"], role!.Permissions);

        var listed = await client.GetFromJsonAsync<List<RoleResponse>>("/api/iam/roles");
        Assert.Contains(listed!, r => r.Id == role.Id);

        var deleted = await client.DeleteAsync($"/api/iam/roles/{role.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task A_permission_no_module_enforces_is_rejected()
    {
        // The reason the catalogue exists. `prodcut:read` is a typo that grants nothing; without
        // validation it is stored happily and surfaces months later as "the button does nothing".
        using var client = Admin();

        var response = await client.PostAsJsonAsync("/api/iam/roles", NewRole("prodcut:read"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("prodcut:read", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Permission_matching_is_case_sensitive()
    {
        // Permissions are identifiers. Accepting `Product:Read` for `product:read` would make a
        // typo indistinguishable from a working grant — the exact failure the catalogue prevents.
        using var client = Admin();

        var response = await client.PostAsJsonAsync("/api/iam/roles", NewRole("Product:Read"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Updating_a_role_replaces_its_permissions_wholesale()
    {
        using var client = Admin();

        var created = await client.PostAsJsonAsync("/api/iam/roles", NewRole("product:read", "product:write"));
        var role = await created.Content.ReadFromJsonAsync<RoleResponse>();

        var updated = await client.PutAsJsonAsync(
            $"/api/iam/roles/{role!.Id}", new RoleRequest(role.Name, ["product:read"]));

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var after = await updated.Content.ReadFromJsonAsync<RoleResponse>();
        Assert.Equal(["product:read"], after!.Permissions);
    }

    [Fact]
    public async Task Two_roles_cannot_share_a_name_within_a_tenant()
    {
        using var client = Admin();
        var request = NewRole("product:read");

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/iam/roles", request)).StatusCode);

        var duplicate = await client.PostAsJsonAsync("/api/iam/roles", request);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Role_administration_needs_its_own_permission_not_merely_a_token()
    {
        // `rep` is authenticated and holds both product permissions — and still cannot touch roles.
        // This is what "permission-based, not role-name-based" buys: capabilities are independent,
        // so being trusted with products grants nothing over who may use them.
        using var repClient = fixture.CreateAuthenticatedClient();

        var read = await repClient.GetAsync("/api/iam/roles");
        var write = await repClient.PostAsJsonAsync("/api/iam/roles", NewRole("product:read"));

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task Reading_roles_does_not_imply_being_able_to_change_them()
    {
        // The split between role:read and role:write. Collapsed into one permission, anyone who can
        // see the roles screen could grant themselves anything listed on it. `viewer` holds the read
        // half of both modules and the write half of neither.
        using var client = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        var read = await client.GetAsync("/api/iam/roles");
        var write = await client.PostAsJsonAsync("/api/iam/roles", NewRole("product:read"));

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task Anonymous_callers_get_401_not_403()
    {
        var response = await fixture.Client.GetAsync("/api/iam/roles");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
