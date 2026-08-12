namespace FieldKit.Modules.Visit.Contracts;

/// <summary>
/// The facts about a visit that another module's work has to hang off (<c>VIS-01</c>).
/// </summary>
/// <remarks>
/// Deliberately thin. Everything a consumer needs to file its own work against the right visit —
/// whose it was, which shop, and whether it is still open — and nothing about how the visit was
/// worked. Steps, positions, override reasons and outcomes stay in the module that owns them.
/// </remarks>
/// <param name="Sealed">
/// Whether the visit is checked out (<c>BR-VIS-4</c>). The one fact a consumer must branch on: work
/// attached to a sealed visit would change a record that has already been filed as done — and
/// <c>BR-AUD-6</c> is the same sentence from the audit's side.
/// </param>
/// <param name="CheckedOutAtUtc">
/// <b>When</b> it was sealed, and it is what makes <see cref="Sealed"/> usable at all offline
/// (W11 slice 8d).
/// <para>
/// A pushed <c>CapturedVisit</c> is created <i>already checked out</i> — <c>Visit.Ingest</c> says
/// "sealed on arrival" — and a device only enqueues one at check-out. So every offline-captured
/// order and audit arrives at a visit that is sealed by the time it lands, and a consumer branching
/// on <see cref="Sealed"/> alone refuses all of them. The rule those consumers actually want is
/// <i>work captured after the visit was sealed</i>, which needs the moment rather than the flag.
/// </para>
/// <para>
/// Null while the visit is open, and never null once <see cref="Sealed"/> is true. A consumer that
/// finds it null on a sealed visit is looking at a row that should not exist, and refusing is the
/// safe reading — it cannot prove the work came first.
/// </para>
/// </param>
public sealed record VisitFacts(
    Guid VisitId,
    Guid OutletId,
    string UserId,
    bool Sealed,
    DateTimeOffset? CheckedOutAtUtc)
{
    /// <summary>
    /// Whether the visit was still being worked at <paramref name="moment"/> (W11 slice 8d).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A fact, not a decision</b>, which is the line this contract draws elsewhere and keeps here.
    /// It answers "was this visit open then"; whether that refuses an audit is <c>BR-AUD-6</c> and
    /// lives in Audit, and whether it refuses an order is Order's. Both ask the same question, and
    /// two copies of the comparison would be two places for the answer to drift.
    /// </para>
    /// <para>
    /// <b>An open visit was open at every moment</b>, including one a device claims is in the future.
    /// Nothing here polices a device's clock — <c>Visit</c> already refuses a check-out later than the
    /// push, and duplicating that judgement here would give two modules a second opinion about it.
    /// </para>
    /// <para>
    /// <b>Sealed with no timestamp is closed at every moment.</b> That row should not exist; refusing
    /// is the safe reading, because nothing about it can prove work came first.
    /// </para>
    /// </remarks>
    public bool WasOpenAt(DateTimeOffset moment) =>
        !Sealed || (CheckedOutAtUtc is { } sealedAt && moment <= sealedAt);
}

/// <summary>
/// Where a visit is, for the modules whose work belongs to one (<c>VIS-01</c>, <c>BR-AUD-6</c>).
/// </summary>
/// <remarks>
/// <para>
/// Visit's csproj has said since W7 that this interface was deliberately unbuilt: "consumed by Audit
/// and Order, which are Phase 3, and an interface designed before its consumer is a guess that
/// consumer has to live with." Audit is that consumer, and the shape follows from the one question it
/// actually asks — <i>may I attach an audit to this visit?</i> — rather than from the several it was
/// once imagined to answer.
/// </para>
/// <para>
/// <b>It answers, it does not decide.</b> Whether a sealed visit refuses an audit is <c>BR-AUD-6</c>,
/// which is Audit's rule and lives in Audit's aggregate. Putting the decision here would move one
/// module's refusal into another module's contract, and every future consumer would inherit the
/// audit's answer to a question they never asked — the same split <c>IFieldDefinitionCatalog</c>
/// makes when it hands back descriptors rather than a validator.
/// </para>
/// </remarks>
public interface IVisitContext
{
    /// <summary>
    /// The visit with this id, or null when this tenant has no such visit.
    /// </summary>
    /// <remarks>
    /// Null rather than a default, unlike <c>IVisitWorkflow</c>: there is no sensible stand-in for a
    /// visit that does not exist, and a caller filing work against one needs the difference between
    /// "not found" and "found and sealed" to say two different things to the rep.
    /// </remarks>
    Task<VisitFacts?> FindAsync(Guid visitId, CancellationToken cancellationToken = default);
}
