using FieldKit.Infrastructure.Outbox;
using FieldKit.Modules.Iam;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FieldKit.Server;

/// <summary>
/// What "ready" means for this service (<c>observability §3</c>) — W13 slice 5.
/// </summary>
/// <remarks>
/// <para>
/// <b>Readiness is about dependencies; liveness is about this process.</b> The template shipped one
/// <c>self</c> check tagged <c>live</c> and nothing else, so <c>/health</c> and <c>/alive</c> answered
/// the same question — and a service that had lost its database reported ready and kept taking
/// traffic. These three are what <c>/health</c> is for.
/// </para>
/// <para>
/// <b>None of them is tagged <c>live</c>, deliberately.</b> A liveness probe that fails on a
/// dependency asks the platform to restart a process that is working — and restarting every instance
/// when the database blinks is how a blip becomes an outage. Liveness stays "is this process
/// answering at all".
/// </para>
/// </remarks>
public static class HealthChecks
{
    /// <summary>How stale a dispatcher's last cycle may be before the outbox counts as stopped.</summary>
    /// <remarks>
    /// The dispatcher idles at five seconds, so a minute is twelve missed cycles — long enough that a
    /// slow batch, a paused container or a moment of database contention does not page anybody, short
    /// enough that a stopped loop is noticed before its backlog is a story. It is deliberately not
    /// the alert: <c>fieldkit.outbox.backlog</c> is what says the queue is growing, and this says why.
    /// </remarks>
    public static readonly TimeSpan OutboxSilence = TimeSpan.FromMinutes(1);

    public static IHostApplicationBuilder AddFieldKitHealthChecks(this IHostApplicationBuilder builder)
    {
        builder.Services
            .AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
            .AddCheck<KeycloakHealthCheck>("keycloak", tags: ["ready"])
            .AddCheck<OutboxHealthCheck>("outbox", tags: ["ready"]);

        // The Keycloak check gets its own client so its timeout is its own. Borrowing the default one
        // would mean a probe that waits as long as a token-metadata fetch is allowed to.
        builder.Services
            .AddHttpClient(KeycloakHealthCheck.ClientName)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(5));

        return builder;
    }
}

/// <summary>Can this service reach its database?</summary>
/// <remarks>
/// <para>
/// <b>One check for eleven contexts, because there is one database.</b> Schema-per-module
/// (<c>ADR-0005</c>) means eleven <c>DbContext</c>s over one Postgres, so eleven checks would report
/// one fact eleven times and take eleven connections to do it.
/// </para>
/// <para>
/// <c>CanConnectAsync</c> rather than a query: it opens a connection and closes it, which is the
/// thing being asked. A <c>SELECT</c> against some table would also be testing that the table is
/// there, which is a migration's problem and fails differently.
/// </para>
/// </remarks>
public sealed class PostgresHealthCheck(IamDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("Postgres answered.")
                : HealthCheckResult.Unhealthy("Postgres did not answer.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Postgres could not be reached.", exception);
        }
    }
}

/// <summary>Is Keycloak serving OIDC metadata?</summary>
/// <remarks>
/// <para>
/// <b>Reachability, which is what the doc claims and all this can honestly say.</b> It fetches the
/// <c>master</c> realm's discovery document — a realm every Keycloak has — so a 200 means the process
/// is up <i>and</i> serving OIDC rather than merely accepting TCP. It does not mean a given tenant's
/// realm exists; that is a per-request question and <c>TenantIssuerDirectory</c> answers it.
/// </para>
/// <para>
/// <b>Why it belongs in readiness at all:</b> every authenticated request needs a signing key from
/// this server. An instance that cannot reach Keycloak can serve nothing but its own health, so it
/// should not be in rotation.
/// </para>
/// </remarks>
public sealed class KeycloakHealthCheck(IHttpClientFactory clients, IConfiguration configuration) : IHealthCheck
{
    public const string ClientName = "keycloak-health";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var baseAddress =
            configuration["services:keycloak:https:0"]
            ?? configuration["services:keycloak:http:0"];

        // Degraded rather than unhealthy: a host booted without Keycloak configured is a
        // misconfiguration to shout about, not an instance to restart — and answering "unhealthy"
        // would keep a platform cycling it forever without ever fixing the cause.
        if (string.IsNullOrWhiteSpace(baseAddress))
            return HealthCheckResult.Degraded("No Keycloak address is configured.");

        try
        {
            using var client = clients.CreateClient(ClientName);

            var response = await client.GetAsync(
                $"{baseAddress.TrimEnd('/')}/realms/master/.well-known/openid-configuration",
                cancellationToken);

            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("Keycloak is serving OIDC metadata.")
                : HealthCheckResult.Unhealthy($"Keycloak answered {(int)response.StatusCode}.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Keycloak could not be reached.", exception);
        }
    }
}

/// <summary>Are the outbox dispatchers still running?</summary>
/// <remarks>
/// <para>
/// <b>The check the observability doc asked for before there was a dispatcher to check.</b> §3 has
/// listed "outbox liveness (dispatcher heartbeat)" since the doc was written; slice 0 found nothing
/// running, slice 3 built it, and this is the half that notices when it stops.
/// </para>
/// <para>
/// <b>Silence is the signal.</b> A loop that has died cannot report that it has died, so the
/// dispatcher stamps a time on every completed cycle and this judges the gap. A module that has never
/// beaten is <b>not</b> a failure: the host may have started seconds ago, and a readiness check that
/// fails during start-up keeps an instance out of rotation for a reason that will pass on its own.
/// </para>
/// </remarks>
public sealed class OutboxHealthCheck(OutboxHeartbeat heartbeat, IClock clock) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;

        var stopped = heartbeat.Beats()
            .Where(beat => now - beat.Value > HealthChecks.OutboxSilence)
            .Select(beat => beat.Key)
            .Order()
            .ToList();

        return Task.FromResult(stopped.Count == 0
            ? HealthCheckResult.Healthy("Every outbox dispatcher has reported recently.")
            : HealthCheckResult.Unhealthy(
                $"No outbox cycle in {HealthChecks.OutboxSilence.TotalSeconds:0}s for: {string.Join(", ", stopped)}."));
    }
}
