using FieldKit.BuildingBlocks;
using FieldKit.Modules.Audit.Contracts;
using FieldKit.Modules.Order.Contracts;
using FieldKit.Modules.Journey.Contracts;
using FieldKit.Modules.Visit.Contracts;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Sync;

public static class PushEndpoints
{
    /// <summary>A day of offline work is a plausible batch; a thousand mutations is a bug.</summary>
    private const int MaximumBatch = 200;

    public static void MapPushEndpoints(this IEndpointRouteBuilder endpoints)
    {
        /*
         * The outbox drain (OFF-04, sync engine §4).
         *
         * Partial success is the normal case, not an error path: a device offline for a day pushes
         * twenty visits and one of them names an outlet that has since been deleted. Nineteen must
         * land. So every mutation gets its own result and the batch never fails as a unit.
         *
         * Exclusivity is on pull and bind, *not* here. A device the rep has replaced may still drain
         * work it captured before the swap — refusing it would be losing it, and the records are
         * device-owned and append-only so a drain cannot create a competing writer (A8). A device
         * revoked as `Compromised` is the exception: it must not push fabricated work at all.
         */
        endpoints.MapPost("/api/sync/push", async (
            PushRequest request,
            SyncDbContext db,
            IMutationLedger ledger,
            IVisitIngest visits,
            IJourneyIngest journeys,
            IAuditIngest audits,
            IOrderIngest orders,
            ITenantContext tenant,
            CancellationToken ct) =>
        {
            if (request.Mutations.Count > MaximumBatch)
            {
                return Problems.BadRequest(
                    "mutations",
                    $"A push carries at most {MaximumBatch} mutations; split the batch.",
                    "sync.push.batchTooLarge");
            }

            var device = await db.Devices
                .SingleOrDefaultAsync(candidate => candidate.Id == request.DeviceId, ct);

            if (device is null || device.UserId != tenant.UserId)
            {
                return Problems.Refuse(
                    StatusCodes.Status404NotFound,
                    "That device is not registered to you.",
                    "sync.push.deviceUnknown");
            }

            if (device.DeactivatedBecause == DeactivationReason.Compromised)
            {
                return Problems.Refuse(
                    StatusCodes.Status403Forbidden,
                    "This device was reported lost or stolen and cannot send work.",
                    "sync.push.deviceCompromised");
            }

            var results = new List<MutationResult>(request.Mutations.Count);

            foreach (var mutation in request.Mutations)
            {
                // Asked first, every time. A retry is answered with what happened the first time
                // rather than re-applied — exactly-once effect over at-least-once delivery.
                if (await ledger.FindAsync(device.Id, mutation.MutationId, ct) is { } prior)
                {
                    results.Add(MutationResult.From(mutation.MutationId, prior));
                    continue;
                }

                var outcome = await ApplyAsync(
                    mutation, visits, journeys, audits, orders, tenant.UserId, ct);

                ledger.Record(device.Id, mutation.MutationId, outcome);
                results.Add(MutationResult.From(mutation.MutationId, outcome));
            }

            await db.SaveChangesAsync(ct);

            return Results.Ok(new PushResponse(results));
        }).RequireAuthorization();
    }

    /// <summary>
    /// Sends one mutation to the module that owns it (module boundaries §7).
    /// </summary>
    /// <remarks>
    /// <b><c>Type</c> became a discriminator here (W9 slice 9), having been a field.</b> With one kind
    /// of mutation it was a guard against nonsense; with four it is the routing, and each arm knows
    /// only which contract to call — Sync still holds no opinion about what makes a visit valid or a
    /// round annotatable. Applying through the owning module is what keeps that true.
    /// </remarks>
    private static async Task<MutationOutcome> ApplyAsync(
        PushedMutation mutation, IVisitIngest visits, IJourneyIngest journeys, IAuditIngest audits,
        IOrderIngest orders, string userId, CancellationToken ct) => mutation.Type switch
        {
            nameof(CapturedVisit) => await ApplyVisitAsync(mutation, visits, userId, ct),
            nameof(NotVisitedCall) => await ApplyNotVisitedAsync(mutation, journeys, userId, ct),
            nameof(RescheduledCall) => await ApplyRescheduleAsync(mutation, journeys, userId, ct),
            nameof(UnplannedCall) => await ApplyUnplannedAsync(mutation, journeys, userId, ct),
            nameof(CapturedAudit) => await ApplyAuditAsync(mutation, audits, userId, ct),
            nameof(CapturedOrder) => await ApplyOrderAsync(mutation, orders, userId, ct),

            // An unknown type is rejected rather than ignored: a device that keeps retrying something
            // the server silently drops never drains.
            _ => new MutationOutcome(
                MutationStatus.Rejected,
                "sync.push.typeUnsupported",
                $"'{mutation.Type}' is not a mutation this server accepts yet."),
        };

    private static async Task<MutationOutcome> ApplyVisitAsync(
        PushedMutation mutation, IVisitIngest visits, string userId, CancellationToken ct)
    {
        if (mutation.Visit is null) return Missing("visit");

        var result = await visits.IngestAsync(mutation.Visit, userId, ct);

        // `AlreadyExists` means the visit landed on an earlier attempt whose ledger entry did not —
        // Visit and Sync commit separately, so that window exists. The work is done, so the honest
        // answer is accepted, and recording it now closes the window for good.
        if (result.Refusal is VisitIngestRefusal.AlreadyExists)
            return new MutationOutcome(MutationStatus.Accepted);

        return result.Accepted
            ? new MutationOutcome(MutationStatus.Accepted)
            : new MutationOutcome(MutationStatus.Rejected, RefusalCode(result.Refusal), result.Detail);
    }

    private static async Task<MutationOutcome> ApplyNotVisitedAsync(
        PushedMutation mutation, IJourneyIngest journeys, string userId, CancellationToken ct)
    {
        if (mutation.NotVisited is null) return Missing("not-visited call");

        return Outcome(await journeys.MarkNotVisitedAsync(mutation.NotVisited, userId, ct));
    }

    private static async Task<MutationOutcome> ApplyRescheduleAsync(
        PushedMutation mutation, IJourneyIngest journeys, string userId, CancellationToken ct)
    {
        if (mutation.Rescheduled is null) return Missing("reschedule");

        return Outcome(await journeys.RescheduleAsync(mutation.Rescheduled, userId, ct));
    }

    private static async Task<MutationOutcome> ApplyUnplannedAsync(
        PushedMutation mutation, IJourneyIngest journeys, string userId, CancellationToken ct)
    {
        if (mutation.Unplanned is null) return Missing("unplanned call");

        return Outcome(await journeys.AddUnplannedAsync(mutation.Unplanned, userId, ct));
    }

    /// <summary>
    /// Applies a pushed audit (<c>AUD-06</c>, <c>BR-AUD-8</c>) — W10 slice 6.
    /// </summary>
    /// <remarks>
    /// <b>The audit is its own mutation, decided in W10 slice 0.</b> A rep's phone drains the visit
    /// and the audit as two entries in the same outbox, and <c>/sync/push</c> answers per mutation
    /// precisely so one refusal cannot take a completed visit with it — an audit rejected for naming
    /// a weight version this server has never published must not strand the visit it belonged to.
    /// </remarks>
    private static async Task<MutationOutcome> ApplyAuditAsync(
        PushedMutation mutation, IAuditIngest audits, string userId, CancellationToken ct)
    {
        if (mutation.Audit is null) return Missing("audit");

        var result = await audits.IngestAsync(mutation.Audit, userId, ct);

        return result.Applied
            ? new MutationOutcome(MutationStatus.Accepted)
            : new MutationOutcome(MutationStatus.Rejected, RefusalCode(result.Refusal), result.Reason);
    }

    /// <summary>
    /// Applies a pushed order (<c>ORD-07</c>, <c>OFF-04</c>) — W11 slice 5.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The only arm that hands the mutation id to the module it calls</b>, and the reason is
    /// <c>BR-ORD-9</c>. Every other kind is idempotent on its own subject — a visit by its id, an
    /// annotation by the call it is about — so a repeat is recognisable from the payload. An order is
    /// not: the same order id arrives twice both when a device retries and when a rep corrects a
    /// rejection, and only the mutation id tells those apart. Order records it (W11 slice 3) so its
    /// answer and this ledger's cannot drift.
    /// </para>
    /// <para>
    /// <b>An order rejected by a rule still answers <c>accepted</c> here</b>, which looks wrong and is
    /// not. The push asked this server to record an order and it did; that <c>BR-ORD-1</c> then
    /// refused it is an outcome carried on the order itself, and the rep meets it on the way back
    /// down. Answering <c>rejected</c> would tell the device the mutation never applied, and the
    /// retry would arrive to find the order already there. Only a refusal that stored <i>nothing</i>
    /// is a rejection at this layer.
    /// </para>
    /// </remarks>
    private static async Task<MutationOutcome> ApplyOrderAsync(
        PushedMutation mutation, IOrderIngest orders, string userId, CancellationToken ct)
    {
        if (mutation.Order is null) return Missing("order");

        var result = await orders.IngestAsync(mutation.Order, mutation.MutationId, userId, ct);

        return result.Refusal is OrderIngestRefusal.None
            ? new MutationOutcome(MutationStatus.Accepted)
            : new MutationOutcome(MutationStatus.Rejected, RefusalCode(result.Refusal), result.Message);
    }

    /// <summary>Maps Order's refusal onto an <c>ADR-0012</c> code the device can branch on.</summary>
    private static string RefusalCode(OrderIngestRefusal refusal) => refusal switch
    {
        OrderIngestRefusal.UnknownVisit => "order.ingest.visitUnknown",
        OrderIngestRefusal.Invalid => "order.ingest.invalid",

        // A second, different push against an order that is already sealed (`BR-ORD-4`). The device
        // stops retrying on this one: nothing it can do makes an edit-after-submit legal, and the
        // documented way back is a rejection it has not been given.
        OrderIngestRefusal.AlreadySubmitted => "order.ingest.alreadySubmitted",

        _ => "order.ingest.refused",
    };

    /// <summary>Maps Audit's refusal onto an <c>ADR-0012</c> code the device can branch on.</summary>
    private static string RefusalCode(AuditIngestRefusal refusal) => refusal switch
    {
        AuditIngestRefusal.UnknownVisit => "audit.ingest.visitUnknown",
        AuditIngestRefusal.VisitSealed => "audit.ingest.visitSealed",
        AuditIngestRefusal.AlreadyAudited => "audit.ingest.alreadyAudited",
        AuditIngestRefusal.NegativeCount => "audit.ingest.negativeCount",
        AuditIngestRefusal.DuplicateProduct => "audit.ingest.duplicateProduct",
        AuditIngestRefusal.CurrencyMismatch => "audit.ingest.currencyMismatch",
        AuditIngestRefusal.Empty => "audit.ingest.empty",
        AuditIngestRefusal.UnknownSurveyForm => "audit.ingest.surveyFormUnknown",
        AuditIngestRefusal.MalformedAnswers => "audit.ingest.answersMalformed",
        AuditIngestRefusal.MalformedPhotos => "audit.ingest.photosMalformed",
        AuditIngestRefusal.UnknownWeightSet => "audit.ingest.weightSetUnknown",
        _ => "audit.ingest.refused",
    };

    private static MutationOutcome Missing(string what) => new(
        MutationStatus.Rejected, "sync.push.payloadMissing", $"The mutation carried no {what}.");

    private static MutationOutcome Outcome(JourneyIngestResult result) => result.Accepted
        ? new MutationOutcome(MutationStatus.Accepted)
        : new MutationOutcome(MutationStatus.Rejected, RefusalCode(result.Refusal), result.Detail);

    /// <summary>Maps Journey's refusal onto an <c>ADR-0012</c> code the device can branch on.</summary>
    private static string RefusalCode(JourneyIngestRefusal refusal) => refusal switch
    {
        JourneyIngestRefusal.CallUnknown => "journey.visit.unknown",
        JourneyIngestRefusal.NotPublished => "journey.plan.notPublished",
        JourneyIngestRefusal.AlreadyNotVisited => "journey.visit.alreadyNotVisited",
        JourneyIngestRefusal.OutsideWindow => "journey.visit.outsideWindow",
        JourneyIngestRefusal.OutsideCycle => "journey.visit.outsideCycle",
        JourneyIngestRefusal.ReasonRequired => "journey.visit.reasonRequired",
        JourneyIngestRefusal.ReasonTooLong => "journey.visit.reasonTooLong",
        JourneyIngestRefusal.NoPlanForDate => "journey.plan.noneForDate",
        JourneyIngestRefusal.OutletUnknown => "journey.visit.outletUnknown",
        _ => "journey.visit.refused",
    };

    /// <summary>Maps a module's refusal onto an <c>ADR-0012</c> code the device can branch on.</summary>
    private static string RefusalCode(VisitIngestRefusal refusal) => refusal switch
    {
        VisitIngestRefusal.OutletUnknown => "visit.ingest.outletUnknown",
        VisitIngestRefusal.OutcomeReasonRequired => "visit.ingest.outcomeReasonRequired",
        VisitIngestRefusal.OutcomeUnknown => "visit.ingest.outcomeUnknown",
        VisitIngestRefusal.AlreadyExists => "visit.ingest.alreadyExists",
        _ => "visit.ingest.refused",
    };
}

public sealed record PushRequest(Guid DeviceId, IReadOnlyList<PushedMutation> Mutations);

/// <summary>One thing the rep did, as the device recorded it.</summary>
/// <remarks>
/// <b>A typed payload per kind rather than a <c>payload</c> blob.</b> Orders and audits will each add
/// their own optional property beside <see cref="Visit"/>, which is additive — a device that sends
/// only <c>visit</c> keeps working — and keeps the request describable in OpenAPI. A single opaque
/// blob would buy generality this has no second type to spend yet, and cost the schema now.
/// </remarks>
/// <param name="MutationId">Minted on the device. The ledger's key, and the whole basis of the retry story.</param>
/// <remarks>
/// <b>Every payload slot has a default, including <see cref="Visit"/>, and that is load-bearing.</b>
/// A positional parameter without one is a *required* constructor argument to
/// <c>System.Text.Json</c>, so a mutation that carries only <c>notVisited</c> — which is exactly what
/// a device sends — fails to bind and the whole batch answers 400. Giving `Visit` a default was the
/// fix; the tests missed it because they serialise a constructed record, which always writes
/// <c>"visit": null</c>, while a real client omits the property.
/// </remarks>
public sealed record PushedMutation(
    Guid MutationId,
    string Type,
    CapturedVisit? Visit = null,
    NotVisitedCall? NotVisited = null,
    RescheduledCall? Rescheduled = null,
    UnplannedCall? Unplanned = null,
    CapturedAudit? Audit = null,
    CapturedOrder? Order = null);

public sealed record MutationResult(Guid MutationId, string Status, string? Reason, string? Detail)
{
    public static MutationResult From(Guid mutationId, MutationOutcome outcome) => new(
        mutationId,
        outcome.Status == MutationStatus.Accepted ? "accepted" : "rejected",
        outcome.ReasonCode,
        outcome.Detail);
}

public sealed record PushResponse(IReadOnlyList<MutationResult> Results);



