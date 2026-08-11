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
public sealed record VisitFacts(Guid VisitId, Guid OutletId, string UserId, bool Sealed);

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
