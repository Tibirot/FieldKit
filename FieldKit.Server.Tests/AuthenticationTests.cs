using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace FieldKit.Server.Tests;

/// <summary>
/// The API's half of <c>IAM-01</c>: a Keycloak-issued JWT is validated on every call that asks for
/// one. Driven over HTTP through the real host against a real Keycloak, so what is under test is
/// the actual pipeline — signature, issuer, audience and lifetime — not a stub that returns true.
/// </summary>
[Collection(ServerCollection.Name)]
public class AuthenticationTests(ServerFixture fixture)
{
    [Fact]
    public async Task Protected_endpoint_rejects_an_anonymous_request()
    {
        var response = await fixture.Client.GetAsync("/api/auth/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_rejects_a_token_it_cannot_validate()
    {
        // A syntactically plausible bearer value that nothing signed. If this were accepted, every
        // other assertion in this file would be meaningless.
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.token");

        var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_accepts_a_real_Keycloak_token()
    {
        using var client = fixture.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/auth/whoami");

        // On a 401 the JWT middleware puts the reason in WWW-Authenticate ("The signature key was
        // not found", "The issuer is invalid", …). Surfacing it turns a bare Expected/Actual into
        // something diagnosable.
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected OK, got {(int)response.StatusCode}. WWW-Authenticate: "
                + string.Join("; ", response.Headers.WwwAuthenticate));
    }

    [Fact]
    public async Task The_API_reads_tenant_and_permissions_from_the_token()
    {
        using var client = fixture.CreateAuthenticatedClient();

        var identity = await client.GetFromJsonAsync<WhoAmIResponse>("/api/auth/whoami");

        Assert.NotNull(identity);
        Assert.False(string.IsNullOrWhiteSpace(identity!.Subject));

        // The realm's hardcoded `tenant` mapper is what makes realm-per-tenant resolve to a
        // TenantId at all — and this value matches the one DevTenantContext already uses, so the
        // token-derived context can replace it without orphaning existing rows.
        Assert.Equal("00000000-0000-0000-0000-000000000001", identity.Tenant);

        // Realm roles arrive flattened into one claim, so modules check permissions and never role
        // names (BR-IAM-2).
        Assert.Equal(["product:read", "product:write"], identity.Permissions);
    }

    [Fact]
    public void The_token_carries_the_API_audience_rather_than_Keycloaks_default()
    {
        // Guards the realm's audience mapper specifically. Keycloak's default access-token audience
        // is `account`; if this regressed, audience validation in the API would still "pass" while
        // checking nothing useful, and a token minted for any client in the realm would be accepted.
        var audiences = ReadClaim(fixture.AccessToken, "aud");

        Assert.Contains("fieldkit-api", audiences);
    }

    [Fact]
    public async Task Products_stay_anonymous_until_the_tenant_context_is_token_derived()
    {
        // Not an oversight — pinned deliberately. Requiring auth here while ITenantContext is still
        // DevTenantContext would let an authenticated caller carrying a real tenant claim write rows
        // stamped with the dev tenant: authenticated and wrong. This flips in the next slice, and
        // this assertion is meant to flip with it.
        var response = await fixture.Client.GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Reads a claim straight out of the JWT payload. Hand-decoded rather than pulling in a JWT
    /// library the test project does not otherwise need — and which would only be reachable here
    /// through the app's own transitive dependencies.
    /// </summary>
    private static IReadOnlyList<string> ReadClaim(string jwt, string claim)
    {
        var segment = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        var payload = Convert.FromBase64String(segment.PadRight((segment.Length + 3) / 4 * 4, '='));

        var value = JsonDocument.Parse(Encoding.UTF8.GetString(payload)).RootElement.GetProperty(claim);

        return value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(element => element.GetString()!)]
            : [value.GetString()!];
    }
}
