using System.Text.Json.Serialization;
using FieldKit.BuildingBlocks;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FieldKit.Modules.Sync;

/// <summary>
/// What went wrong on a device, as the device saw it (<c>observability §5</c>) — W13 slice 8.
/// </summary>
/// <remarks>
/// <b>A closed vocabulary, not a free-text level.</b> Every value here is a thing the client can
/// detect without judgement — an unhandled rejection, a worker that failed to install, a quota the
/// browser refused — so the server can count them without parsing prose, and a dashboard can name
/// them without a lookup table. A kind this server does not know is refused rather than bucketed:
/// silently accepting one would mean a client shipping a typo reports nothing for a release.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceEventKind>))]
public enum DeviceEventKind
{
    /// <summary>An exception or rejection nothing caught.</summary>
    UnhandledError,

    /// <summary>The service worker failed to install, activate, or fetch.</summary>
    ServiceWorkerFailure,

    /// <summary>The browser refused a write for want of room.</summary>
    StorageQuotaExceeded,

    /// <summary>The browser discarded the local store — the case that loses a day's work.</summary>
    StorageEvicted,

    /// <summary>A sync run gave up. The reason is in <c>detail</c>, and is this server's own code.</summary>
    SyncFailed,
}

/// <summary>
/// One thing that happened on a device.
/// </summary>
/// <param name="OccurredAtUtc">
/// When the device says it happened. Not corrected here: a device with a wrong clock is reporting a
/// real event at a wrong time, and rewriting the time would hide the one thing that would explain a
/// batch of nonsense.
/// </param>
/// <param name="Detail">
/// What the device could say about it — an error message, a refusal code. Optional, capped, and
/// <b>never a place for a location</b>: see <see cref="TelemetryEndpoints"/>.
/// </param>
public sealed record DeviceEvent(
    DeviceEventKind Kind,
    DateTimeOffset OccurredAtUtc,
    string? Detail = null);

/// <param name="DeviceId">Which phone. The batch is refused if it is not one of this rep's.</param>
public sealed record DeviceTelemetryRequest(Guid DeviceId, IReadOnlyList<DeviceEvent> Events);

/// <param name="Accepted">How many events were recorded.</param>
public sealed record DeviceTelemetryResponse(int Accepted);

/// <summary>
/// What a field device says when it is failing quietly (<c>observability §5</c>) — W13 slice 8.
/// </summary>
/// <remarks>
/// <para>
/// <b>The argument is the doc's own: there is no SSH into a field fleet.</b> A rep whose local store
/// was evicted, whose service worker never installed, or whose sync has been failing for a week looks
/// from here exactly like a rep having a quiet week — the same absence of visits, orders and pulls.
/// Every other signal this week measures work that <i>arrived</i>; this is the only one that can say
/// why some did not.
/// </para>
/// <para>
/// <b>It goes to the telemetry pipeline, not to a table.</b> No schema, no migration, no retention
/// policy to invent — a structured log per event, correlated by the same tenant and trace the rest of
/// W13 established, plus a counter so "devices are failing" is alertable rather than merely
/// searchable. A business table would make this data somebody's to keep and to erase; logs already
/// have a lifecycle, and this is diagnostics rather than a record of work.
/// </para>
/// <para>
/// <b>No location, ever</b> (<c>observability §5</c>, <c>security §4</c>), and the enforcement is
/// structural rather than a rule somebody has to remember: <see cref="DeviceEvent"/> has nowhere to
/// put one. A client that sends latitude and longitude has them discarded by the deserializer before
/// any code here sees them — which is a stronger guarantee than validation, because there is no
/// branch to get wrong. What remains a client-side promise is the <i>content</i> of
/// <see cref="DeviceEvent.Detail"/>, and that is stated rather than pretended away.
/// </para>
/// <para>
/// <b>Bound to a device the rep actually has.</b> Telemetry is unauthenticated data in the sense that
/// nothing here is checked against reality — so the batch is refused unless the device is registered
/// to the subject in the token, which is the same gate <c>/sync/push</c> uses. Without it the endpoint
/// is a log-injection hole with a rate limit.
/// </para>
/// </remarks>
public static class TelemetryEndpoints
{
    /// <summary>How many events one batch may carry.</summary>
    /// <remarks>
    /// A device that has been offline for a week and failing all of it still has a handful of
    /// distinct things to report — the same error a thousand times is one line to a reader. Fifty is
    /// generous for the honest case and bounds the dishonest one; a client with more should send the
    /// oldest and keep the rest, because the first failure explains the others.
    /// </remarks>
    public const int MaximumEvents = 50;

    /// <summary>How much a device may say about one event.</summary>
    /// <remarks>
    /// Long enough for a browser's error message and a refusal code, short enough that this is not a
    /// place to put a stack trace — or anything else. A trace from a minified bundle names no source
    /// this server could read anyway.
    /// </remarks>
    public const int MaximumDetail = 500;

    public static void MapTelemetryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/sync/telemetry", async (
            DeviceTelemetryRequest request,
            SyncDbContext db,
            ITenantContext tenant,
            DeviceTelemetryMetrics metrics,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (request.Events.Count > MaximumEvents)
            {
                return Problems.BadRequest(
                    "events",
                    $"A batch carries at most {MaximumEvents} events; send the oldest and keep the rest.",
                    "sync.telemetry.batchTooLarge");
            }

            var device = await db.Devices
                .SingleOrDefaultAsync(candidate => candidate.Id == request.DeviceId, ct);

            // The same answer, and the same reason, as `/sync/push`: unknown and not-yours are one
            // response because a device id is a guessable shape.
            if (device is null || device.UserId != tenant.UserId)
            {
                return Problems.Refuse(
                    StatusCodes.Status404NotFound,
                    "That device is not registered to you.",
                    "sync.telemetry.deviceUnknown");
            }

            /*
             * A deactivated device may still report, unlike a pull and like a drain-push.
             *
             * The interesting telemetry arrives *from* devices something has gone wrong with, and a
             * replaced phone that has been failing to sync for a week is exactly the case worth
             * hearing about. Nothing it says is trusted with anything — these are log lines, not
             * work — so there is no harm to weigh against the diagnosis.
             */

            var logger = loggers.CreateLogger("FieldKit.Device");

            foreach (var recorded in request.Events)
            {
                var detail = Trim(recorded.Detail);

                /*
                 * Logged at Warning, all of them.
                 *
                 * None of these is normal and none is this server failing — a quota refusal or an
                 * evicted store is a device in trouble, which is a thing an operator should see
                 * without going looking and should not be paged for. Error would put a rep's full
                 * disk beside a database outage.
                 */
                logger.LogWarning(
                    "Device {DeviceId} reported {Kind} at {OccurredAtUtc}: {Detail}",
                    device.Id,
                    recorded.Kind,
                    recorded.OccurredAtUtc,
                    detail ?? "(no detail)");

                metrics.Reported(tenant.TenantId, recorded.Kind);
            }

            return Results.Ok(new DeviceTelemetryResponse(request.Events.Count));
        }).RequireAuthorization();
    }

    /// <summary>What the device said, bounded.</summary>
    /// <remarks>
    /// Truncated rather than refused. A batch turned away for one over-long message is a batch whose
    /// other forty-nine events are lost — and the value here is the shape of what is failing, which
    /// survives losing the tail of one sentence.
    /// </remarks>
    private static string? Trim(string? detail) => string.IsNullOrWhiteSpace(detail)
        ? null
        : detail.Length <= MaximumDetail ? detail : detail[..MaximumDetail];
}
