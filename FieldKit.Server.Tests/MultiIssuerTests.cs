using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Catalog;

namespace FieldKit.Server.Tests;

/// <summary>
/// Multi-issuer validation (ADR-0008): two tenants, two realms, two issuers, two JWKS endpoints.
/// </summary>
/// <remarks>
/// These are the tests that could not exist before a second realm did. With one realm, every
/// assertion about issuer resolution passes whether the issuer is looked up per request or
/// hard-coded — which is exactly why the gap survived as long as it did.
/// </remarks>
[Collection(ServerCollection.Name)]
public class MultiIssuerTests(ServerFixture fixture)
{
    [Fact]
    public async Task A_token_from_the_second_realm_is_accepted()
    {
        // The headline: a different issuer, signed by a different realm's keys, validating against
        // the same API. Under the previous single-authority configuration this was a 401.
        using var client = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var response = await client.GetAsync("/api/auth/whoami");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Each_realms_token_resolves_to_its_own_tenant()
    {
        using var tenantA = fixture.CreateAuthenticatedClient();
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var a = await tenantA.GetFromJsonAsync<WhoAmIResponse>("/api/auth/whoami");
        var b = await tenantB.GetFromJsonAsync<WhoAmIResponse>("/api/auth/whoami");

        Assert.Equal("00000000-0000-0000-0000-000000000001", a!.Tenant);
        Assert.Equal("00000000-0000-0000-0000-000000000002", b!.Tenant);
        Assert.NotEqual(a.Tenant, b.Tenant);
    }

    [Fact]
    public async Task One_tenant_cannot_see_another_tenants_data()
    {
        // The point of all of it. Until now "tenant isolation" was proved at the DbContext level with
        // two fabricated tenant contexts; this is two real tenants, two real tokens, two real realms,
        // over HTTP, through the whole stack.
        var sku = $"ISO-{Guid.NewGuid():N}"[..12];

        using var tenantA = fixture.CreateAuthenticatedClient();
        var created = await tenantA.PostAsJsonAsync("/api/products", new CreateProductRequest(sku, "A's product"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);
        var visibleToB = await tenantB.GetFromJsonAsync<List<ProductResponse>>("/api/products");

        Assert.NotNull(visibleToB);
        Assert.DoesNotContain(visibleToB!, product => product.Sku == sku);

        // …and A still sees it, so the assertion above is not passing against an empty database.
        var visibleToA = await tenantA.GetFromJsonAsync<List<ProductResponse>>("/api/products");
        Assert.Contains(visibleToA!, product => product.Sku == sku);
    }

    [Fact]
    public async Task A_token_whose_issuer_no_tenant_claims_is_rejected()
    {
        // The registry is the trust list, and this is a real token: Keycloak's own `master` realm
        // signed it, the signature verifies, it has not expired. It is refused for one reason — no
        // tenant row claims that realm. That is what stops someone who can create a realm on the
        // identity provider from thereby creating a tenant.
        using var client = fixture.CreateAuthenticatedClient(fixture.UntrustedRealmAccessToken);

        var response = await client.GetAsync("/api/auth/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // The reason, not just the status. A 401 alone would survive a regression that made the
        // registry trust every realm: this token would still be refused, only later and for
        // something else (its `master` audience, its missing `tenant` claim). Naming the issuer in
        // the challenge is what ties the test to the check it claims to cover.
        var challenge = response.Headers.WwwAuthenticate.ToString();
        Assert.Contains("realms/master", challenge);
        Assert.Contains("issuer", challenge);
    }

    [Fact]
    public async Task A_realm_cannot_mint_a_token_for_a_tenant_it_does_not_own()
    {
        // The check that makes multi-issuer safe rather than merely functional, and the only test
        // here that needs a deliberately-hostile realm to exist.
        //
        // Issuer validation proves a token came from a realm we trust. It says nothing about which
        // tenant the token claims to be — and the tenant context reads that claim, so a trusted realm
        // asserting someone else's tenant id would walk straight through the query filter with a
        // complete view of their data. Editing a token's payload cannot demonstrate this, because the
        // signature breaks first and the request is refused for the wrong reason.
        //
        // So realm B carries a second client whose hardcoded tenant claim names tenant A. Its tokens
        // are properly signed by a trusted issuer and fail exactly one check.
        Assert.Contains("00000000-0000-0000-0000-000000000001", TokenPayload(fixture.ImpostorAccessToken));

        using var impostor = fixture.CreateAuthenticatedClient(fixture.ImpostorAccessToken);

        var response = await impostor.GetAsync("/api/auth/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>The claims segment, decoded — for asserting what a token says before sending it.</summary>
    private static string TokenPayload(string jwt)
    {
        var segment = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        var padded = segment.PadRight((segment.Length + 3) / 4 * 4, '=');
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }
}
