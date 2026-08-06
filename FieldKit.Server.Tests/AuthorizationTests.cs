using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Products;

namespace FieldKit.Server.Tests;

/// <summary>
/// Permission-based authorization (<c>IAM-05</c>, <c>BR-IAM-2</c>) and the tenant-isolation
/// guarantee that depends on it, driven over HTTP against a real Keycloak.
/// </summary>
[Collection(ServerCollection.Name)]
public class AuthorizationTests(ServerFixture fixture)
{
    private static CreateProductRequest NewProduct() =>
        new($"SKU-{Guid.NewGuid():N}"[..12], "Veridian Sparkling 1L");

    [Fact]
    public async Task A_permission_the_caller_holds_lets_the_request_through()
    {
        using var client = fixture.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_permission_the_caller_lacks_is_forbidden_not_unauthorized()
    {
        // `viewer` holds product:read but not product:write. The distinction matters: 401 tells a
        // caller to authenticate, which they already have. 403 tells them their role is wrong, which
        // is the truth and is actionable by an admin.
        using var readOnly = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        var read = await readOnly.GetAsync("/api/products");
        var write = await readOnly.PostAsJsonAsync("/api/products", NewProduct());

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task The_permission_check_is_the_reason_the_write_is_refused()
    {
        // Guards against the 403 above being incidental — the same request from a caller that *does*
        // hold product:write must succeed. Without this, deleting the permission role from the realm
        // would leave the suite green.
        using var client = fixture.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/products", NewProduct());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task The_tenant_comes_from_the_token_and_a_crafted_header_cannot_change_it()
    {
        // The tenant context this replaces honoured an `X-Tenant-Id` header. That was harmless while
        // nothing authenticated and is a cross-tenant write primitive the moment something does:
        // the EF global query filter reads whatever the tenant context reports.
        //
        // If the header were still honoured, this row would be stamped with the crafted tenant and
        // the follow-up read — filtered to the token's tenant — would not find it.
        using var client = fixture.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Guid.NewGuid().ToString());

        var product = NewProduct();
        var create = await client.PostAsJsonAsync("/api/products", product);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var visible = await client.GetFromJsonAsync<List<ProductResponse>>("/api/products");

        Assert.NotNull(visible);
        Assert.Contains(visible!, p => p.Sku == product.Sku);
    }
}
