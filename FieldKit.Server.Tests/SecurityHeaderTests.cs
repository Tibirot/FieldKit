using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace FieldKit.Server.Tests;

/// <summary>
/// What every API response carries, and what it deliberately does not (<c>security §6</c>) — W13
/// slice 7.
/// </summary>
/// <remarks>
/// <para>
/// Two questions, and the second is the one the W13 slice 0 audit left open. **What headers does a
/// JSON API owe?** — the front end's proxy sets none on `/api/`, correctly, because everything it
/// does is about rendering a document. And **is this API reachable cross-origin at all?** — the
/// security doc claims "CORS locked to known origins" and there is no CORS in the solution.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class SecurityHeaderTests(ServerFixture fixture)
{
    [Fact]
    public async Task Every_response_says_it_is_not_to_be_sniffed_stored_or_rendered()
    {
        /*
         * `nosniff` is the one that matters for JSON: it makes the declared content type binding
         * rather than a suggestion. The CSP is for the case this API is not supposed to have — a
         * response that renders as HTML, from a proxy's error page or an endpoint returning a string
         * that starts with `<` — where `default-src 'none'` is what stops anything in it executing.
         * And `no-store` because every read here is tenant-scoped: a shared cache keyed on a URL is a
         * cross-tenant read waiting for two people to ask the same question.
         */
        using var rep = fixture.CreateAuthenticatedClient();

        var response = await rep.GetAsync("/api/auth/whoami");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", Single(response, "X-Content-Type-Options"));
        Assert.Equal(SecurityHeaders.ApiContentSecurityPolicy, Single(response, "Content-Security-Policy"));
        Assert.Equal("strict-origin-when-cross-origin", Single(response, "Referrer-Policy"));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task A_response_that_never_reached_an_endpoint_carries_them_too()
    {
        /*
         * The reason the middleware runs first. A 401 is produced by authorization and never sees an
         * endpoint; so is a 429, and so is the exception handler's 500. Headers set *after* the
         * pipeline would miss all three — and those are the responses most likely to be produced by
         * something other than this application's own code.
         */
        var response = await fixture.Client.GetAsync("/api/auth/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("nosniff", Single(response, "X-Content-Type-Options"));
        Assert.Equal(SecurityHeaders.ApiContentSecurityPolicy, Single(response, "Content-Security-Policy"));
    }

    [Fact]
    public async Task A_browser_on_another_origin_is_refused_by_omission()
    {
        /*
         * <b>The claim the slice 0 audit could not settle, settled.</b> `security §6` said "CORS
         * locked to known origins" and this solution contains no `AddCors` and no `UseCors` — so
         * either a control was missing or the sentence was wrong.
         *
         * The sentence was wrong. A browser never reaches this API cross-origin: it calls `/api/*` on
         * the **front end's own origin**, and `proxy.ts` rewrites to the upstream. Same-origin needs
         * no CORS, and adding a policy would mean naming origins that are permitted — which is
         * strictly more permission than none.
         *
         * So the assertion is an *absence*, and it is the load-bearing one: with no
         * `Access-Control-Allow-Origin` in the answer, a browser discards the response whatever the
         * server did with it. The day somebody adds a CORS policy without meaning to, this fails.
         */
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/whoami");

        request.Headers.Add("Origin", "https://not-the-front-end.example");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.AccessToken);

        var response = await fixture.Client.SendAsync(request);

        // The server answers — it has no opinion about the Origin header, and should not have one —
        // and the browser is what refuses to hand the body over.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task A_preflight_is_not_answered_either()
    {
        /*
         * The other half of the same claim. A cross-origin `fetch` carrying an `Authorization` header
         * is never simple, so a browser asks permission first — and this API does not answer, which
         * is the refusal. Asserted separately because a CORS policy added by accident would most
         * likely announce itself here, on a request no endpoint is mapped for.
         */
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/auth/whoami");

        preflight.Headers.Add("Origin", "https://not-the-front-end.example");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization");

        var response = await fixture.Client.SendAsync(preflight);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
    }

    /// <summary>The one value of a header, failing loudly if it was set more than once.</summary>
    /// <remarks>
    /// A header set twice is a header a proxy may join with a comma or pick from arbitrarily — and
    /// two `Content-Security-Policy` values are intersected, so a second one that looks harmless can
    /// silently tighten or contradict the first.
    /// </remarks>
    private static string Single(HttpResponseMessage response, string name) =>
        Assert.Single(response.Headers.GetValues(name));
}
