using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using FieldKit.Modules.Sync;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Testcontainers.Azurite;
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

    /// <summary>A second tenant in its own realm — a different issuer and a different JWKS.</summary>
    private const string RealmB = "fieldkit-dev-b";

    private const string ClientId = "fieldkit-web";

    /// <summary>The client in realm B that hardcodes tenant <b>A</b>'s id — see the realm file.</summary>
    private const string ImpostorClientId = "fieldkit-impostor";

    /// <summary>
    /// Keycloak's own realm. Always present, never a FieldKit tenant — which makes it the honest
    /// source of a genuinely-signed token from an issuer the registry does not trust.
    /// </summary>
    private const string UntrustedRealm = "master";

    // Keycloak's bootstrap admin, set explicitly rather than relying on the container defaults —
    // the container exposes no getter for them, so a default change would silently break the
    // untrusted-issuer test. Local container, torn down with the test run.
    private const string AdminRealmUsername = "admin";
    private const string AdminRealmPassword = "admin";

    /// <summary>Holds both product permissions.</summary>
    private const string RepUsername = "rep";

    /// <summary>Holds the read half of both modules and the write half of neither — what proves 403 is real.</summary>
    private const string ViewerUsername = "viewer";

    /// <summary>
    /// Holds the IAM permissions and none of Products'. The disjointness is the point: it shows a
    /// permission grants exactly its own capability rather than "administrator-ness".
    /// </summary>
    private const string AdminUsername = "admin";

    // Matches the fixture users in the imported realm. Not a secret — see realms/README.md.
    private const string Password = "dev-only-not-a-secret";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    // Pinned to the image Aspire.Hosting.Keycloak runs, so the tests exercise the same Keycloak the
    // AppHost does. If that package moves to a new major, this should move with it — a test passing
    // against a different Keycloak than the app runs is worth less than it looks.
    // Two realms, because multi-issuer validation cannot be proved with one: a single realm passes
    // whether the issuer is resolved per request or hard-coded. `WithRealm` maps a file into
    // /opt/keycloak/data/import/ and Keycloak imports the whole directory, so the second rides
    // alongside as a plain resource mapping.
    private readonly KeycloakContainer _keycloak = new KeycloakBuilder("quay.io/keycloak/keycloak:26.6")
        .WithUsername(AdminRealmUsername)
        .WithPassword(AdminRealmPassword)
        .WithRealm("realms/fieldkit-dev-realm.json")
        .WithResourceMapping(
            new FileInfo("realms/fieldkit-dev-b-realm.json"), "/opt/keycloak/data/import/")
        .Build();

    /*
     * Object storage, because a presigned URL cannot be proved against a fake (`OFF-08`, W11 12a).
     *
     * A third container is a real cost on every test in this collection, and it buys the one thing a
     * stub cannot: that the URL the API mints is actually accepted by a Blob service for a PUT and
     * refused for a GET. Signing is the whole feature, and a hand-rolled double would only ever
     * confirm the shape of a string.
     */
    private readonly AzuriteContainer _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.35.0")
        /*
         * <b>`--skipApiVersionCheck`, and it belongs here rather than in the client.</b>
         *
         * `Azure.Storage.Blobs` 12.28 speaks REST API version `2026-04-06`; Azurite 3.35 knows nothing
         * past its own release and answers `400 The API version … is not supported`. The alternative
         * fix — pinning `BlobClientOptions.ServiceVersion` down to what the emulator understands —
         * would slow the *shipped* client to the emulator's pace, which is the tail wagging the dog:
         * the real service supports the newer version, and development would be dictating production.
         *
         * So the emulator is told to stop checking. What that costs is honest and worth stating: a
         * feature the newer API adds and Azurite has not implemented would fail here differently than
         * it does in Azure. Nothing this slice uses is in that gap — a block-blob PUT and a SAS are
         * as old as the service.
         */
        .WithCommand("--skipApiVersionCheck")
        .Build();

    private WebApplicationFactory<Program> _factory = null!;

    /// <summary>An unauthenticated client — every request is anonymous.</summary>
    public HttpClient Client { get; private set; } = null!;

    /// <summary>Access token for <c>rep</c> — both product permissions.</summary>
    public string AccessToken { get; private set; } = null!;

    /// <summary>Access token for <c>viewer</c> — <c>product:read</c> + <c>role:read</c>, no write anywhere.</summary>
    public string ReadOnlyAccessToken { get; private set; } = null!;

    /// <summary>Access token for <c>admin</c> — role and user permissions, no product permissions.</summary>
    public string AdminAccessToken { get; private set; } = null!;

    /// <summary>
    /// Access token from the <b>second tenant's realm</b> — a different issuer, a different JWKS, and
    /// a different <c>tenant</c> claim. Nothing about it can be validated by the first realm's keys.
    /// </summary>
    public string TenantBAccessToken { get; private set; } = null!;

    /// <summary>
    /// A real, correctly-signed token from realm B whose <c>tenant</c> claim names tenant <b>A</b>.
    /// Everything about it validates except the one thing that matters.
    /// </summary>
    public string ImpostorAccessToken { get; private set; } = null!;

    /// <summary>A real, correctly-signed token from a realm no tenant claims.</summary>
    public string UntrustedRealmAccessToken { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _keycloak.StartAsync(), _azurite.StartAsync());

        // Aspire injects these at runtime; here we supply them before the host reads its config.
        Environment.SetEnvironmentVariable("ConnectionStrings__fieldkitdb", _postgres.GetConnectionString());

        // The same key the AppHost's `WithReference(photos)` writes. Its presence is what makes the
        // host register the blob client and the presign endpoint at all — see `SyncModule`.
        Environment.SetEnvironmentVariable("ConnectionStrings__photos", _azurite.GetConnectionString());

        /*
         * The container, which the app no longer creates for itself (W11 slice 12d).
         *
         * The AppHost declares it — bicep when published, Aspire's emulator hook in development — and
         * neither of those is here, so the fixture does what they do. This is the same bargain as the
         * connection strings above: supply what the orchestrator supplies, then exercise the real
         * code path.
         *
         * The honest cost of the change: nothing in this suite would now notice the AppHost dropping
         * the declaration. `scripts/check-deploy-manifest.mjs` is what covers that, and it asserts the
         * published container carries this exact name.
         */
        await new BlobServiceClient(_azurite.GetConnectionString())
            .GetBlobContainerClient(BlobPhotoStorage.ContainerName)
            .CreateIfNotExistsAsync();

        // What the AppHost sets, and what the CORS rule the API applies at startup is cut from — the
        // browser upload is refused without it, one layer past the Content Security Policy.
        Environment.SetEnvironmentVariable("FIELDKIT_WEB_ORIGIN", "http://localhost:3000");

        // No `ConnectionStrings__cache`. There was one — `localhost:6379,abortConnect=false`, a Redis
        // that has never run in CI — because the app registered a Redis-backed output cache. That
        // registration is gone (see Program.cs), and so is the string: every test in this assembly
        // booting the host without it is what holds the removal in place.

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
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(Environments.Development);

                /*
                 * The sync rate limit is lifted for the shared host, because this suite is not one
                 * rep (W13 slice 6).
                 *
                 * The limiter partitions on the subject in the token, and every test here presents
                 * the same handful of tokens — so a collection making two hundred sync calls in a
                 * minute looks exactly like one device in a loop, which is the thing the limit exists
                 * to stop. Leaving it on made six `SyncPullTests` fail with 429 depending on what had
                 * run before them.
                 *
                 * The rule is not untested: `RateLimitTests` stands up its own host with a limit of
                 * two, which is the only way to exercise a budget without spending one that
                 * everything else shares.
                 */
                builder.UseSetting(RateLimits.SyncPerMinuteSetting, int.MaxValue.ToString());
            });

        Client = _factory.CreateClient();
        AccessToken = await RequestAccessTokenAsync(_keycloak.GetBaseAddress(), RepUsername);
        ReadOnlyAccessToken = await RequestAccessTokenAsync(_keycloak.GetBaseAddress(), ViewerUsername);
        AdminAccessToken = await RequestAccessTokenAsync(_keycloak.GetBaseAddress(), AdminUsername);
        TenantBAccessToken = await RequestAccessTokenAsync(_keycloak.GetBaseAddress(), "rep-b", RealmB);
        ImpostorAccessToken = await RequestAccessTokenAsync(
            _keycloak.GetBaseAddress(), "rep-b", RealmB, ImpostorClientId);
        UntrustedRealmAccessToken = await RequestAccessTokenAsync(
            _keycloak.GetBaseAddress(),
            AdminRealmUsername,
            UntrustedRealm,
            clientId: "admin-cli",
            password: AdminRealmPassword);
    }

    /// <summary>
    /// The running host's services, for assertions HTTP cannot make — notably whether an integration
    /// event actually reached the outbox, which is invisible from the API surface.
    /// </summary>
    public IServiceProvider Services => _factory.Services;

    /// <summary>A second host over the same containers, with settings of its own.</summary>
    /// <remarks>
    /// The containers are the expensive part and this reuses them; what it does not reuse is the
    /// host's in-memory state. That is the point for anything <b>stateful and shared</b> — a rate
    /// limiter's budget most obviously, where a test that spends one in the collection's own host
    /// leaves every later test in the same minute with nothing (W13 slice 6 learned this the hard
    /// way: six  went red).
    /// </remarks>
    public WebApplicationFactory<Program> WithSettings(params (string Key, string Value)[] settings) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);

            foreach (var (key, value) in settings) builder.UseSetting(key, value);
        });

    /// <summary>A client presenting a bearer token — <c>rep</c>'s unless another is given.</summary>
    public HttpClient CreateAuthenticatedClient(string? token = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token ?? AccessToken);
        return client;
    }

    /// <summary>
    /// Mints a token the way a browser never would — the realm's direct-access grant exists so tests
    /// and scripts can get one without driving an OIDC redirect. Real tenant realms disable it.
    /// </summary>
    private static async Task<string> RequestAccessTokenAsync(
        string baseAddress,
        string username,
        string realm = Realm,
        string clientId = ClientId,
        string password = Password)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseAddress) };

        var response = await http.PostAsync(
            $"realms/{realm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = clientId,
                ["username"] = username,
                ["password"] = password,
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
        await Task.WhenAll(
            _postgres.DisposeAsync().AsTask(),
            _keycloak.DisposeAsync().AsTask(),
            _azurite.DisposeAsync().AsTask());
        Environment.SetEnvironmentVariable("ConnectionStrings__fieldkitdb", null);
        Environment.SetEnvironmentVariable("ConnectionStrings__cache", null);
        Environment.SetEnvironmentVariable("ConnectionStrings__photos", null);
        Environment.SetEnvironmentVariable("services__keycloak__http__0", null);
    }
}

/// <summary>Shares one host + containers across every Server test class.</summary>
[CollectionDefinition(Name)]
public sealed class ServerCollection : ICollectionFixture<ServerFixture>
{
    public const string Name = "server";
}
