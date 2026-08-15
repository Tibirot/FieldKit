using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FieldKit.Modules.Sync;
using FieldKit.Server;
using FieldKit.Web;

namespace FieldKit.Server.Tests;

/// <summary>
/// What one caller may ask for, and how often (<c>security §6</c>, <c>§7</c>) — W13 slice 6.
/// </summary>
/// <remarks>
/// <para>
/// <b>The interesting assertion is the one that passes.</b> A limiter is easy to prove works — send
/// too much, read a 429 — and the failure that costs something is the opposite: a limit tuned on an
/// idle machine that refuses the one burst the product promises to absorb.
/// <c>observability §6</c> calls 200 reps reconnecting at shift start a documented normal, so a
/// shared budget would go off at 07:00 on a Monday and refuse whichever reps happened to be last.
/// </para>
/// <para>
/// <b>On a host of its own, and the first version of this file is why.</b> It exhausted the rep's
/// budget in the collection's shared host — and a fixed window is a minute wide, so the six
/// <c>SyncPullTests</c> that ran next were refused. A limiter's budget is state, shared and slow to
/// recover; testing it in a host everything else uses is testing it in production's shape and
/// breaking production's neighbours. The derived host reuses the containers and nothing else.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class RateLimitTests(ServerFixture fixture)
{
    /// <summary>Small enough to reach in three requests, so the test is about the rule, not the load.</summary>
    private const int Permits = 2;

    [Fact]
    public async Task One_rep_exhausting_their_minute_does_not_touch_another_s()
    {
        /*
         * Everything this slice decides, in one run.
         *
         * The budget is partitioned on the subject in the validated token, so two hundred reps
         * reconnecting at once are two hundred budgets that do not interact — which is what makes a
         * per-minute limit safe to set low enough to mean anything. Asserted with two real tokens
         * rather than by reading the partition key back: the key is an implementation detail, and
         * "these two callers are independent" is the promise.
         *
         * Driven through `/api/sync/push` with a device that does not exist, which answers 404. Past
         * authorization, so the limiter sees it, and it writes nothing.
         */
        using var host = fixture.WithSettings((RateLimits.SyncPerMinuteSetting, Permits.ToString()));

        using var rep = Authenticated(host, fixture.AccessToken);
        using var admin = Authenticated(host, fixture.AdminAccessToken);

        for (var spent = 0; spent < Permits; spent++)
            Assert.Equal(HttpStatusCode.NotFound, (await PushAsync(rep)).StatusCode);

        var refused = await PushAsync(rep);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);

        /*
         * The refusal arrives in this API's own envelope. The limiter's default is a 429 with an
         * empty body; every other refusal here is `{ "errors": [...] }` with an `ADR-0012` code
         * (`api-contracts §3`), and a device already has one branch for that — so "slow down" comes
         * through the same door rather than as a status code a client may not have plumbed through.
         */
        Assert.Equal("request.tooManyRequests", Assert.Single(await Refusals.ProblemsOf(refused)).Code);

        // And when to come back, which is the one thing a well-behaved client can act on.
        Assert.True(refused.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.True(int.TryParse(Assert.Single(retryAfter), out var seconds) && seconds > 0);

        // The other rep is untouched. This is the assertion the slice exists to keep true, and the
        // one that fails if anybody ever "simplifies" the partition into a global limiter.
        Assert.Equal(HttpStatusCode.NotFound, (await PushAsync(admin)).StatusCode);
    }

    [Fact]
    public void The_policy_a_module_asks_for_is_the_one_the_host_registers()
    {
        /*
         * ASP.NET throws on an unknown policy name at <b>request</b> time, not at start-up — so a
         * typo in `RequireRateLimiting` ships and surfaces as a 500 on whichever endpoint nobody
         * exercised. The constant exists for that reason.
         *
         * Weak on its own and deliberately cheap: the test above is what proves the policy is
         * attached to anything. This one catches the rename that leaves it attached to nothing.
         */
        Assert.Equal("sync", RateLimitPolicies.Sync);
    }

    private static HttpClient Authenticated(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, string token)
    {
        var client = host.CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    private static Task<HttpResponseMessage> PushAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/sync/push", new PushRequest(Guid.CreateVersion7(), []));
}
