using System.Text.RegularExpressions;
using FieldKit.BuildingBlocks;
using FieldKit.Modules.Audit.Contracts;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FieldKit.Modules.Sync;

/// <summary>
/// What a device asks for before it uploads a photograph (<c>OFF-08</c>, <c>B5</c>) — W11 slice 12a.
/// </summary>
/// <param name="ObjectKey">
/// The key the device minted when the rep took the picture, <c>audits/{auditId}/{photoId}.jpg</c>.
/// <b>Without a tenant prefix</b> — the device does not know its tenant id and must not be trusted
/// with it either; see <see cref="PhotoEndpoints"/>.
/// </param>
public sealed record PresignRequest(string ObjectKey);

/// <param name="Url">Where to <c>PUT</c> the bytes.</param>
/// <param name="ObjectKey">The full path, tenant prefix included — what a reader fetches it by.</param>
/// <param name="ExpiresAtUtc">When the URL stops working.</param>
public sealed record PresignResponse(string Url, string ObjectKey, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// What a device says once the bytes are in storage (<c>OFF-08</c>, <c>B5</c>) — W11 slice 13a.
/// </summary>
/// <param name="ObjectKeys">
/// Full keys, tenant prefix included — <b>what presign returned</b>, not what the device minted. The
/// device is echoing back a value this API gave it, so nothing here needs to be trusted: a key naming
/// another tenant's object matches no row under the tenant filter.
/// </param>
public sealed record ConfirmPhotosRequest(IReadOnlyList<string> ObjectKeys);

/// <param name="Confirmed">How many references this call moved from expected to arrived.</param>
/// <param name="Unknown">
/// How many keys named nothing yet. Not an error — the audit may still be in the outbox — but worth
/// returning, because it is the one case where confirming again later is worth the device's time.
/// </param>
public sealed record ConfirmPhotosResponse(int Confirmed, int Unknown);

public static partial class PhotoEndpoints
{
    /// <summary>
    /// Long enough for a bad connection, short enough that a leaked URL is worth little.
    /// </summary>
    /// <remarks>
    /// A downscaled JPEG is ~20–200 KB (<c>B5</c>), which is seconds even on a poor signal; fifteen
    /// minutes is generous for a retry or two. The device asks again if it lapses, which costs one
    /// small request — cheaper than widening the window every stolen URL lives in.
    /// </remarks>
    private static readonly TimeSpan UploadWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The only shape a device may ask to write: <c>audits/{guid}/{guid}.jpg</c>.
    /// </summary>
    /// <remarks>
    /// Anchored, and both segments are GUIDs, so nothing a caller sends can traverse (<c>..</c>),
    /// escape the prefix (a leading <c>/</c>), or address anything but a photograph. `LocalPhoto`'s
    /// key generator on the device produces exactly this.
    /// </remarks>
    [GeneratedRegex(
        @"^audits/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\.jpg$")]
    private static partial Regex PhotoKey { get; }

    public static void MapPhotoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        /*
         * A short-lived, write-only URL for one photograph (OFF-08, B5, sync engine §5).
         *
         * <b>The tenant prefix is the server's to write, never the caller's to send.</b> The device
         * asks for `audits/{auditId}/{photoId}.jpg` and the API stores it under
         * `{tenantId}/audits/…`, taking the tenant from the validated token. That is the whole of the
         * isolation: a rep cannot address another tenant's prefix because they never get to spell one
         * — there is no request they can craft that produces a key outside their own.
         *
         * <b>It deliberately does not check that the audit exists.</b> The JSON push and the upload
         * are independent transports and either can win (B5); refusing a photograph whose audit has
         * not landed would fail exactly the case the split exists to support — a rep who sealed an
         * audit on a dead connection and reached signal an hour later. The audit is not consulted, so
         * there is nothing to race.
         *
         * What that costs is real and worth stating: a rep can obtain a URL for an audit id they made
         * up, and write a JPEG nothing references. It is bounded — their own tenant, one blob, fifteen
         * minutes, no read and no delete — and the alternative refuses honest work to prevent litter.
         */
        endpoints.MapPost("/api/sync/photos/presign", async (
            PresignRequest request,
            ITenantContext tenant,
            CancellationToken ct,
            IPhotoStorage? storage = null) =>
        {
            if (storage is null)
            {
                // No storage account is configured for this host. Said as *not implemented* rather
                // than as a server error, because it is a deployment that cannot take photographs
                // rather than one that is broken — and the device should stop asking, not retry.
                return Problems.Refuse(
                    StatusCodes.Status501NotImplemented,
                    "This deployment has no photo storage configured.",
                    "sync.photos.unavailable");
            }

            if (!PhotoKey.IsMatch(request.ObjectKey))
            {
                return Problems.BadRequest(
                    "objectKey",
                    "A photo key looks like audits/{auditId}/{photoId}.jpg.",
                    "sync.photos.malformedKey");
            }

            var presigned = await storage.PresignUploadAsync(
                $"{tenant.TenantId.Value}/{request.ObjectKey}",
                UploadWindow,
                ct);

            return Results.Ok(new PresignResponse(
                presigned.Url.ToString(),
                presigned.ObjectKey,
                presigned.ExpiresAtUtc));
        }).RequireAuthorization();

        /*
         * The other end of the upload: the device tells the server the bytes are there (OFF-08, B5,
         * sync engine §5).
         *
         * <b>Why the server cannot work this out for itself.</b> It never sees the `PUT` — that goes
         * browser to storage on a signature — so without this call the reference in an audit and a
         * photograph that is never coming look identical forever. That is the state W11 shipped in,
         * and it is what makes "synced" and "uploaded" impossible to tell apart on a rep's screen.
         *
         * <b>It takes keys and answers counts.</b> No refusals: the push and the upload are
         * independent transports and either can win, so a key whose audit has not landed yet is
         * ordinary and is reported as `unknown` rather than rejected — the device confirms it again
         * after the next push. Anything a modified client sends that names another tenant's object
         * matches nothing, because every query in Audit runs under the tenant filter.
         *
         * <b>Not idempotency-ledgered</b>, unlike a push. The operation is already idempotent in the
         * only way that matters — the first confirmation wins and a repeat changes nothing — so a
         * ledger entry would be bookkeeping for a call that cannot do harm twice.
         */
        endpoints.MapPost("/api/sync/photos/confirm", async (
            ConfirmPhotosRequest request,
            IPhotoEvidence evidence,
            CancellationToken ct) =>
        {
            if (request.ObjectKeys.Count == 0)
            {
                return Problems.BadRequest(
                    "objectKeys",
                    "Name at least one object key.",
                    "sync.photos.noKeys");
            }

            var confirmation = await evidence.ConfirmUploadedAsync(request.ObjectKeys, ct);

            return Results.Ok(new ConfirmPhotosResponse(confirmation.Confirmed, confirmation.Unknown));
        }).RequireAuthorization();
    }
}
