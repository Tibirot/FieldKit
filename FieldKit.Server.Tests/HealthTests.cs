using System.Net;
using System.Text.Json;
using FieldKit.Infrastructure.Outbox;
using FieldKit.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FieldKit.Server.Tests;

/// <summary>
/// What <c>/health</c> and <c>/alive</c> answer, and what they say while doing it
/// (<c>observability §3</c>) — W13 slice 5.
/// </summary>
/// <remarks>
/// <para>
/// Two things under test, and they pull in opposite directions. **Readiness has to be honest** — an
/// instance that cannot reach its database must not report ready — and **the answer has to be terse
/// outside development**, because the default body is a list of a service's dependencies and how
/// they fail. Testing one without the other would let either regress.
/// </para>
/// <para>
/// <b>And the redaction turned out to be the framework default.</b> ASP.NET already writes one word;
/// what was missing was the detail Development wants, so the custom writer is the *detailed* one and
/// the terse path is the default left alone. Both are asserted directly against a report rather than
/// by standing a second host up in Production — the thing that could regress is what gets written.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class HealthTests(ServerFixture fixture)
{
    [Fact]
    public async Task Readiness_names_the_dependencies_it_checked()
    {
        /*
         * The slice, from a probe's point of view. Before it, `/health` ran one `self` check that
         * returns Healthy unconditionally — so a service with no database answered ready and kept
         * taking traffic until a request failed.
         */
        var response = await fixture.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var checks = body.RootElement.GetProperty("entries").EnumerateObject()
            .Select(entry => entry.Name)
            .Order()
            .ToList();

        /*
         * A superset assertion rather than an exact one, and the reason is a small discovery:
         * `AddAzureBlobServiceClient` registers its own `Azure_BlobServiceClient` check, so the set
         * depends on whether the host was given storage. Pinning the whole list would make this test
         * fail for a host booted without photographs — a configuration `SyncModule` deliberately
         * supports — while telling nobody anything about the three checks this slice added.
         */
        Assert.Contains("postgres", checks);
        Assert.Contains("keycloak", checks);
        Assert.Contains("outbox", checks);

        // And the template's own, still tagged `live` and still the only thing `/alive` runs.
        Assert.Contains("self", checks);
    }

    [Fact]
    public async Task Liveness_asks_only_about_this_process()
    {
        /*
         * A liveness probe that fails on a dependency asks the platform to restart a service that is
         * working — and restarting every instance when the database blinks is how a blip becomes an
         * outage. So none of the three dependency checks carries the `live` tag, and this is what
         * says so: `/alive` runs `self` and nothing else.
         */
        var response = await fixture.Client.GetAsync("/alive");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(
            ["self"],
            body.RootElement.GetProperty("entries").EnumerateObject().Select(entry => entry.Name));
    }

    [Fact]
    public async Task The_outbox_check_reads_silence_as_a_stopped_dispatcher()
    {
        /*
         * The check `observability §3` asked for before there was a dispatcher to check. A loop that
         * has died cannot report that it has died, so the dispatcher stamps every completed cycle and
         * this judges the gap.
         *
         * Driven through the heartbeat rather than by stopping a real dispatcher: what is under test
         * is the *judgement*, and a test that killed a background service would be asserting the
         * host's shutdown semantics instead.
         */
        var heartbeat = fixture.Services.GetRequiredService<OutboxHeartbeat>();
        var clock = fixture.Services.GetRequiredService<IClock>();

        var check = new OutboxHealthCheck(heartbeat, clock);
        var context = new HealthCheckContext();

        // Long enough ago to count as stopped. `Beat` is keyed by module, so this leaves the real
        // dispatchers' own entries alone — and the name is one no module has.
        heartbeat.Beat("TestOnly", clock.UtcNow - HealthChecks.OutboxSilence - TimeSpan.FromSeconds(1));

        var stopped = await check.CheckHealthAsync(context);

        Assert.Equal(HealthStatus.Unhealthy, stopped.Status);
        Assert.Contains("TestOnly", stopped.Description);

        // And a cycle just now clears it, which is what makes the check a heartbeat rather than a
        // latch: a dispatcher that recovers is healthy again without anybody resetting anything.
        heartbeat.Beat("TestOnly", clock.UtcNow);

        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(context)).Status);
    }

    [Fact]
    public async Task A_module_that_has_never_reported_is_not_a_failure()
    {
        /*
         * A readiness check that fails during start-up keeps an instance out of rotation for a reason
         * that will pass on its own — and on a platform that gives up after N failed probes, a slow
         * boot becomes a revision that never goes healthy.
         *
         * So "no heartbeat yet" is silence to wait through, not silence to report. This is asserted
         * against a heartbeat with nothing in it at all, which is precisely the first second of a
         * process's life.
         */
        var check = new OutboxHealthCheck(
            new OutboxHeartbeat(), fixture.Services.GetRequiredService<IClock>());

        Assert.Equal(
            HealthStatus.Healthy,
            (await check.CheckHealthAsync(new HealthCheckContext())).Status);
    }

    [Fact]
    public async Task Outside_development_the_answer_is_one_word()
    {
        /*
         * The template mapped these in Development only, and the reasoning was sound: the default body
         * names every check, its duration and its exception — a description of a service's
         * dependencies handed to anyone who can reach the port. What it did not survive is W15, where
         * Container Apps probes an endpoint to decide whether an instance may take traffic.
         *
         * So both are mapped everywhere and the *body* is what changes. The status code does not, so
         * a platform behaves identically in both.
         */
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["postgres"] = new(
                    HealthStatus.Unhealthy,
                    "Postgres could not be reached at fieldkit-db.postgres.database.azure.com.",
                    TimeSpan.Zero,
                    exception: new InvalidOperationException("password authentication failed"),
                    data: null),
            },
            TimeSpan.Zero);

        var written = await WriteAsync(report, detailed: false);

        Assert.Equal("Unhealthy", written);

        // Everything a probe reads, and nothing else: not the host it failed to reach, not why.
        Assert.DoesNotContain("postgres", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", written, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task In_development_the_detail_stays()
    {
        // Because that is where somebody is looking at it with their own eyes, and "Unhealthy" with
        // no further comment is a worse first morning than a stack trace.
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["postgres"] = new(HealthStatus.Unhealthy, "Postgres could not be reached.", TimeSpan.Zero, null, null),
            },
            TimeSpan.Zero);

        var written = await WriteAsync(report, detailed: true);

        Assert.Contains("postgres", written, StringComparison.Ordinal);
        Assert.Contains("could not be reached", written, StringComparison.Ordinal);
    }

    /// <summary>Runs the response writer the host would use and returns what it wrote.</summary>
    private static async Task<string> WriteAsync(HealthReport report, bool detailed)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var body = new MemoryStream();

        context.Response.Body = body;

        await Microsoft.Extensions.Hosting.Extensions.HealthResponseWriter(detailed)(context, report);

        body.Position = 0;

        return await new StreamReader(body).ReadToEndAsync();
    }
}
