namespace FieldKit.Modules.Visit.Contracts;

/// <summary>
/// A step the rep completed while offline, as the device had it.
/// </summary>
/// <remarks>
/// It carries the step's shape — order, type, label, whether it was mandatory — rather than only an
/// id, because the server cannot re-derive them. Steps are instantiated per visit from the channel's
/// workflow, and the device instantiated these; asking Configuration for the workflow *now* would
/// describe a visit under a definition that may have been republished since it happened.
/// <para>
/// <see cref="Type"/> is a string rather than the Configuration enum, so this assembly does not have
/// to reference another module's contracts to describe its own record.
/// </para>
/// </remarks>
public sealed record CapturedStep(
    Guid StepId,
    int Order,
    string Type,
    bool Mandatory,
    string Label,
    string? Notes,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// A visit that already happened, arriving from a device that was offline while it did.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every timestamp and coordinate here is a record of the past, not an instruction.</b> The rep
/// checked in at a shop, worked the steps and checked out — possibly yesterday, possibly through a
/// whole day with no signal. The server's job is to make that real, not to decide whether it should
/// have happened.
/// </para>
/// <para>
/// <b>The geofence assessment arrives as fact and is stored unmodified</b>, which is the decision
/// this record exists to encode. The server cannot re-evaluate where a phone was; it could re-run
/// the distance against the outlet's *current* radius, and that would be worse than useless — a
/// radius widened last week would silently reclassify a rep who was legitimately outside it, and one
/// narrowed would accuse a rep who was inside. `BR-VIS-2`'s "never block the rep, always record"
/// applies to the record too.
/// </para>
/// <para>
/// <see cref="VisitId"/> is minted on the device, so a replayed push maps to the same visit rather
/// than a second one. The idempotency ledger is what makes the replay a no-op, and this is what
/// makes the *effect* identifiable if it ever slipped past.
/// </para>
/// </remarks>
public sealed record CapturedVisit(
    Guid VisitId,
    Guid OutletId,
    Guid? PlannedVisitId,
    DateTimeOffset CheckedInAtUtc,
    double? CheckInLatitude,
    double? CheckInLongitude,
    double? CheckInDistanceMetres,
    bool WasInsideGeofence,
    string? OverrideReason,
    IReadOnlyList<CapturedStep> Steps,
    string Outcome,
    string? OutcomeReason,
    DateTimeOffset CheckedOutAtUtc,
    double? CheckOutLatitude,
    double? CheckOutLongitude);

/// <summary>Why an ingest was refused, as an <c>ADR-0012</c> code the device can act on.</summary>
public enum VisitIngestRefusal
{
    None = 0,

    /// <summary>The outlet does not exist, or not for this tenant. Nothing to attach the visit to.</summary>
    OutletUnknown,

    /// <summary>
    /// A non-productive visit with no reason. Checked *as of now* because it is a property of the
    /// record rather than of the world — a rep who left without selling always had to say why.
    /// </summary>
    OutcomeReasonRequired,

    /// <summary>An outcome the server does not recognise, which means a device and a server disagree.</summary>
    OutcomeUnknown,

    /// <summary>
    /// A visit with this id is already stored — the ledger did not recognise the mutation, but the
    /// work is nonetheless done. Almost always the crash window: the visit committed and the ledger
    /// entry that would have remembered it did not. Sync therefore reads this as success.
    /// </summary>
    AlreadyExists,
}

public sealed record VisitIngestResult(VisitIngestRefusal Refusal, string? Detail = null)
{
    public bool Accepted => Refusal == VisitIngestRefusal.None;

    public static VisitIngestResult Ok() => new(VisitIngestRefusal.None);
}

/// <summary>
/// Takes work a device captured offline and makes it real (<c>OFF-04</c>, sync engine §4).
/// </summary>
/// <remarks>
/// <para>
/// Sync calls this rather than writing the <c>visit</c> schema, so the module that owns the rules
/// still runs them (module boundaries §7). Sync knows about devices, ledgers and cursors; it does
/// not know what makes a visit valid, and this is the seam where that stays true.
/// </para>
/// <para>
/// <b>Refusals are results, not exceptions.</b> A batch of twenty visits with one bad outlet id
/// returns nineteen accepted and one refused — partial success is the normal case for a device that
/// has been offline for a day, and throwing would lose the nineteen.
/// </para>
/// <para>
/// <b>An accepted visit is committed before this returns.</b> Visit and Sync own separate schemas and
/// separate contexts, so the caller cannot enlist this in its own transaction however much it would
/// like to; pretending otherwise would leave the work in a change tracker nobody saves. What the two
/// saves cost is a window where a visit is stored and the ledger has not recorded it, and
/// <see cref="VisitIngestRefusal.AlreadyExists"/> is what closes it.
/// </para>
/// </remarks>
public interface IVisitIngest
{
    Task<VisitIngestResult> IngestAsync(
        CapturedVisit visit, string userId, CancellationToken cancellationToken = default);
}
