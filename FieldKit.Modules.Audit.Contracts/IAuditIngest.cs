namespace FieldKit.Modules.Audit.Contracts;

/// <summary>
/// Whether an MSL product was on the shelf (<c>AUD-01</c>, <c>BR-AUD-1</c>).
/// </summary>
/// <remarks>
/// A closed set of three, and the third is the reason there are not two. "Absent" and "out of stock"
/// look the same from the aisle and mean opposite things to the business: absent is a listing the
/// shop never took, out-of-stock is one it took and cannot keep filled. Collapsing them would make
/// the availability pillar unable to tell a distribution problem from a replenishment one, which is
/// most of what the pillar is for.
/// </remarks>
public enum AvailabilityStatus
{
    /// <summary>On the shelf.</summary>
    Present = 0,

    /// <summary>Not stocked here at all — a listing gap.</summary>
    Absent = 1,

    /// <summary>Stocked, but the shelf is empty — a replenishment gap.</summary>
    OutOfStock = 2,
}

/// <summary>One MSL product, as the rep found it (<c>AUD-01</c>).</summary>
/// <param name="ProductId">
/// Products' id, carried as a bare <c>Guid</c>. No foreign key: the catalogue lives in another
/// module's schema (AT-1), and an audit records what was checked even if the product is delisted
/// afterwards.
/// </param>
public sealed record CapturedAvailability(Guid ProductId, AvailabilityStatus Status);

/// <summary>
/// Facings counted for one product (<c>AUD-02</c>).
/// </summary>
/// <remarks>
/// The <i>numerator</i> of share-of-shelf. The denominator is
/// <see cref="CapturedAudit.CategoryFacings"/> and is captured separately, because
/// <c>BR-AUD-2</c> is explicit that summing own facings would always produce ~100%.
/// </remarks>
public sealed record CapturedFacings(Guid ProductId, int Facings);

/// <summary>
/// A shelf price the rep read, and the price they were told to expect (<c>AUD-03</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="ExpectedMinorUnits"/> arrives from the device and is stored, not re-resolved.</b>
/// The server could ask Pricing what this outlet's price is <i>today</i> — and that would re-judge a
/// completed audit under a price list that may have been republished since, marking a rep
/// non-compliant against a number nobody showed them. The same call <c>CapturedVisit</c> makes about
/// the geofence, for the same reason.
/// </para>
/// <para>
/// <b>Minor units, as integers.</b> The decimal discipline pricing already uses
/// (<c>BR-PRD-8</c>/<c>BR-PRD-9</c>): a contract carrying <c>double</c> would put a rounding
/// decision in the wire format, and <c>BR-AUD-5</c> needs the phone and the server to agree exactly.
/// </para>
/// </remarks>
/// <param name="ExpectedMinorUnits">
/// What the device resolved as expected here. Null when it could resolve none — an unpriced product
/// is not a compliance failure, and scoring it as one would punish a rep for a gap in the price list.
/// </param>
public sealed record CapturedPrice(
    Guid ProductId, long ObservedMinorUnits, long? ExpectedMinorUnits, string Currency);

/// <summary>
/// Which part of the audit a photo belongs to (<c>AUD-05</c>).
/// </summary>
/// <remarks>
/// Deliberately <b>not</b> <c>ScorePillar</c>, although the first three read like it. A pillar is a
/// thing the score weighs; a section is a thing a rep points a camera at, and the two lists stop
/// agreeing at <see cref="Survey"/> and <see cref="General"/> — neither of which is ever weighted.
/// Sharing one enum would make adding a scored pillar silently change where photos can be filed.
/// </remarks>
public enum AuditSection
{
    /// <summary>Evidence for the availability check (<c>AUD-01</c>).</summary>
    Availability = 0,

    /// <summary>The shelf, for the facings count (<c>AUD-02</c>).</summary>
    ShareOfShelf = 1,

    /// <summary>A shelf edge or a price tag (<c>AUD-03</c>).</summary>
    PriceCompliance = 2,

    /// <summary>Evidence a survey question asked for (<c>AUD-04</c>).</summary>
    Survey = 3,

    /// <summary>The store, the display, the thing the rep thought worth recording.</summary>
    General = 4,
}

/// <summary>
/// A photo the rep took, as a <b>reference</b> (<c>AUD-05</c>, <c>B5</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The binary is not here and never travels this way.</b> Photos are downscaled on the device and
/// uploaded to object storage separately, on reconnect, through presigned URLs — retried
/// independently of the JSON push (<c>B5</c>). This record is the audit's pointer at the object.
/// </para>
/// <para>
/// <b>Which means the object may not exist yet, and may never.</b> The JSON push regularly wins the
/// race — an audit lands with three photo references and the images arrive minutes later, or not at
/// all if the phone is wiped first. That is deliberate: refusing the audit until its photos land
/// would hold a rep's whole day hostage to a slow upload, and <c>AUD-05</c>'s acceptance criterion
/// says as much ("photos appear against the audit after reconnect, even if the JSON push succeeds
/// before the images finish uploading"). A reader that cannot fetch an object should show a gap, not
/// an error — the upload path itself is W11 (<c>OFF-08</c>).
/// </para>
/// </remarks>
/// <param name="ObjectKey">
/// Where the image will be in object storage. Minted on the device, so the reference and the upload
/// agree without a round trip — the same reason <c>AuditId</c> is the device's.
/// </param>
public sealed record CapturedPhoto(AuditSection Section, string ObjectKey);

/// <summary>
/// One survey answer, as the rep gave it (<c>AUD-04</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>It carries the question, not just the key.</b> A key alone would need the form re-read to mean
/// anything, and the form may have been re-worded — or the question dropped — between the rep
/// answering and the push arriving. Carrying the text as it was asked makes the answer readable
/// forever without consulting anything, which is the same call every other part of
/// <see cref="CapturedAudit"/> makes.
/// </para>
/// <para>
/// <b>The value is a string, whatever the question's type was.</b> A number question's answer is
/// <c>"12"</c> and a multi-choice question's is its chosen options joined. That is a real loss of
/// typing and it is deliberate: the alternative is five nullable columns, one per
/// <c>SurveyQuestionType</c>, of which four are always null — and a sixth the day a type is added.
/// The type lives on the question, which is where a reader that cares can find it.
/// </para>
/// </remarks>
public sealed record CapturedAnswer(string QuestionKey, string QuestionText, string Value);

/// <summary>
/// An audit that already happened, arriving from a device that was offline while it did
/// (<c>AUD-01</c>, <c>AUD-02</c>, <c>AUD-03</c>, <c>AUD-04</c>, <c>AUD-05</c>, <c>OFF-04</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every entry here is a record of the past, not an instruction.</b> The rep stood at a shelf and
/// counted; the server's job is to make that real, not to decide whether the numbers should have been
/// those. What the server does re-derive is the <i>score</i> (<c>BR-AUD-8</c>, W10 slice 4) — from
/// these entries and the weight version named below, never from today's configuration.
/// </para>
/// <para>
/// <b>The MSL is not re-resolved either.</b> <c>BR-AUD-1</c> has availability driven by the outlet's
/// MSL, and the device already holds that assortment (<c>OFF-03</c>). Asking Products which SKUs are
/// on the MSL <i>now</i> would describe an audit against a list that may have changed since the rep
/// walked the aisle — inventing checks they were never asked to make, and discarding ones they were.
/// </para>
/// <para>
/// <see cref="AuditId"/> is minted on the device, so a replayed push maps to the same audit rather
/// than a second one — exactly as <c>CapturedVisit.VisitId</c> does.
/// </para>
/// </remarks>
/// <param name="CategoryFacings">
/// The <b>total</b> facings in the category, own SKUs and competitors' alike — the denominator
/// <c>BR-AUD-2</c> requires. Null when the rep could not count it, and that is a real answer: without
/// it the share-of-shelf pillar is <i>skipped</i>, not faked (W10 slice 0).
/// </param>
/// <param name="WeightSetVersion">
/// The version of the tenant's weighting this audit was scored against (<c>BR-AUD-8</c>). Recorded
/// here, at capture, because it is the one fact that cannot be recovered later: a re-weighting
/// between the audit and its push would leave the server unable to say which numbers the rep saw.
/// </param>
/// <param name="SurveyFormId">
/// Which questionnaire the rep worked, or null when the audit had no survey step. Confirmed to exist
/// in this tenant on the way in — an answer set that names no form is uninterpretable, which is worse
/// than one that is refused.
/// </param>
public sealed record CapturedAudit(
    Guid AuditId,
    Guid VisitId,
    DateTimeOffset CapturedAtUtc,
    int WeightSetVersion,
    int? CategoryFacings,
    IReadOnlyList<CapturedAvailability> Availability,
    IReadOnlyList<CapturedFacings> Facings,
    IReadOnlyList<CapturedPrice> Prices,
    Guid? SurveyFormId = null,
    IReadOnlyList<CapturedAnswer>? Answers = null,
    IReadOnlyList<CapturedPhoto>? Photos = null);

/// <summary>Why a pushed audit was not applied. <see cref="None"/> means it was.</summary>
public enum AuditIngestRefusal
{
    None,

    /// <summary>No such visit for this rep — see <see cref="IAuditIngest"/> on why this is one answer.</summary>
    UnknownVisit,

    /// <summary>The visit is sealed, and an audit belongs to a visit being worked (<c>BR-AUD-6</c>).</summary>
    VisitSealed,

    /// <summary>The visit already has an audit, and it is not this one.</summary>
    AlreadyAudited,

    /// <summary>A facings or category count below zero.</summary>
    NegativeCount,

    /// <summary>The same product measured twice in one section.</summary>
    DuplicateProduct,

    /// <summary>Prices in more than one currency, or an unrecognised currency.</summary>
    CurrencyMismatch,

    /// <summary>Nothing was measured at all.</summary>
    Empty,

    /// <summary>Answers naming a questionnaire this tenant does not have (<c>AUD-04</c>).</summary>
    UnknownSurveyForm,

    /// <summary>Two answers under one question key, or answers with no form named.</summary>
    MalformedAnswers,

    /// <summary>A photo with no object key, or two references to one object (<c>AUD-05</c>).</summary>
    MalformedPhotos,
}

/// <summary>What became of a pushed audit.</summary>
/// <param name="Reason">Prose for the rep's screen. Null when nothing was refused.</param>
public sealed record AuditIngestResult(AuditIngestRefusal Refusal, string? Reason = null)
{
    public static AuditIngestResult Ok() => new(AuditIngestRefusal.None);

    public bool Applied => Refusal is AuditIngestRefusal.None;
}

/// <summary>
/// Applies an audit a device captured offline (<c>OFF-04</c>, <c>BR-AUD-6</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The module's only write path.</b> There is no back-office "enter an audit" screen and no live
/// REST capture: an audit is worked at a shelf, inside a visit, with no signal
/// ([audits §7](../../docs/product/22-merchandising-and-audits.md)). Building a second door would be
/// building an API with no user and a second way for an audit to come into existence.
/// </para>
/// <para>
/// <b>A replay is success, not a conflict.</b> Audit and Sync commit separately, so a mutation can
/// land here and lose its ledger entry; the device then retries. Pushing the same
/// <see cref="CapturedAudit.AuditId"/> again returns <see cref="AuditIngestResult.Ok"/> rather than
/// <see cref="AuditIngestRefusal.AlreadyAudited"/> — the same window <c>IVisitIngest</c> and
/// <c>IJourneyIngest</c> already close, and for the same reason: a device told "refused" forever
/// about work that is done has no way back.
/// </para>
/// <para>
/// <b>An unknown visit and another rep's visit are one answer.</b> A device sends ids it read out of
/// its own store; nothing stops a modified client sending a different one. Scoping to the rep makes a
/// fabricated id indistinguishable from a missing one, so this cannot be used to discover whose
/// visits exist.
/// </para>
/// <para>
/// <b><c>BR-AUD-7</c> is not enforced here, and that is the decision rather than an omission.</b>
/// "Mandatory survey questions must be answered before the audit step completes" is a rule about
/// <i>completing a step</i>, which happens on the device with the rep looking at the form. Re-checking
/// it on arrival would test the answers against the questionnaire as it reads <b>today</b> — so a form
/// that gained a mandatory question after the rep answered would refuse an audit for a question that
/// did not exist when they worked the shelf, and one that dropped a question would refuse an audit for
/// an answer that was mandatory at the time. The same as-of-capture reasoning that keeps this module
/// from re-resolving the MSL or the expected price.
/// </para>
/// </remarks>
public interface IAuditIngest
{
    /// <param name="userId">The rep, from the token Sync is holding — never from the payload.</param>
    Task<AuditIngestResult> IngestAsync(
        CapturedAudit audit, string userId, CancellationToken cancellationToken = default);
}
