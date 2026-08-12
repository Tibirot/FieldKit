using System.Text.Json.Serialization;

namespace FieldKit.Modules.Journey.Contracts;

/// <summary>A call the rep could not make, and why (<c>JRN-06</c>, <c>VIS-07</c>).</summary>
/// <remarks>
/// <b>The call is named by its own id and nothing else.</b> A device holds the round it pulled, where
/// each call is one row with one id; it does not hold the plan that row belongs to, and it should not
/// have to — asking a device to send a plan id would be asking it to model a relationship the pull
/// deliberately flattens away.
/// </remarks>
public sealed record NotVisitedCall(Guid PlannedVisitId, string Reason);

/// <summary>A call the rep moved to another day inside its own cycle (<c>JRN-06</c>, <c>BR-JRN-4</c>).</summary>
public sealed record RescheduledCall(Guid PlannedVisitId, DateOnly Date);

/// <summary>
/// A call the rep made that nobody planned (<c>JRN-06</c>).
/// </summary>
/// <remarks>
/// <b>No plan id, and no call id either — this one does not exist yet.</b> The rep was at a shop on a
/// day, and which of their published plans covers that day is a question only Journey can answer.
/// </remarks>
public sealed record UnplannedCall(Guid OutletId, DateOnly Date);

/// <summary>Why an annotation from a device was refused. <see cref="None"/> means it was not.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JourneyIngestRefusal>))]
public enum JourneyIngestRefusal
{
    None,

    /// <summary>
    /// No such call for this rep.
    /// </summary>
    /// <remarks>
    /// <b>One answer for every kind of miss</b>, the same rule <c>IJourneyQuery</c> follows: no such
    /// call, another rep's call, and a call on a plan that was regenerated all return this. A device
    /// that could tell them apart would be an oracle for what is on somebody else's round.
    /// </remarks>
    CallUnknown,

    /// <summary>The plan is a draft — there is no round for a rep to be reporting on.</summary>
    NotPublished,

    /// <summary>Already recorded as not-visited.</summary>
    AlreadyNotVisited,

    /// <summary>The date is outside the window the plan covers.</summary>
    OutsideWindow,

    /// <summary>The date is in a different cycle from the one the call was bought for.</summary>
    OutsideCycle,

    /// <summary>A not-visited call with nothing said (<c>BR-JRN-2</c>).</summary>
    ReasonRequired,

    /// <summary>The reason is longer than the column that stores it.</summary>
    ReasonTooLong,

    /// <summary>This rep has no published plan covering that day.</summary>
    NoPlanForDate,

    /// <summary>The outlet does not exist for this tenant.</summary>
    OutletUnknown,
}

/// <summary>What an annotation did, in a shape a caller branches on rather than catches.</summary>
public sealed record JourneyIngestResult(JourneyIngestRefusal Refusal, string? Detail = null)
{
    public bool Accepted => Refusal is JourneyIngestRefusal.None;

    public static JourneyIngestResult Ok() => new(JourneyIngestRefusal.None);
}

/// <summary>
/// Rep-side annotations arriving from a device that was offline when they happened
/// (<c>VIS-07</c>, <c>OFF-04</c>, sync engine §4).
/// </summary>
/// <remarks>
/// <para>
/// The push-side twin of <c>IJourneyQuery</c>, and the second <c>I…Ingest</c> the
/// [module registry](../../docs/architecture/10-module-boundaries.md#7-module-registry) names. Sync
/// calls this rather than writing the <c>journey</c> schema, so the module that owns
/// <c>BR-JRN-2</c> and <c>BR-JRN-4</c> is still the one enforcing them.
/// </para>
/// <para>
/// <b>Every method takes the rep, and that is the whole security story.</b> A device sends a call id
/// it read out of its own pulled round, and nothing stops a modified client from sending a different
/// one. Scoping every lookup to the plan's own <c>UserId</c> is what makes a fabricated id
/// indistinguishable from a missing one — a rep cannot annotate somebody else's round, and cannot
/// learn that it exists.
/// </para>
/// <para>
/// <b>Refusals are results, not exceptions</b>, for the reason the visit ingest gives: a batch from a
/// device that has been offline for a day is normally a partial success, and throwing on the third of
/// twenty loses the other seventeen.
/// </para>
/// <para>
/// <b>Committed before the call returns.</b> Journey and Sync own separate schemas and therefore
/// separate contexts, so the caller cannot enlist this in its own transaction. The cost is a window
/// where the annotation is stored and the ledger has not recorded it; each method's idempotency note
/// says what closes it.
/// </para>
/// </remarks>
public interface IJourneyIngest
{
    /// <summary>
    /// Records that a planned call did not happen.
    /// </summary>
    /// <remarks>
    /// <b>A repeat is accepted, not refused.</b> The only way to reach this method twice for one call
    /// is a retry whose ledger entry was lost, and the state it wants is already the state it finds —
    /// so <c>AlreadyNotVisited</c> is answered as success and the retry stops. The reason is not
    /// overwritten: the first one is what the rep wrote at the shop.
    /// </remarks>
    Task<JourneyIngestResult> MarkNotVisitedAsync(
        NotVisitedCall call, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a planned call to another day in its cycle.
    /// </summary>
    /// <remarks>
    /// Naturally idempotent: moving a call to the day it is already on is a no-op that reports
    /// success, so a lost ledger entry costs nothing.
    /// </remarks>
    Task<JourneyIngestResult> RescheduleAsync(
        RescheduledCall call, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a call the rep made that nobody planned.
    /// </summary>
    /// <remarks>
    /// <b>The one annotation that is not idempotent by nature</b>, because it creates a row rather
    /// than changing one. A retry past a lost ledger entry would put the same shop on the same day
    /// twice and overstate the rep's coverage, so the implementation refuses to add a second
    /// unplanned call for a shop and day that already have one — and answers success, because the
    /// work the device asked for is done.
    /// </remarks>
    Task<JourneyIngestResult> AddUnplannedAsync(
        UnplannedCall call, string userId, CancellationToken cancellationToken = default);
}
