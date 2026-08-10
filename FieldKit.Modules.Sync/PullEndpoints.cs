using FieldKit.BuildingBlocks;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Journey.Contracts;
using FieldKit.Modules.Org.Contracts;
using FieldKit.Modules.Outlets.Contracts;
using FieldKit.Modules.Products.Contracts;
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
            IVisitWorkflowFeed workflows,
            IProductChangeFeed products,
            IAssortmentChangeFeed assortment,
            IPriceChangeFeed prices,
            IPromotionChangeFeed promotions,
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
             * One block per entity type, each answering "whose row is it" differently — which is the
             * only thing that varies between them (sync engine §3). Outlets are the complicated one
             * and live in their own method; the other three are a cursor and a call.
             */
            var territory = await TerritoryAsync(db, outlets, device.Id, coverage.OutletIds, request, ct);

            /*
             * The rep's round (W8 slice 8a). Scoped by the subject in the token, not by the device's
             * outlet set — a call belongs to a rep because the plan names them, and a rep can be
             * given a call at a shop that has since left their territory. Scoping the round by
             * outlet would hide exactly the call whose absence a supervisor would ask about.
             *
             * No baseline half: a planned call is *born* belonging to one rep and never changes
             * hands, so membership only ever changes by creation — which stamps a version above
             * every cursor by construction.
             */
            var round = await journeys.GetChangesAsync(
                request.Cursors?.Journeys ?? 0, tenant.UserId, PageLimit, ct);

            /*
             * Visit workflows (W8 slice 8b). Scoped by nothing: every device in the tenant gets every
             * workflow. Narrowing to the channels of the rep's outlets would reintroduce the
             * membership problem the outlet baseline exists to work around — moving a shop to another
             * channel puts a workflow in scope without editing it — to save a handful of rows a
             * tenant's own administrators wrote.
             */
            var configuration = await workflows.GetChangesAsync(
                request.Cursors?.Configuration ?? 0, PageLimit, ct);

            /*
             * The product catalogue (W8 slice 8c), and scoped by nothing for the same reason plus one
             * of its own: a rep standing in a shop has to be able to *name* what they are looking at.
             * Narrowing to the assortment would mean an unplanned call, or a shop whose assortment
             * changed this morning, has products the device cannot label — and the failure would look
             * like missing data rather than like a scoping decision.
             */
            var catalogue = await products.GetChangesAsync(
                request.Cursors?.Products ?? 0, PageLimit, ct);

            /*
             * What a rep may sell, in two halves with two different scopes (W8 slice 8d).
             *
             * The channel list is tenant-wide, like the catalogue. The overrides belong to
             * individual outlets and are exactly as private as the outlets are — so they are the
             * first entity to reuse the device's outlet scope, and the first to need the baseline
             * shape outlets have had since slice 3: an outlet entering a rep's territory brings its
             * overrides with it *without editing them*.
             *
             * `entering` and `retained` come from the territory diff above rather than being
             * recomputed, which is the point of doing that diff once.
             */
            var lines = await assortment.GetLineChangesAsync(
                request.Cursors?.Assortment ?? 0, PageLimit, ct);

            var overrideCursor = request.Cursors?.OutletAssortment ?? 0;
            var overridePage = await assortment.GetOverrideChangesAsync(
                overrideCursor, territory.Retained, PageLimit, ct);
            var overrideBaseline = await assortment.GetOverrideBaselineAsync(
                territory.Entering, PageLimit, ct);

            // The cursor has to cover the baseline too — the bug slice 3a shipped and its tests
            // caught. A first pull sends nothing from the delta half, so a device that banked the
            // delta's cursor would ask for `> 0` forever and never engage.
            var overrideCursorAfter = overridePage.Cursor;
            foreach (var snapshot in overrideBaseline)
                overrideCursorAfter = Math.Max(overrideCursorAfter, snapshot.RowVersion);

            /*
             * Prices (W8 slice 8e). Lists and lines are tenant-wide; assignments are split the way
             * the assortment is, because a channel assignment is a tenant's pricing policy and an
             * outlet assignment is a fact about one shop.
             *
             * The assignment half takes `retained` and `entering` for the same reason the overrides
             * do — an outlet joining a territory brings an assignment written long ago. What differs
             * is that an *empty* outlet set is not an empty answer here: a rep with no territory
             * still needs the channel policy, because the shops they are given tomorrow are priced
             * by it.
             */
            var priceLists = await prices.GetListChangesAsync(
                request.Cursors?.PriceLists ?? 0, PageLimit, ct);
            var priceLines = await prices.GetLineChangesAsync(
                request.Cursors?.PriceLines ?? 0, PageLimit, ct);

            var assignmentPage = await prices.GetAssignmentChangesAsync(
                request.Cursors?.PriceAssignments ?? 0, territory.Retained, PageLimit, ct);
            var assignmentBaseline = await prices.GetAssignmentBaselineAsync(
                territory.Entering, PageLimit, ct);

            var assignmentCursorAfter = assignmentPage.Cursor;
            foreach (var snapshot in assignmentBaseline)
                assignmentCursorAfter = Math.Max(assignmentCursorAfter, snapshot.RowVersion);

            /*
             * Promotions (W8 slice 8f), the last reference entity. Same split as prices: the
             * promotions themselves are tenant-wide, the assignments are channel-or-outlet.
             *
             * A promotion travels *whole* — its targets and tiers are inside the row, because a
             * device holding four of five tiers does not fail, it computes a different discount and
             * nothing looks wrong.
             */
            var promotionPage = await promotions.GetChangesAsync(
                request.Cursors?.Promotions ?? 0, PageLimit, ct);

            var promotionAssignments = await promotions.GetAssignmentChangesAsync(
                request.Cursors?.PromotionAssignments ?? 0, territory.Retained, PageLimit, ct);
            var promotionBaseline = await promotions.GetAssignmentBaselineAsync(
                territory.Entering, PageLimit, ct);

            var promotionCursorAfter = promotionAssignments.Cursor;
            foreach (var snapshot in promotionBaseline)
                promotionCursorAfter = Math.Max(promotionCursorAfter, snapshot.RowVersion);

            await RecordScopeAsync(
                db, device.Id, tenant.TenantId, territory.Entering, territory.Leaving, ct);

            return Results.Ok(new PullResponse(
                new PullChanges(
                    territory.Changes,
                    new EntityChanges<PlannedVisitSnapshot>(
                        round.Upserts, round.Tombstones, round.Cursor),
                    new EntityChanges<VisitWorkflowSnapshot>(
                        configuration.Upserts, configuration.Tombstones, configuration.Cursor),
                    new EntityChanges<ProductSnapshot>(
                        catalogue.Upserts, catalogue.Tombstones, catalogue.Cursor),
                    new EntityChanges<AssortmentLineSnapshot>(
                        lines.Upserts, lines.Tombstones, lines.Cursor),
                    new EntityChanges<AssortmentOverrideSnapshot>(
                        [.. overrideBaseline, .. overridePage.Upserts],
                        overridePage.Tombstones,
                        overrideCursorAfter),
                    new EntityChanges<PriceListSnapshot>(
                        priceLists.Upserts, priceLists.Tombstones, priceLists.Cursor),
                    new EntityChanges<PriceLineSnapshot>(
                        priceLines.Upserts, priceLines.Tombstones, priceLines.Cursor),
                    new EntityChanges<PriceAssignmentSnapshot>(
                        [.. assignmentBaseline, .. assignmentPage.Upserts],
                        assignmentPage.Tombstones,
                        assignmentCursorAfter),
                    new EntityChanges<PromotionSnapshot>(
                        promotionPage.Upserts, promotionPage.Tombstones, promotionPage.Cursor),
                    new EntityChanges<PromotionAssignmentSnapshot>(
                        [.. promotionBaseline, .. promotionAssignments.Upserts],
                        promotionAssignments.Tombstones,
                        promotionCursorAfter)),
                // A patchwork, not a point in time: watermarks advance per entity type, and the
                // device tolerates the skew because captured work records its own inputs
                // (sync engine §3). The string names the outlet cursor only — it is a label for
                // support and a tiebreaker, not something the device parses.
                $"{clock.UtcNow:O}#{territory.Changes.Cursor}"));
        }).RequireAuthorization();
    }

    /// <summary>What the outlet half of a pull produced, and what the device's scope set must become.</summary>
    /// <param name="Retained">
    /// Outlets the device already held and still holds — the delta half of the diff. Carried out of
    /// this method because every *other* per-outlet entity needs the same three sets, and computing
    /// them once is the point of doing the diff at all.
    /// </param>
    private sealed record Territory(
        EntityChanges<OutletSnapshot> Changes,
        IReadOnlyList<Guid> Entering,
        IReadOnlyList<Guid> Leaving,
        IReadOnlyList<Guid> Retained);

    /// <summary>
    /// The outlets a device should hold: membership first, then content (sync engine §3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two questions a cursor cannot both answer.</b> The device's stored scope is what it was
    /// last told it holds. Diffing it against the rep's current coverage gives the ids that have
    /// <i>entered</i> — which need a baseline, because their row version is whatever it has always
    /// been and is almost certainly below the cursor — and the ids that have <i>left</i>, which need
    /// a tombstone, because the rows still exist and the delta will never mention them again.
    /// </para>
    /// <para>
    /// The delta then runs over the intersection: outlets the device already had and still covers.
    /// Entering ids are being sent in full anyway.
    /// </para>
    /// <para>
    /// It is the only entity type that needs any of this, which is why it is the only one with a
    /// method. The others answer "whose row is it" with a single value, and a value needs no diff.
    /// </para>
    /// </remarks>
    private static async Task<Territory> TerritoryAsync(
        SyncDbContext db,
        IReferenceChangeFeed outlets,
        Guid deviceId,
        IReadOnlyCollection<Guid> covered,
        PullRequest request,
        CancellationToken ct)
    {
        var known = await db.DeviceScope
            .Where(entry => entry.DeviceId == deviceId)
            .Select(entry => entry.OutletId)
            .ToListAsync(ct);

        var current = covered.ToHashSet();
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
         * nothing — every id is entering, so `retained` is empty and it returns the cursor it was
         * given, zero. Meanwhile the baseline hands over rows at version 6. A device that banked zero
         * would come back asking for `> 0` and be handed the same rows again, and again, forever: the
         * delta would never engage and the protocol would degrade to a full snapshot on every sync
         * while looking entirely correct.
         */
        var cursorAfter = page.Cursor;
        foreach (var snapshot in baseline)
            cursorAfter = Math.Max(cursorAfter, snapshot.RowVersion);

        // A device that has left scope keeps the row unless it is told otherwise, and the row is not
        // deleted, so there is no tombstone in Outlets to find. Sync mints them: the version is the
        // page's, which is all the client uses them for — ordering within this response.
        var scopeTombstones = leaving
            .Select(outletId => new ReferenceTombstone(outletId, cursorAfter))
            .ToList();

        return new Territory(
            new EntityChanges<OutletSnapshot>(
                [.. baseline, .. page.Upserts],
                [.. page.Tombstones, .. scopeTombstones],
                cursorAfter),
            entering,
            leaving,
            retained);
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
public sealed record PullCursors(
    long? Outlets,
    long? Journeys = null,
    long? Configuration = null,
    long? Products = null,
    long? Assortment = null,
    long? OutletAssortment = null,
    long? PriceLists = null,
    long? PriceLines = null,
    long? PriceAssignments = null,
    long? Promotions = null,
    long? PromotionAssignments = null);

public sealed record EntityChanges<T>(
    IReadOnlyList<T> Upserts, IReadOnlyList<ReferenceTombstone> Tombstones, long Cursor);

public sealed record PullChanges(
    EntityChanges<OutletSnapshot> Outlets,
    EntityChanges<PlannedVisitSnapshot> Journeys,
    EntityChanges<VisitWorkflowSnapshot> Configuration,
    EntityChanges<ProductSnapshot> Products,
    EntityChanges<AssortmentLineSnapshot> Assortment,
    EntityChanges<AssortmentOverrideSnapshot> OutletAssortment,
    EntityChanges<PriceListSnapshot> PriceLists,
    EntityChanges<PriceLineSnapshot> PriceLines,
    EntityChanges<PriceAssignmentSnapshot> PriceAssignments,
    EntityChanges<PromotionSnapshot> Promotions,
    EntityChanges<PromotionAssignmentSnapshot> PromotionAssignments);

public sealed record PullResponse(PullChanges Changes, string SnapshotVersion);
