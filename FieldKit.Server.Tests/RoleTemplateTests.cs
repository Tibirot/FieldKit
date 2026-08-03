using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Iam;
using FieldKit.Web;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// System role templates (<c>IAM-06</c>): the roles a tenant is seeded with.
/// </summary>
/// <remarks>
/// A tenant without them has permissions defined and nobody who can hold them — and because role
/// administration is itself permission-guarded, no way to create the first role from inside the
/// product. These run against the seeded dev tenants, so they assert what a real tenant actually
/// starts with rather than what a fixture was arranged to show.
/// </remarks>
[Collection(ServerCollection.Name)]
public class RoleTemplateTests(ServerFixture fixture)
{
    [Fact]
    public void Every_templated_permission_is_one_the_running_system_enforces()
    {
        // The failure this catches is silent: `prodcut:read` in a template saves fine, displays
        // fine, and grants nothing — and it is the tenant's *starting* role, so the first person to
        // notice is a rep who cannot do their job. Asserted against the live catalogue, which is
        // built from the modules actually loaded.
        var catalog = fixture.Services.GetRequiredService<IPermissionCatalog>();

        var unknown = SystemRoleTemplates.All
            .SelectMany(template => template.Permissions)
            .Where(permission => !catalog.Contains(permission))
            .ToList();

        Assert.Empty(unknown);
    }

    [Fact]
    public async Task A_seeded_tenant_starts_with_the_system_roles()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var roles = await client.GetFromJsonAsync<List<RoleResponse>>("/api/iam/roles");

        Assert.NotNull(roles);

        foreach (var template in SystemRoleTemplates.All)
        {
            var seeded = Assert.Single(roles!, role => role.Name == template.Name);

            Assert.True(seeded.IsSystemTemplate);
            Assert.Equal(template.Permissions.Order(StringComparer.Ordinal), seeded.Permissions);
        }
    }

    [Fact]
    public async Task The_templates_are_not_a_hierarchy()
    {
        // The property that makes this a permission model rather than a tier list: the role that
        // administers who may sell holds nothing that lets it sell. If a future template quietly
        // becomes a superset of every other, this is what says so.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var roles = await client.GetFromJsonAsync<List<RoleResponse>>("/api/iam/roles");
        var admin = Assert.Single(roles!, role => role.Name == "Tenant Admin");

        Assert.DoesNotContain(admin.Permissions, permission => permission.StartsWith("product:"));
    }

    [Fact]
    public async Task A_system_template_cannot_be_deleted()
    {
        // Templates are the way back to a working set of roles. Renaming and recomposing them is an
        // admin's business (IAM-04); removing the last one is not, because nothing in the product
        // can create a replacement without a role that grants `role:write`.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var roles = await client.GetFromJsonAsync<List<RoleResponse>>("/api/iam/roles");
        var template = Assert.Single(roles!, role => role.Name == "Tenant Admin");

        var response = await client.DeleteAsync($"/api/iam/roles/{template.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Each_tenant_gets_its_own_set_of_roles()
    {
        // Seeding writes tenant-owned rows from a background service rather than a request — the one
        // place in the system where "which tenant is this?" is not answered by a token. Two distinct
        // failures hide here, and each tenant seeing exactly its own four rules out both: stamping
        // every role with an empty tenant id (nobody would see any), and seeding one shared set
        // (each would see eight).
        using var tenantA = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var a = await tenantA.GetFromJsonAsync<List<RoleResponse>>("/api/iam/roles");
        var b = await tenantB.GetFromJsonAsync<List<RoleResponse>>("/api/iam/roles");

        // Templates only. Asserting the *whole* list would couple this to whatever the role-CRUD
        // tests happen to have created in tenant A first — a failure that depends on test order and
        // says nothing about seeding.
        var templates = SystemRoleTemplates.All.Select(template => template.Name).ToHashSet();

        var seededForA = a!.Where(role => templates.Contains(role.Name)).ToList();
        var seededForB = b!.Where(role => templates.Contains(role.Name)).ToList();

        Assert.Equal(templates.Count, seededForA.Count);
        Assert.Equal(templates.Count, seededForB.Count);

        // Same names, different rows — the roles are per-tenant, not one shared set everyone reads.
        Assert.Empty(seededForA.Select(role => role.Id).Intersect(seededForB.Select(role => role.Id)));
    }
}
