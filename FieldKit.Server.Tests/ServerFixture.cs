using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Testcontainers.Keycloak;
using Testcontainers.PostgreSql;

namespace FieldKit.Server.Tests;

/// <summary>
/// Boots the real Server host once against a real Postgres <b>and</b> a real Keycloak.
/// </summary>
/// <remarks>
/// <para>
/// Shared across the whole test class collection because container startup dominates the runtime:
/// two suites each starting their own Postgres and Keycloak would roughly double it for no extra
/// confidence. The two containers start concurrently for the same reason.
/// </para>
/// <para>
/// Keycloak imports <b>the same realm file the AppHost imports</b> (linked into the output by the
/// csproj), not a test-shaped copy. That is what makes these tests able to fail: drop the audience
/// mapper or rename a permission role in the real realm and the assertions below break.
/// </para>
/// </remarks>
public sealed class ServerFixture : IAsyncLifetime
{
    private const string Realm = "fieldkit-dev";
    private const string ClientId = "fieldkit-web";
    private const string Username = "rep";

    // Matches the fixture user in the imported realm. Not a secret — see realms/README.md.
    private const string Password = "dev-only-not-a-secret";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    // Pinned to the image Aspire.Hosting.Keycloak runs, so the tests exercise the same Keycloak the
    // AppHost does. If that package moves to a new major, this should move with it — a test passing
    // against a different Keycloak than the app runs is worth less than it looks.
    private readonly KeycloakContainer _keycloak = new KeycloakBuilder("quay.io/keycloak/keycloak:26.6")
        .WithRealm("realms/fieldkit-dev-realm.json")
        .Build();

    private WebApplicationFactory<Program> _factory = null!;

    /// <summary>An unauthenticated client — every request is anonymous.</summary>
    public HttpClient Client { get; private set; } = null!;

    /// <summary>The raw access token for the realm's fixture user.</summary>
    public string AccessToken { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _keycloak.StartAsync());

        // Aspire injects these at runtime; here we supply them before the host reads its config.
        Environment.SetEnvironmentVariable("ConnectionStrings__fieldkitdb", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("ConnectionStrings__cache", "localhost:6379,abortConnect=false");

        // The app resolves the Keycloak authority through Aspire service discovery
        // ("https+http://keycloak"), which reads these config keys — the AppHost's
        // `WithReference(keycloak)` is what populates them at runtime. Supplying them here points
        // the *real* resolution path at the test container, so the JWT pipeline under test is the
        // one that ships rather than one re-configured to be testable.
        //
        // Overriding JwtBearerOptions instead does not work cleanly: `Authority` alone is ignored
        // because the handler keeps the ConfigurationManager already built from Aspire's metadata
        // address, and it fails as "The issuer '…' is invalid" — a configuration problem wearing a
        // token problem's error message.
        Environment.SetEnvironmentVariable(
            "services__keycloak__http__0", _keycloak.GetBaseAddress().TrimEnd('/'));

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment(Environments.Development));

        Client = _factory.CreateClient();
        AccessToken = await RequestAccessTokenAsync(_keycloak.GetBaseAddress());
    }

    /// <summary>A client that presents the fixture user's bearer token on every request.</summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        return client;
    }

    /// <summary>
    /// Mints a token the way a browser never would — the realm's direct-access grant exists so tests
    /// and scripts can get one without driving an OIDC redirect. Real tenant realms disable it.
    /// </summary>
    private static async Task<string> RequestAccessTokenAsync(string baseAddress)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseAddress) };

        var response = await http.PostAsync(
            $"realms/{Realm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = ClientId,
                ["username"] = Username,
                ["password"] = Password,
            }));

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>();
        return payload?.AccessToken ?? throw new InvalidOperationException("Keycloak returned no access token.");
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken);

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _keycloak.DisposeAsync().AsTask());
        Environment.SetEnvironmentVariable("ConnectionStrings__fieldkitdb", null);
        Environment.SetEnvironmentVariable("ConnectionStrings__cache", null);
        Environment.SetEnvironmentVariable("services__keycloak__http__0", null);
    }
}

/// <summary>Shares one host + containers across every Server test class.</summary>
[CollectionDefinition(Name)]
public sealed class ServerCollection : ICollectionFixture<ServerFixture>
{
    public const string Name = "server";
}
