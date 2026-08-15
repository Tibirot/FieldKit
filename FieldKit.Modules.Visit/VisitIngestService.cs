using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Outlets.Contracts;
using FieldKit.Modules.Visit.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Visit;

/// <summary>
/// Turns a visit captured offline into a stored one (<c>OFF-04</c>, sync engine §4).
/// </summary>
/// <remarks>
/// <para>
/// The rules it enforces are the ones that still mean something for work that already happened:
/// the outlet exists, the outcome is one this server understands, a non-productive visit says why,
/// and the same visit is not stored twice.
/// </para>
/// <para>
/// The rules it deliberately does <b>not</b> re-run are the ones whose inputs have moved on —
/// geofence distance against today's radius, mandatory steps against today's workflow. Re-judging a
/// completed visit under republished configuration would refuse work that was correct when it was
/// done, which is the opposite of what an offline-first system owes a rep.
/// </para>
/// </remarks>
internal sealed class VisitIngestService(
    VisitDbContext db, IOutletCatalog outlets, VisitMetrics metrics) : IVisitIngest
{
    public async Task<VisitIngestResult> IngestAsync(
        CapturedVisit captured, string userId, CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<VisitOutcome>(captured.Outcome, ignoreCase: false, out var outcome))
        {
            return new VisitIngestResult(
                VisitIngestRefusal.OutcomeUnknown,
                $"'{captured.Outcome}' is not an outcome this server recognises.");
        }

        // BR-VIS-3, and as-of-now on purpose: "why did nothing come of it" is a property of the
        // record the rep wrote, not of a world that has since changed.
        if (outcome == VisitOutcome.NonProductive && string.IsNullOrWhiteSpace(captured.OutcomeReason))
        {
            return new VisitIngestResult(
                VisitIngestRefusal.OutcomeReasonRequired,
                "A non-productive visit has to say why.");
        }

        var known = await outlets.FindManyAsync([captured.OutletId], cancellationToken);
        if (known.Count == 0)
        {
            return new VisitIngestResult(
                VisitIngestRefusal.OutletUnknown,
                "That outlet does not exist for this tenant.");
        }

        /*
         * The visit id is minted on the device, which makes this ingest idempotent in the domain
         * rather than only in the ledger — and that is what covers the gap the ledger cannot.
         *
         * Visit and Sync own different schemas and therefore different DbContexts, so "record the
         * outcome in the same transaction as the work" is not available: two saves, two
         * transactions. A crash between them leaves the visit stored and no ledger entry, and the
         * device's retry arrives looking new.
         *
         * It arrives here, finds the visit, and is told `AlreadyExists` — which the push endpoint
         * reads as "this already succeeded" and records as accepted. The window closes itself
         * instead of double-applying or refusing work that was done.
         */
        var exists = await db.Visits.AnyAsync(visit => visit.Id == captured.VisitId, cancellationToken);
        if (exists)
        {
            return new VisitIngestResult(
                VisitIngestRefusal.AlreadyExists,
                "A visit with that id is already stored.");
        }

        var steps = captured.Steps
            .OrderBy(step => step.Order)
            .Select(step => VisitStep.Ingested(
                captured.VisitId,
                step.StepId,
                step.Order,
                Enum.TryParse<VisitStepType>(step.Type, out var type) ? type : VisitStepType.Note,
                step.Mandatory,
                step.Label,
                step.Notes,
                step.CompletedAtUtc))
            .ToList();

        db.Visits.Add(Visit.Ingest(captured, userId, outcome, steps));

        // Saved here, in Visit's own transaction, because Visit's schema is Visit's to write — and
        // because the caller could not commit it anyway (see above). The visit landing without its
        // ledger entry is the window the `AlreadyExists` answer above exists to close.
        await db.SaveChangesAsync(cancellationToken);

        // After the save, so a refused or failed ingest is not counted as a finished visit — and
        // once per visit, because a device's retry of a visit that already landed is answered by the
        // `AlreadyExists` branch above and never reaches this line.
        metrics.Completed(db.CurrentTenantId, outcome);

        return VisitIngestResult.Ok();
    }
}
