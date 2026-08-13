using System.Net;
using System.Net.Http.Json;
using FieldKit.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// A short-lived, tenant-scoped URL for one shelf photograph (<c>OFF-08</c>, <c>B5</c>) — W11 12a.
/// </summary>
/// <remarks>
/// <para>
/// Against a real Blob service (Azurite), because the feature <b>is</b> the signature. A test double
/// handing back a string would confirm the shape of a URL and nothing about whether storage accepts
/// it — and "the API mints a URL that does not work" is the failure this endpoint exists to avoid.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public sealed class PhotoPresignTests(ServerFixture fixture)
{
    /// <summary>
    /// The origin the fixture tells the API to allow, which is what a browser would send.
    /// </summary>
    /// <remarks>
    /// The same value the AppHost sets in development; the CORS rule is cut from it at startup.
    /// </remarks>
    private const string WebOrigin = "http://localhost:3000";

    private static string SomeKey() => $"audits/{Guid.NewGuid()}/{Guid.NewGuid()}.jpg";

    private sealed record Presigned(string Url, string ObjectKey, DateTimeOffset ExpiresAtUtc);

    [Fact]
    public async Task Refuses_an_anonymous_caller()
    {
        // The bytes are a rep's work and the URL is a capability. Nothing here is public.
        var response = await fixture.Client.PostAsJsonAsync(
            "/api/sync/photos/presign",
            new { objectKey = SomeKey() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    // Traversal, which is the attack this shape exists to make impossible.
    [InlineData("audits/../../secrets/key.jpg")]
    // An absolute path, which would escape the tenant prefix by concatenation.
    [InlineData("/audits/019ff9ee-0de7-7ed9-8854-88e457c80f25/019ff9ee-0de7-7ed9-8854-88e457c80f26.jpg")]
    // Another tenant's prefix, spelled out. The device never sends one; this is somebody trying.
    [InlineData("00000000-0000-0000-0000-000000000001/audits/a/b.jpg")]
    // The right shape with the wrong extension — the key promises a JPEG and `downscale` writes one.
    [InlineData("audits/019ff9ee-0de7-7ed9-8854-88e457c80f25/019ff9ee-0de7-7ed9-8854-88e457c80f26.exe")]
    // Not GUIDs. Free-form segments are how a key stops being addressable by anything but luck.
    [InlineData("audits/mine/photo.jpg")]
    [InlineData("")]
    public async Task Refuses_a_key_that_is_not_a_photograph(string objectKey)
    {
        var client = fixture.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            "/api/sync/photos/presign",
            new { objectKey });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Writes_the_tenant_prefix_itself_rather_than_taking_one()
    {
        /*
         * <b>The whole of the isolation.</b> The device sends `audits/{auditId}/{photoId}.jpg` and
         * never a tenant — it does not know its tenant id, and must not be trusted with one either.
         * The API prefixes what the validated token says.
         *
         * So there is no request a rep can craft that produces a key outside their own tenant: the
         * prefix is not something they can influence, only something the server writes.
         */
        var client = fixture.CreateAuthenticatedClient();
        var key = SomeKey();

        var response = await client.PostAsJsonAsync("/api/sync/photos/presign", new { objectKey = key });
        var presigned = await response.Content.ReadFromJsonAsync<Presigned>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.EndsWith($"/{key}", presigned!.ObjectKey, StringComparison.Ordinal);
        Assert.NotEqual(key, presigned.ObjectKey);
    }

    [Fact]
    public async Task Gives_two_tenants_different_prefixes_for_the_same_key()
    {
        /*
         * The same object key from two realms. If the prefix came from anywhere but the token, these
         * would collide — one tenant's photograph overwriting another's, which is the worst outcome
         * this design has to exclude.
         */
        var key = SomeKey();

        var mine = await fixture.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/sync/photos/presign", new { objectKey = key });
        var theirs = await fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken)
            .PostAsJsonAsync("/api/sync/photos/presign", new { objectKey = key });

        var ours = await mine.Content.ReadFromJsonAsync<Presigned>();
        var others = await theirs.Content.ReadFromJsonAsync<Presigned>();

        Assert.NotEqual(ours!.ObjectKey, others!.ObjectKey);
        Assert.EndsWith($"/{key}", ours.ObjectKey, StringComparison.Ordinal);
        Assert.EndsWith($"/{key}", others.ObjectKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mints_a_url_object_storage_actually_accepts()
    {
        /*
         * <b>The test the double could not have written.</b> Everything above is about strings; this
         * is whether the signature works — the API's only real promise, since a rep whose upload is
         * refused has a photograph that never leaves the phone.
         *
         * `x-ms-blob-type` is required by the Blob REST API for a block blob; the device sends it too.
         */
        var client = fixture.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            "/api/sync/photos/presign",
            new { objectKey = SomeKey() });

        var presigned = await response.Content.ReadFromJsonAsync<Presigned>();

        using var direct = new HttpClient();
        using var content = new ByteArrayContent([1, 2, 3, 4]);
        content.Headers.Add("x-ms-blob-type", "BlockBlob");

        var upload = await direct.PutAsync(presigned!.Url, content);

        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
    }

    [Fact]
    public async Task Mints_a_url_that_cannot_read_anything_back()
    {
        /*
         * Write, and only write. A URL that could also `GET` would let a device — or whoever obtained
         * the URL from it — fetch a photograph, and a rep's phone has no business reading evidence
         * back out of storage. The permission is the narrowest that does the job.
         */
        var client = fixture.CreateAuthenticatedClient();

        var presigned = await (await client.PostAsJsonAsync(
            "/api/sync/photos/presign",
            new { objectKey = SomeKey() })).Content.ReadFromJsonAsync<Presigned>();

        using var direct = new HttpClient();
        using var content = new ByteArrayContent([1, 2, 3, 4]);
        content.Headers.Add("x-ms-blob-type", "BlockBlob");

        await direct.PutAsync(presigned!.Url, content);

        var read = await direct.GetAsync(presigned.Url);

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
    }

    [Fact]
    public async Task Mints_a_url_that_writes_one_blob_and_no_other()
    {
        /*
         * <b>The claim the comments made and nothing checked</b> — found by a sabotage pass that
         * widened the SAS from one blob (<c>Resource = "b"</c>) to the whole container and watched
         * every test still pass.
         *
         * It matters because the URL leaves this server. A container-scoped signature would let
         * whoever holds it write *anywhere* under the tenant, including over a photograph attached to
         * an audit that has already been filed — evidence replaced after the fact, by a device that
         * only ever asked to upload one picture.
         *
         * Same signature, different path: the query is what authorises, and it must not authorise
         * this.
         */
        var client = fixture.CreateAuthenticatedClient();

        var presigned = await (await client.PostAsJsonAsync(
            "/api/sync/photos/presign",
            new { objectKey = SomeKey() })).Content.ReadFromJsonAsync<Presigned>();

        var mine = new Uri(presigned!.Url);
        var somebodyElses = new UriBuilder(mine)
        {
            Path = $"/devstoreaccount1/photos/{Guid.NewGuid()}/audits/{Guid.NewGuid()}/{Guid.NewGuid()}.jpg",
        }.Uri;

        using var direct = new HttpClient();
        using var content = new ByteArrayContent([1, 2, 3, 4]);
        content.Headers.Add("x-ms-blob-type", "BlockBlob");

        var elsewhere = await direct.PutAsync(somebodyElses, content);

        Assert.Equal(HttpStatusCode.Forbidden, elsewhere.StatusCode);
    }

    [Fact]
    public async Task Lets_a_browser_ask_permission_before_uploading()
    {
        /*
         * <b>The wall a browser check found after the Content Security Policy came down.</b>
         *
         * The upload carries `x-ms-blob-type`, which makes the `PUT` non-simple, so a browser sends
         * `OPTIONS` first and storage answers only if a CORS rule names the calling origin. Nothing
         * did, so every upload failed *after* the policy allowed it — presign succeeded, bytes never
         * moved, retry hid it.
         *
         * Simulated exactly as a browser does it: the preflight is a plain `OPTIONS` with the origin
         * and the intended method and headers, and it must come back allowing them.
         */
        var presigned = await (await fixture.CreateAuthenticatedClient().PostAsJsonAsync(
            "/api/sync/photos/presign",
            new { objectKey = SomeKey() })).Content.ReadFromJsonAsync<Presigned>();

        using var direct = new HttpClient();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, presigned!.Url);

        preflight.Headers.Add("Origin", WebOrigin);
        preflight.Headers.Add("Access-Control-Request-Method", "PUT");
        preflight.Headers.Add("Access-Control-Request-Headers", "x-ms-blob-type,content-type");

        var answer = await direct.SendAsync(preflight);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(WebOrigin, answer.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Does_not_let_a_browser_read_a_photograph_back()
    {
        /*
         * The CORS rule allows `PUT` and `OPTIONS` and nothing else, which is the same narrowness the
         * SAS has. A rule that allowed `GET` would undo the presigned URL being write-only — from a
         * different direction, and without touching the signature that makes it so.
         */
        var presigned = await (await fixture.CreateAuthenticatedClient().PostAsJsonAsync(
            "/api/sync/photos/presign",
            new { objectKey = SomeKey() })).Content.ReadFromJsonAsync<Presigned>();

        using var direct = new HttpClient();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, presigned!.Url);

        preflight.Headers.Add("Origin", WebOrigin);
        preflight.Headers.Add("Access-Control-Request-Method", "GET");

        var answer = await direct.SendAsync(preflight);

        Assert.NotEqual(HttpStatusCode.OK, answer.StatusCode);
    }

    [Fact]
    public async Task Says_when_the_url_stops_working()
    {
        // Returned rather than left implicit: the device decides whether an upload is worth starting,
        // and a rep who has just walked into a chiller aisle may not have fifteen minutes of signal.
        var client = fixture.CreateAuthenticatedClient();

        var presigned = await (await client.PostAsJsonAsync(
            "/api/sync/photos/presign",
            new { objectKey = SomeKey() })).Content.ReadFromJsonAsync<Presigned>();

        // The host's own clock, not the test's: the architecture gate bans static time here for the
        // same reason it does in the module, and asserting against the clock the signature was cut
        // from is the stronger claim anyway.
        var now = fixture.Services.GetRequiredService<IClock>().UtcNow;

        Assert.InRange(presigned!.ExpiresAtUtc, now.AddMinutes(10), now.AddMinutes(20));
    }
}
