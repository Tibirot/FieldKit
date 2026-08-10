using FieldKit.BuildingBlocks;
using FieldKit.Modules.Org.Contracts;
using FieldKit.Modules.Outlets.Contracts;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Sync;

public static class PullEndpoints
{
    /// <summary>
    /// How many rows one pull may carry per entity type. Small enough to arrive over a bad
    /// connection, large enough that a first sync is not a hundred round trips.
    /// </summary>
    private const int PageLimit = 500;

    public static void MapPullEndpoints(this IEndpointRouteBuilder endpoints)
    {
        /*
         * The reference delta (OFF-03, sync engine §3).
         *
         * The order of the checks is the design. A device that is not the rep's active one is
         * refused *before* its scope is resolved — resolving scope means reading Organization's
         * assignments and territory membership, and a revoked phone must not be able to make the
         * server do that work, nor learn anything from how long the refusal took.
         */
        endpoints.MapPost("/api/sync/pull", async (
            PullRequest request,
            SyncDbContext db,
            IRepScope repScope,
            IReferenceChangeFeed outlets,
            ITenantContext tenant,
            IClock clock,
            CancellationToken ct) =>
        {
            var device = await db.Devices
                .SingleOrDefaultAsync(candidate => candidate.Id == request.DeviceId, ct);

            // Unknown and not-yours are the same answer on purpose. A device id is a guessable
            // shape, and distinguishing "no such device" from "someone else's device" would let a
            // caller enumerate them.
            if (device is null || device.UserId != tenant.UserId)
            {
                return Problems.Refuse(
                    StatusCodes.Status404NotFound,
                    "That device is not registered to you.",
                    "sync.pull.deviceUnknown");
            }


            if (!device.IsActive)
            {
                // The rep re-bound elsewhere, or an administrator revoked this one. The client's job
                // is to bind again, which re-snapshots from zero — hence a code rather than prose.
                return Problems.Conflict(
                    field: null,
                    "This device is no longer the active one for your account. Bind it again to sync.",
                    "sync.pull.deviceInactive");
            }

            var coverage = await repScope.ForRepAsync(
                tenant.UserId, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), ct);

            var page = await outlets.GetChangesAsync(
                request.Cursors?.Outlets ?? 0, coverage.OutletIds, PageLimit, ct);

            return Results.Ok(new PullResponse(
                new PullChanges(new EntityChanges<OutletSnapshot>(page.Upserts, page.Tombstones, page.Cursor)),
                // A patchwork, not a point in time: watermarks advance per entity type, and the
                // device tolerates the skew because captured work records its own inputs
                // (sync engine §3). With one entity type there is nothing to skew yet.
                $"{clock.UtcNow:O}#{page.Cursor}"));
        }).RequireAuthorization();
    }
}

/// <param name="Cursors">
/// Absent means "I have nothing" — a first pull, or a rebind after a swap, and the server answers
/// with everything in scope rather than an empty delta.
/// </param>
public sealed record PullRequest(Guid DeviceId, PullCursors? Cursors);

public sealed record PullCursors(long? Outlets);

public sealed record EntityChanges<T>(
    IReadOnlyList<T> Upserts, IReadOnlyList<ReferenceTombstone> Tombstones, long Cursor);

public sealed record PullChanges(EntityChanges<OutletSnapshot> Outlets);

public sealed record PullResponse(PullChanges Changes, string SnapshotVersion);
