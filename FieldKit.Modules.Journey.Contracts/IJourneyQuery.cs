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
/// What a round promised, counted by what became of the promise (<c>JRN-04</c>, <c>BR-JRN-6</c>) —
/// W12 slice 2a.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Planned"/> does not mean "not done yet", and reading it that way is the one real
/// hazard in this record.</b> It is the status name, and a planned call <i>never learns it was
/// visited</i> — Journey has no subscriber to <c>VisitCompleted</c> and no <c>Visited</c> state.
/// Fulfilment lives in Visit, which holds the planned call's id on the visit that claims it. So
/// <see cref="Planned"/> means "on the round and not declined", and the module that would have to
/// say otherwise is a different one.
/// </para>
/// <para>
/// <b>That is exactly why coverage is a composition and not a number Journey can produce.</b> The
/// denominator is <see cref="Total"/>; the numerator is Visit's count of visits that claimed a
/// planned call. Neither module can compute the ratio alone, which is the honest shape — the
/// alternative is Journey subscribing to visits so it can keep a tally that Visit already has.
/// </para>
/// <para>
/// <b><see cref="NotVisited"/> stays inside <see cref="Total"/>.</b> <c>BR-JRN-2</c> refuses to let
/// a rep delete a call precisely so a skipped shop cannot vanish from the denominator: dropping it
/// would make coverage measure what was left on the plan rather than what was promised. It is
/// reported separately because a round that was 80% covered with eight shops shut is a different
/// week from one that was 80% covered with eight shops missed, and the ratio alone cannot tell them
/// apart.
/// </para>
/// </remarks>
/// <param name="Planned">Still standing on the round — not declined by the rep.</param>
/// <param name="NotVisited">The rep said they could not make it, and why (<c>JRN-06</c>).</param>
public sealed record PlannedCallCounts(int Planned, int NotVisited)
{
    /// <summary>Every call the round promised — coverage's denominator.</summary>
    public int Total => Planned + NotVisited;
}

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
/// <para>
/// <b>W12 slice 2a is the second such question</b> (<c>CountPlannedAsync</c>), and it is a second
/// method rather than a fatter <see cref="PlannedCall"/> because it asks about a <i>population</i>
/// rather than about one call. The first is answered per check-in and the second per dashboard load;
/// nothing either one needs is on the other's path.
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

    /// <summary>
    /// How many calls were promised at these shops over a closed date range — coverage's denominator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Published plans only</b>, for the reason <see cref="ForVisitAsync"/> gives: a draft is a
    /// supervisor's experiment that the next generation run replaces wholesale, and counting one
    /// would make a plan nobody committed to look like a promise somebody broke.
    /// </para>
    /// <para>
    /// <b>Dated by the day the call was planned for</b>, which is the only date a planned call has.
    /// Both ends inclusive, matching <c>IVisitQuery</c> — the two are divided by each other, so a
    /// window that meant different things on each side would produce a ratio of two different
    /// questions.
    /// </para>
    /// <para>
    /// An empty <paramref name="outletIds"/> answers zero rather than everything, and for the same
    /// reason it does there: reading an empty filter as "no filter" is how a scoped query quietly
    /// becomes a tenant-wide one.
    /// </para>
    /// </remarks>
    Task<PlannedCallCounts> CountPlannedAsync(
        IReadOnlyCollection<Guid> outletIds,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);
}
