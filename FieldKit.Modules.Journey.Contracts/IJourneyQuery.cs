namespace FieldKit.Modules.Journey.Contracts;

/// <summary>
/// A call on somebody's published round (<c>JRN-04</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>It carries only the identity, and that is on purpose.</b> The one caller needs to know whether
/// the call is real and nothing else, so nothing else is here — the date, the cycle and how the call
/// came to be on the plan are all facts this module holds, and each of them would be a guess about a
/// consumer that has not arrived.
/// </para>
/// <para>
/// A record rather than a <c>bool</c> for one reason: when a second consumer does want the date, it
/// is a property here and every existing caller is unaffected. That is the shape
/// <c>OutletClassification</c> has now been grown twice — segment, then country — without a single
/// call site changing, and it is cheaper than the method-per-question alternative.
/// </para>
/// </remarks>
public sealed record PlannedCall(Guid PlannedVisitId);

/// <summary>
/// What another module may ask about a rep's round (<c>JRN-04</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Designed against one caller, and the caller is check-in.</b> Visit records the planned call a
/// visit fulfils and until now took the id on trust — nothing in the system would notice a
/// fabricated one until it reached a coverage report, where it would look like a call that was made.
/// This interface exists to make that impossible, and its shape is exactly what that needs.
/// </para>
/// <para>
/// <b>The question is asked in one call, not assembled from three.</b> "Is this planned call this
/// rep's, at this outlet?" could be a lookup plus two comparisons in the caller — and then every
/// caller would repeat the comparisons, and one of them would eventually forget the rep. Asking the
/// module that owns the plan to answer the whole question is the difference between a contract and a
/// table.
/// </para>
/// <para>
/// <b>Published plans only.</b> A draft is a supervisor's experiment and the next generation run
/// replaces it wholesale, so a visit anchored to a draft call would point at a row that is about to
/// stop existing. The same rule <c>BR-JRN-2</c>'s annotations follow, enforced here rather than left
/// to the caller because "which plans count" is Journey's business and not Visit's.
/// </para>
/// <para>
/// The rest of what the [journey spec](../../docs/product/20-journey-planning.md) promises this
/// interface — today's round for a rep, the period view — is deliberately still absent: the screens
/// that want it (<c>JRN-05</c>, W9) read it over HTTP, not in process. It grows a method when
/// something inside the monolith asks a question it cannot answer.
/// </para>
/// </remarks>
public interface IJourneyQuery
{
    /// <summary>
    /// The planned call <paramref name="plannedVisitId"/> names, if it is
    /// <paramref name="userId"/>'s, at <paramref name="outletId"/>, on a published plan.
    /// </summary>
    /// <remarks>
    /// <b>One answer for every kind of miss.</b> No such call, another rep's call, the right call at
    /// the wrong shop, and a call on an unpublished plan all return null — deliberately, so that a
    /// caller cannot turn this into an oracle for what is on somebody else's round. The caller is
    /// left with a single refusal to write, which is also the only honest one: the id it was handed
    /// is not a call this visit can claim.
    /// </remarks>
    Task<PlannedCall?> ForVisitAsync(
        Guid plannedVisitId, string userId, Guid outletId, CancellationToken cancellationToken = default);
}
