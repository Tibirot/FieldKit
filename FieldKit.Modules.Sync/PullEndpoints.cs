using FieldKit.BuildingBlocks;
using FieldKit.Modules.Journey.Contracts;
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
            IJourneyChangeFeed journeys,
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

            /*
             * Membership, then content — two questions a cursor cannot both answer (sync engine §3).
             *
             * The device's stored scope is what it was last told it holds. Diffing it against the
             * rep's current coverage gives the ids that have *entered* (which need a baseline,
             * because their row version is whatever it has always been and is almost certainly
             * below the cursor) and the ids that have *left* (which need a tombstone, because the
             * rows still exist and the delta will never mention them again).
             *
             * The delta then runs over the intersection — outlets the device already had and still
             * covers — because entering ids are being sent in full anyway.
             */
            var known = await db.DeviceScope
                .Where(entry => entry.DeviceId == device.Id)
                .Select(entry => entry.OutletId)
                .ToListAsync(ct);

            var current = coverage.OutletIds.ToHashSet();
            var knownSet = known.ToHashSet();

            var entering = current.Except(knownSet).ToList();
            var leaving = knownSet.Except(current).ToList();
            var retained = current.Intersect(knownSet).ToList();

            var cursor = request.Cursors?.Outlets ?? 0;
            var page = await outlets.GetChangesAsync(cursor, retained, PageLimit, ct);
            var baseline = await outlets.GetBaselineAsync(entering, PageLimit, ct);

            /*
             * The cursor must cover the baseline too, and this is the bug the tests caught.
             *
             * `GetChangesAsync` reports the highest version it *sent*, and on a first pull it sends
             * nothing — every id is entering, so `retained` is empty and it returns the cursor it
             * was given, zero. Meanwhile the baseline hands over rows at version 6. A device that
             * banked zero would come back asking for `> 0` and be handed the same rows again, and
             * again, forever: the delta would never engage and the protocol would degrade to a full
             * snapshot on every sync while looking entirely correct.
             */
            var cursorAfter = page.Cursor;
            foreach (var snapshot in baseline)
                cursorAfter = Math.Max(cursorAfter, snapshot.RowVersion);

            // A device that has left scope keeps the row unless it is told otherwise, and the row is
            // not deleted, so there is no tombstone in Outlets to find. Sync mints them: the version
            // is the page's, which is all the client uses them for — ordering within this response.
            var scopeTombstones = leaving
                .Select(outletId => new ReferenceTombstone(outletId, cursorAfter))
                .ToList();

            /*
             * The rep's round, and the second entity type this protocol carries (W8 slice 8a).
             *
             * Scoped by the subject in the token, not by the device's outlet set — a call belongs to
             * a rep because the plan names them, and a rep can be given a call at a shop that has
             * since left their territory. Scoping journeys by outlet would hide exactly the call
             * whose absence a supervisor would ask about.
             *
             * It needs no baseline half, and that asymmetry is the interesting part of this slice: a
             * planned call is *born* belonging to one rep and never changes hands, so membership
             * only ever changes by creation — which stamps a version above every cursor by
             * construction. Outlets need a baseline because a shop can enter a territory without
             * being edited at all.
             */
            var journeyCursor = request.Cursors?.Journeys ?? 0;
            var round = await journeys.GetChangesAsync(journeyCursor, tenant.UserId, PageLimit, ct);

            await RecordScopeAsync(db, device.Id, tenant.TenantId, entering, leaving, ct);

            return Results.Ok(new PullResponse(
                new PullChanges(
                    new EntityChanges<OutletSnapshot>(
                        [.. baseline, .. page.Upserts],
                        [.. page.Tombstones, .. scopeTombstones],
                        cursorAfter),
                    new EntityChanges<PlannedVisitSnapshot>(
                        round.Upserts, round.Tombstones, round.Cursor)),
                // A patchwork, not a point in time: watermarks advance per entity type, and the
                // device tolerates the skew because captured work records its own inputs
                // (sync engine §3). Now that there are two, the string names the outlet cursor only
                // — it is a label for support and a tiebreaker, not something the device parses.
                $"{clock.UtcNow:O}#{cursorAfter}"));
        }).RequireAuthorization();
    }

    /// <summary>
    /// Rewrites what this device is known to hold, in the same save as nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written <b>after</b> the page is built and before it is returned, which is a deliberate and
    /// imperfect ordering: if the response never reaches the device, the server believes it has rows
    /// it does not. The device recovers by rebinding, which clears the set and re-snapshots — and
    /// the alternative, recording only what a device acknowledges, is a second round trip on every
    /// pull to protect against a case that already has a remedy.
    /// </para>
    /// <para>
    /// Stated rather than hidden because it is the one place this protocol is not
    /// self-healing, and slice 9's resume properties are where it should be revisited.
    /// </para>
    /// </remarks>
    private static async Task RecordScopeAsync(
        SyncDbContext db,
        Guid deviceId,
        TenantId tenantId,
        IReadOnlyList<Guid> entering,
        IReadOnlyList<Guid> leaving,
        CancellationToken ct)
    {
        if (entering.Count == 0 && leaving.Count == 0) return;

        foreach (var outletId in entering)
            db.DeviceScope.Add(new DeviceScopeEntry { DeviceId = deviceId, OutletId = outletId, TenantId = tenantId });

        if (leaving.Count > 0)
        {
            var stale = await db.DeviceScope
                .Where(entry => entry.DeviceId == deviceId && leaving.Contains(entry.OutletId))
                .ToListAsync(ct);

            db.DeviceScope.RemoveRange(stale);
        }

        await db.SaveChangesAsync(ct);
    }
}

/// <param name="Cursors">
/// Absent means "I have nothing" — a first pull, or a rebind after a swap, and the server answers
/// with everything in scope rather than an empty delta.
/// </param>
public sealed record PullRequest(Guid DeviceId, PullCursors? Cursors);

/// <summary>
/// One cursor per entity type, each absent when the device has never been told about that one.
/// </summary>
/// <remarks>
/// Separate cursors rather than a single number, because the entities advance independently: a
/// tenant that edits outlets hourly and publishes a plan monthly would, on a shared cursor, make
/// every outlet edit look like a journey change and vice versa. This is also what lets a new entity
/// type be added without resetting the ones that already work — a device that has never sent
/// `journeys` gets its whole round on the next pull and keeps its outlet watermark.
/// </remarks>
public sealed record PullCursors(long? Outlets, long? Journeys = null);

public sealed record EntityChanges<T>(
    IReadOnlyList<T> Upserts, IReadOnlyList<ReferenceTombstone> Tombstones, long Cursor);

public sealed record PullChanges(
    EntityChanges<OutletSnapshot> Outlets, EntityChanges<PlannedVisitSnapshot> Journeys);

public sealed record PullResponse(PullChanges Changes, string SnapshotVersion);
