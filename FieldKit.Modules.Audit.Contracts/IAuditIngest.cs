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
/// An audit that already happened, arriving from a device that was offline while it did
/// (<c>AUD-01</c>, <c>AUD-02</c>, <c>AUD-03</c>, <c>OFF-04</c>).
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
public sealed record CapturedAudit(
    Guid AuditId,
    Guid VisitId,
    DateTimeOffset CapturedAtUtc,
    int WeightSetVersion,
    int? CategoryFacings,
    IReadOnlyList<CapturedAvailability> Availability,
    IReadOnlyList<CapturedFacings> Facings,
    IReadOnlyList<CapturedPrice> Prices);

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
/// </remarks>
public interface IAuditIngest
{
    /// <param name="userId">The rep, from the token Sync is holding — never from the payload.</param>
    Task<AuditIngestResult> IngestAsync(
        CapturedAudit audit, string userId, CancellationToken cancellationToken = default);
}
