using System.Globalization;
using System.Threading.RateLimiting;
using FieldKit.Web;
using Microsoft.AspNetCore.RateLimiting;

namespace FieldKit.Server;

/// <summary>
/// What one caller may ask for, and how often (<c>security §6</c>, <c>§7</c>) — W13 slice 6.
/// </summary>
/// <remarks>
/// <para>
/// <b>The DoS row of the threat model cited a mitigation that did not exist.</b> §7 answers "sync
/// flooding" with "rate limiting; batch-size limits; scale-to-zero autoscale" — two of those were
/// real (<c>MaximumBatch</c> caps a push at 200 mutations, and the platform does autoscale) and the
/// first was a sentence. This is the sentence.
/// </para>
/// <para>
/// <b>Partitioned per caller, never globally, and that is the whole design.</b>
/// <c>observability §6</c> calls 200 reps reconnecting at shift start a <i>documented normal</i> — the
/// system is specified to absorb it. A global limiter tuned to look sensible on an idle dev box would
/// refuse the one burst the product promises to handle, and it would refuse it at 07:00 on a Monday
/// to whichever reps happened to be last. Per-subject partitions mean two hundred reps are two
/// hundred budgets and do not interact at all.
/// </para>
/// <para>
/// <b>A window rather than a token bucket.</b> A bucket's refill rate is a second number to tune and
/// buys smoothing that nothing here needs: a device syncs in short bursts, then stops. What a window
/// says is simply "one device, this many requests a minute", which is a sentence an operator can
/// hold in their head while reading a 429.
/// </para>
/// </remarks>
public static class RateLimits
{
    /// <summary>
    /// How many sync requests one rep may make a minute.
    /// </summary>
    /// <remarks>
    /// <b>Sixty was the first answer and it was wrong</b>, which the test suite proved by being a
    /// legitimate client that exceeded it. A steady-state reconnect is single digits — a pull, a
    /// push, a photo confirmation — but a <i>first</i> sync is not: binding a device pages ten entity
    /// feeds of up to 500 rows each, and a rep given a new phone or moved between territories does
    /// that from cold. Add the retries a bad connection produces and sixty sits inside the range of
    /// ordinary behaviour, which is the one place a limit must never be.
    /// <para>
    /// Three hundred is five requests a second sustained, per device, for a minute. No real device
    /// approaches it, and a client stuck in a loop is still bounded to a rate a server does not
    /// notice — which is what the number is for.
    /// </para>
    /// </remarks>
    public const int DefaultSyncPerMinute = 300;

    /// <summary>Where a deployment may say otherwise.</summary>
    /// <remarks>
    /// Configurable for one honest reason: a limit is a deployment decision, and the number that
    /// suits a fleet of two hundred phones is not the number that suits a demo. A test benefits
    /// second — it can stand a host up with a limit of two rather than sending sixty requests through
    /// the shared one, which is exactly how the first version of that test exhausted a budget the
    /// rest of the suite was still spending.
    /// </remarks>
    public const string SyncPerMinuteSetting = "RateLimits:SyncPerMinute";

    public static IServiceCollection AddFieldKitRateLimits(
        this IServiceCollection services, IConfiguration configuration)
    {
        var syncPerMinute = configuration.GetValue(SyncPerMinuteSetting, DefaultSyncPerMinute);

        return services.AddRateLimiter(limiter =>
        {
            limiter.AddPolicy(RateLimitPolicies.Sync, context => Window(
                // The subject from the validated token, so a limit follows a rep rather than a
                // network. Falling back to the address matters less than it looks: `/sync` requires
                // authorization, so an unauthenticated request is refused before it ever gets here.
                partition: context.User.FindFirst("sub")?.Value ?? Address(context),
                permits: syncPerMinute));

            limiter.OnRejected = async (context, cancellationToken) =>
            {
                /*
                 * The refusal envelope, because this API has one (`api-contracts §3`).
                 *
                 * The limiter's default is a 429 with an empty body, which a client has to special-case
                 * — and this one already has a branch for `{ "errors": [...] }` with an `ADR-0012`
                 * code. A device can then tell "slow down" from "you are not allowed" without reading
                 * a status code it may not have plumbed through.
                 */
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                // `Retry-After` in seconds, when the limiter knows. A device that respects it stops
                // hammering; one that does not is refused again, which is the same outcome as before.
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                await context.HttpContext.Response.WriteAsJsonAsync(
                    new
                    {
                        errors = new[]
                        {
                            new FieldProblem(
                                null,
                                "Too many requests. Wait a moment and try again.",
                                "request.tooManyRequests"),
                        },
                    },
                    cancellationToken);
            };
        });
    }

    private static RateLimitPartition<string> Window(string partition, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),

            // No queue. A device waiting in a server-side queue is a device holding a connection open
            // to be told the same thing later; being refused now is information it can act on, and a
            // queue on this path is how a burst becomes a thread-pool problem.
            QueueLimit = 0,
        });

    /// <summary>
    /// The caller's address, or a single shared bucket when there is none.
    /// </summary>
    /// <remarks>
    /// <c>RemoteIpAddress</c> is null for an in-process request — every test in this repository, and
    /// anything behind a proxy that has not been told to forward it. Grouping those under one key is
    /// deliberate: it is the conservative reading, and a limiter that silently gave <i>every</i>
    /// unattributable caller its own budget would be a limiter with an opt-out.
    /// </remarks>
    private static string Address(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unattributed";
}
