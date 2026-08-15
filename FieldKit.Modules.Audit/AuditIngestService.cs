using FieldKit.Modules.Audit.Contracts;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Visit.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Audit;

/// <summary>
/// Applies an audit a device captured offline (<c>OFF-04</c>, <c>BR-AUD-6</c>) — W10 slice 3a.
/// </summary>
/// <remarks>
/// <para>
/// <b>It goes through the aggregate, not around it.</b> Everything about what a stored audit must be
/// true of lives in <see cref="Audit.Record"/>; what this class adds is the three things the
/// aggregate cannot know — that the visit exists, that it is this rep's and still open, and that no
/// audit is already filed against it.
/// </para>
/// <para>
/// <b>The rep is the scope, and every miss looks the same.</b> A device sends ids it read out of its
/// own store; nothing stops a modified client sending a different one. A visit that does not exist, a
/// visit in another tenant and another rep's visit all answer
/// <see cref="AuditIngestRefusal.UnknownVisit"/>, so this cannot be used to discover whose visits
/// exist — the same call <c>JourneyIngestService</c> makes.
/// </para>
/// </remarks>
internal sealed class AuditIngestService(
    AuditDbContext db, IVisitContext visits, ISurveyForms surveys, IScoreWeights weights,
    PhotoMetrics photos)
    : IAuditIngest
{
    public async Task<AuditIngestResult> IngestAsync(
        CapturedAudit captured, string userId, CancellationToken cancellationToken = default)
    {
        /*
         * The replay check comes first, and before the visit is even looked up.
         *
         * Audit and Sync commit separately, so a mutation can land here and lose its ledger entry;
         * the device retries with the same audit id. That retry has to succeed — a device told
         * "refused" forever about work that is done has no way back — and it has to succeed even
         * once the visit it belongs to has been sealed, which is the case a later check would get
         * wrong. Same window `IVisitIngest.AlreadyExists` and `IJourneyIngest`'s repeat-is-success
         * close.
         */
        if (await db.Audits.AnyAsync(row => row.Id == captured.AuditId, cancellationToken))
        {
            return AuditIngestResult.Ok();
        }

        if (await visits.FindAsync(captured.VisitId, cancellationToken) is not { } visit
            || visit.UserId != userId)
        {
            return new AuditIngestResult(
                AuditIngestRefusal.UnknownVisit,
                "That visit is not one of yours, or no longer exists.");
        }

        /*
         * BR-AUD-6, and W11 slice 8d rewrote it because the old reading refused every audit there
         * has ever been.
         *
         * The rule is that an audit belongs to a visit *being worked* — the visit was filed as done,
         * and attaching a fresh measurement afterwards would change a record already counted. What
         * this used to test was `visit.Sealed`, which sounds like the same thing and is not: a
         * pushed `CapturedVisit` is created **already checked out** (`Visit.Ingest`: "sealed on
         * arrival"), and a device only enqueues one *at* check-out. So an offline audit has no
         * window at all — it is `UnknownVisit` before the visit lands and `VisitSealed` after — and
         * the same was true of every order, through `OrderIngestService`'s copy of this check.
         *
         * What the rule means is *captured* after the seal, so that is what is compared. Both
         * timestamps come from the same device's clock — `CapturedAudit.CapturedAtUtc` and the
         * `CheckedOutAtUtc` the device sent on its own `CapturedVisit` — so the comparison is
         * internally consistent even on a phone whose clock is wrong, which is the only way it could
         * be made to work at all.
         *
         * The replay check above stays ahead of this, because a retry of an audit that already
         * landed is not a new audit whatever the timestamps say.
         */
        if (!visit.WasOpenAt(captured.CapturedAtUtc))
        {
            return new AuditIngestResult(
                AuditIngestRefusal.VisitSealed,
                "That visit was checked out before this audit was taken.");
        }

        // A different audit against the same visit. Caught here so the answer is a refusal a device
        // can act on rather than a unique-index violation surfacing as a 500.
        if (await db.Audits.AnyAsync(row => row.VisitId == captured.VisitId, cancellationToken))
        {
            return new AuditIngestResult(
                AuditIngestRefusal.AlreadyAudited, "That visit already has an audit.");
        }

        /*
         * The one thing this module asks Configuration, and the only one worth asking.
         *
         * An answer set naming a questionnaire this tenant does not have is uninterpretable: `AUD-09`
         * would hold a set of responses belonging to no form, and nobody could say what was asked
         * overall. That is worse than a refusal the device can retry after a sync.
         *
         * What is deliberately *not* asked is whether the answers satisfy the form. `BR-AUD-7` is a
         * device rule (see IAuditIngest), and checking each key against today's questions would
         * refuse an audit because the form was re-worded after the rep answered it. The answers carry
         * their own question text precisely so they never need the form to be readable.
         */
        if (captured.SurveyFormId is { } formId
            && await surveys.ByIdAsync(formId, cancellationToken) is null)
        {
            return new AuditIngestResult(
                AuditIngestRefusal.UnknownSurveyForm,
                "That questionnaire is not one this tenant has.");
        }

        /*
         * The weighting the audit was scored against — `BR-AUD-8`, and the reason W10 slice 0 had
         * the device record a version at all.
         *
         * Resolved by version, never "the current one". A re-weighting between the rep working the
         * shelf and the phone draining would otherwise score their audit against numbers they were
         * never shown, which is precisely what that rule forbids.
         *
         * `IScoreWeights` answers for *published* sets only: a draft can still be edited, so an audit
         * scored against one would have a score nobody could reproduce. An unknown version and a
         * still-draft version are one answer, so a device cannot learn which drafts a tenant is
         * working on.
         */
        if (await weights.ByVersionAsync(captured.WeightSetVersion, cancellationToken) is not { } weighting)
        {
            return new AuditIngestResult(
                AuditIngestRefusal.UnknownWeightSet,
                $"There is no published weighting at version {captured.WeightSetVersion}.");
        }

        var (audit, refusal) = Audit.Record(
            captured,
            visit.OutletId,
            userId,
            [.. weighting.Weights.Select(weight => new PillarWeight(weight.Pillar, weight.Percentage))]);

        if (refusal is not AuditRefusal.None) return Refuse(refusal);

        db.Audits.Add(audit!);
        await db.SaveChangesAsync(cancellationToken);

        // The other half of the photo backlog (W13 slice 4): an audit is where references are born,
        // a confirmation is where they die, and the level only moves at those two points — so
        // recording at both is exact, where sampling would be a guess between them.
        await photos.ReportPendingAsync(db, cancellationToken);

        return AuditIngestResult.Ok();
    }

    /// <summary>Maps the aggregate's refusal onto the contract's, with prose for the rep's screen.</summary>
    private static AuditIngestResult Refuse(AuditRefusal refusal) => refusal switch
    {
        AuditRefusal.Empty => new AuditIngestResult(
            AuditIngestRefusal.Empty, "That audit measured nothing."),

        AuditRefusal.NegativeCount => new AuditIngestResult(
            AuditIngestRefusal.NegativeCount, "A count of facings cannot be below zero."),

        AuditRefusal.DuplicateProduct => new AuditIngestResult(
            AuditIngestRefusal.DuplicateProduct, "A product was measured twice in one section."),

        AuditRefusal.CurrencyMismatch => new AuditIngestResult(
            AuditIngestRefusal.CurrencyMismatch, "The price checks are not all in one currency."),

        AuditRefusal.MalformedAnswers => new AuditIngestResult(
            AuditIngestRefusal.MalformedAnswers,
            "Those survey answers name no questionnaire, or answer one question twice."),

        AuditRefusal.MalformedPhotos => new AuditIngestResult(
            AuditIngestRefusal.MalformedPhotos, "A photo has no object key, or one is referenced twice."),

        _ => new AuditIngestResult(AuditIngestRefusal.Empty, "That audit was refused."),
    };
}
