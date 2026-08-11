using FieldKit.SharedKernel;
using FieldKit.Modules.Journey.Contracts;
using FieldKit.Modules.Outlets.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Journey;

/// <summary>
/// Applies rep-side annotations that arrive from a device (<c>VIS-07</c>, <c>OFF-04</c>) — W9 slice 9.
/// </summary>
/// <remarks>
/// <para>
/// <b>It goes through the aggregate, not around it.</b> Every method here loads the plan and calls the
/// same <c>Try…</c> method the HTTP endpoint calls, so <c>BR-JRN-2</c>'s "published plans only" and
/// <c>BR-JRN-4</c>'s cycle rule are enforced once, in one place, whichever door the annotation came
/// through. What this class adds is the two things the HTTP path gets for free from its URL: finding
/// the plan from a call id, and proving the round is this rep's.
/// </para>
/// <para>
/// <b>The rep is the scope, and every miss looks the same.</b> A device sends ids it read out of its
/// own pulled round; nothing stops a modified client sending a different one. Scoping the lookup to
/// the plan's <c>UserId</c> makes a fabricated id indistinguishable from a missing one.
/// </para>
/// </remarks>
internal sealed class JourneyIngestService(
    JourneyDbContext db, IOutletCatalog outlets, IClock clock) : IJourneyIngest
{
    public async Task<JourneyIngestResult> MarkNotVisitedAsync(
        NotVisitedCall call, string userId, CancellationToken cancellationToken = default)
    {
        // Checked before the plan is loaded: an empty reason is refusable without knowing anything
        // about the round, and `BR-JRN-2` is the reason the annotation exists at all.
        if (string.IsNullOrWhiteSpace(call.Reason))
        {
            return new JourneyIngestResult(
                JourneyIngestRefusal.ReasonRequired,
                "Say why the call did not happen. A skipped shop with no reason is a gap nobody can act on.");
        }

        if (call.Reason.Trim().Length > PlannedVisit.MaximumReasonLength)
        {
            return new JourneyIngestResult(
                JourneyIngestRefusal.ReasonTooLong,
                $"A reason is at most {PlannedVisit.MaximumReasonLength} characters.");
        }

        var (plan, visit) = await FindAsync(call.PlannedVisitId, userId, cancellationToken);
        if (plan is null || visit is null) return Unknown();

        var refusal = plan.TryMarkNotVisited(visit, call.Reason, clock);

        /*
         * A repeat is success, and this is the same window `IVisitIngest.AlreadyExists` closes.
         *
         * Journey and Sync commit separately, so a mutation can land here and have its ledger entry
         * lost. The device then retries, finds the call already marked, and would otherwise be told
         * "refused" forever about work that is done. The first reason is kept: it is what the rep
         * wrote standing at the shop, and the retry is carrying the same text anyway.
         */
        if (refusal is JourneyPlan.AnnotationRefusal.AlreadyNotVisited) return JourneyIngestResult.Ok();

        if (refusal is not JourneyPlan.AnnotationRefusal.None) return Refuse(refusal);

        await db.SaveChangesAsync(cancellationToken);

        return JourneyIngestResult.Ok();
    }

    public async Task<JourneyIngestResult> RescheduleAsync(
        RescheduledCall call, string userId, CancellationToken cancellationToken = default)
    {
        var (plan, visit) = await FindAsync(call.PlannedVisitId, userId, cancellationToken);
        if (plan is null || visit is null) return Unknown();

        // Idempotent by nature: moving a call to the day it is already on changes nothing and the
        // aggregate reports success, so a lost ledger entry costs a retry and no correctness.
        var refusal = plan.TryReschedule(visit, call.Date, clock);
        if (refusal is not JourneyPlan.AnnotationRefusal.None) return Refuse(refusal);

        await db.SaveChangesAsync(cancellationToken);

        return JourneyIngestResult.Ok();
    }

    public async Task<JourneyIngestResult> AddUnplannedAsync(
        UnplannedCall call, string userId, CancellationToken cancellationToken = default)
    {
        // The outlet is Outlets' to confirm, exactly as the HTTP path confirms it: an unresolvable id
        // on a plan is something reporting has to explain later.
        if ((await outlets.FindManyAsync([call.OutletId], cancellationToken)).Count == 0)
        {
            return new JourneyIngestResult(
                JourneyIngestRefusal.OutletUnknown, "That outlet does not exist for this tenant.");
        }

        /*
         * Which plan covers the day is a question only this module can answer, and the device cannot
         * be asked it — a rep's round is pulled as flat calls with no plan on them.
         *
         * <b>The most recently published one, not the only one.</b> My first version used
         * `SingleOrDefault` on the theory that a rep's published plans do not overlap, and the
         * integration tests refused it with a 500: nothing stops a supervisor generating and
         * publishing a second plan over the same window, and `JRN-03` positively expects it — a
         * regenerated round is how a plan is corrected, since publishing is one-way. So overlap is
         * ordinary, and the newest published plan is the round the rep is actually walking.
         */
        var plan = await db.JourneyPlans
            .Include(row => row.Visits)
            .Where(row => row.UserId == userId
                && row.Status == JourneyPlanStatus.Published
                && row.FromDate <= call.Date
                && row.ToDate >= call.Date)
            .OrderByDescending(row => row.PublishedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (plan is null)
        {
            return new JourneyIngestResult(
                JourneyIngestRefusal.NoPlanForDate,
                "You have no published round covering that day.");
        }

        /*
         * The one annotation that creates a row, and therefore the one the ledger's window can
         * duplicate — a retry past a lost entry would put the same shop on the same day twice and
         * overstate the rep's coverage.
         *
         * A rep genuinely calling twice at one shop in one day is real, and this refuses to record
         * the second. That is the deliberate trade: an inflated coverage figure is a number a
         * supervisor acts on, and a missing duplicate is a visit that still exists in the Visit
         * module with its own timestamps. Coverage counts shops, not calls.
         */
        if (plan.Visits.Any(existing => existing.OutletId == call.OutletId && existing.Date == call.Date))
        {
            return JourneyIngestResult.Ok();
        }

        var refusal = plan.TryAddUnplanned(call.OutletId, call.Date, clock, out var added);
        if (refusal is not JourneyPlan.AnnotationRefusal.None) return Refuse(refusal);

        // Added through the context as well as through the aggregate, the same way the HTTP path
        // has to. The id is client-generated, so EF sees a non-default key on an entity reached
        // through a navigation, settles on `Modified`, and issues an UPDATE that matches no row.
        // The push path found this as a 500 rather than a refusal, which is exactly how it presents.
        db.Set<PlannedVisit>().Add(added!);

        await db.SaveChangesAsync(cancellationToken);

        return JourneyIngestResult.Ok();
    }

    /// <summary>
    /// The plan and the call, if the call is this rep's.
    /// </summary>
    /// <remarks>
    /// The plan is loaded with its visits because the aggregate is what enforces the rules — reaching
    /// for the <c>PlannedVisit</c> alone and mutating it would be exactly the "around the aggregate"
    /// shortcut that puts <c>BR-JRN-2</c> in two places.
    /// </remarks>
    private async Task<(JourneyPlan? Plan, PlannedVisit? Visit)> FindAsync(
        Guid plannedVisitId, string userId, CancellationToken cancellationToken)
    {
        var plan = await db.JourneyPlans
            .Include(row => row.Visits)
            .SingleOrDefaultAsync(
                row => row.UserId == userId && row.Visits.Any(visit => visit.Id == plannedVisitId),
                cancellationToken);

        return (plan, plan?.Visits.SingleOrDefault(visit => visit.Id == plannedVisitId));
    }

    private static JourneyIngestResult Unknown() => new(
        JourneyIngestRefusal.CallUnknown, "That call is not on a round of yours.");

    /// <summary>Maps the aggregate's refusal onto the contract's, which a device can branch on.</summary>
    private static JourneyIngestResult Refuse(JourneyPlan.AnnotationRefusal refusal) => refusal switch
    {
        JourneyPlan.AnnotationRefusal.NotPublished => new(
            JourneyIngestRefusal.NotPublished, "That round is not published."),
        JourneyPlan.AnnotationRefusal.AlreadyNotVisited => new(
            JourneyIngestRefusal.AlreadyNotVisited, "That call is already recorded as not visited."),
        JourneyPlan.AnnotationRefusal.OutsideWindow => new(
            JourneyIngestRefusal.OutsideWindow, "That day is outside the plan's window."),
        JourneyPlan.AnnotationRefusal.OutsideCycle => new(
            JourneyIngestRefusal.OutsideCycle, "That day is in a different cycle."),
        _ => new(JourneyIngestRefusal.CallUnknown, "That annotation was refused."),
    };
}
