namespace FieldKit.Modules.Visit.Contracts;

/// <summary>
/// How a set of visits came out (<c>VIS-10</c>, reporting read-side) — W12 slice 1.
/// </summary>
/// <remarks>
/// <para>
/// <b>Counts, not rows.</b> Strike rate is <i>productive ÷ visits</i> and coverage is <i>actual ÷
/// planned</i>; neither caller wants the visits themselves, and handing them back would move the
/// arithmetic into whoever asked. What counts as productive is <c>VIS-05</c>'s business and lives in
/// this module — an endpoint that reduced a list would have taken that judgement home with it.
/// </para>
/// <para>
/// <b><c>Open</c> is separate from the two outcomes rather than a third one.</b> An open visit
/// has no outcome yet — <c>Visit.Outcome</c> is null until check-out — so folding it in would make a
/// rep who is standing in a shop look like a rep who achieved nothing. It is reported because a
/// supervisor reading a strike rate mid-morning needs to know how much of the day is still open, and
/// it is deliberately outside the ratio: <c>StrikeRate</c> divides by visits that finished.
/// </para>
/// </remarks>
/// <param name="Productive">Checked out, and something came of it.</param>
/// <param name="NonProductive">Checked out with nothing to show, and a reason the rep wrote.</param>
/// <param name="Open">Checked in and not yet out — no outcome to count.</param>
public sealed record VisitOutcomeCounts(int Productive, int NonProductive, int Open)
{
    /// <summary>Every visit in the window, open ones included.</summary>
    public int Total => Productive + NonProductive + Open;

    /// <summary>Visits that reached an outcome — the denominator of a strike rate.</summary>
    public int Finished => Productive + NonProductive;

    /// <summary>
    /// Productive ÷ finished, or <c>null</c> when nothing has finished.
    /// </summary>
    /// <remarks>
    /// <b>Null rather than zero, and the distinction is the whole reason this is a property here
    /// rather than a division at the call site.</b> A territory with no finished visits has no strike
    /// rate; reporting one as 0% says every call failed, which is a different and much worse claim
    /// than "nothing has come back yet". A fresh tenant and a bad week must not look alike.
    /// </remarks>
    public decimal? StrikeRate => Finished == 0 ? null : (decimal)Productive / Finished;
}

/// <summary>
/// Reading visits back (<c>VIS-10</c>) — the contract the module registry has listed as planned
/// since W7.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is the second contract here built before a caller had arrived, and the reason is not the
/// first one's.</b> <c>IVisitWorkflow</c> went first because <c>BR-VIS-2</c> could not be
/// <i>implemented</i> without it. This goes first because its caller is a <b>host composition</b> —
/// <c>/api/reporting/summary</c>, W12 slice 3 — which cannot exist until four modules can each
/// answer, so one of the four has to be first. Guessing is still the hazard, and what reduces it is
/// that both shapes below are fixed by the KPI table in the product overview rather than by taste,
/// and both are proved against a seeded month rather than against an empty set.
/// </para>
/// <para>
/// <b>W7 guessed the caller and guessed wrong</b>, which is worth keeping as evidence for the rule
/// rather than against it: the registry note said Audit and Order would be the first consumers. Both
/// were built in W10 and W11 and neither wanted this — they needed <c>IVisitContext</c>, which is a
/// different question. The caller turned out to be reporting, three weeks later.
/// </para>
/// <para>
/// <b>Outlets, not a territory.</b> A <c>Visit</c> carries an outlet and a user and knows nothing
/// about org structure, which is correct and stays that way — resolving a territory to its shops is
/// <c>IRepScope</c>'s and <c>ITerritoryDirectory</c>'s job, and a Visit that took a territory id
/// would have to learn what one is. So the caller narrows first and asks second.
/// </para>
/// <para>
/// <b>One method, and reading a single visit back is deliberately not here.</b> The decomposition
/// listed it — "a visit by id for the review screen" — and that screen is W12 slice 5, so adding it
/// now would be exactly the guess this note is about, two slices early and shaped by nobody.
/// <c>IVisitContext.FindAsync</c> already answers the thin version; what a review screen actually
/// needs — steps, geofence facts, the outcome reason — lands with the screen that reads them.
/// </para>
/// </remarks>
public interface IVisitQuery
{
    /// <summary>
    /// How the visits at these shops came out, over a closed date range.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Dated by check-in, in UTC, and that is a decision rather than a default.</b> A visit
    /// belongs to the day the rep worked it; dating by check-out would move a call that ran past
    /// midnight into the next day, and dating by when the server heard would move every offline
    /// visit to the day the phone found signal. <c>BR-PRD-6</c>'s business-day rule is about pricing
    /// at a shop's local moment and is a different question from which day a report counts.
    /// </para>
    /// <para>
    /// <b>Both ends inclusive</b>, because the callers are "this cycle" and "this month" — ranges a
    /// person names by their last day, not by the first day of the next one.
    /// </para>
    /// <para>
    /// An empty <paramref name="outletIds"/> answers all-zero rather than everything. A supervisor
    /// whose scope resolved to no shops has no visits, and reading an empty filter as "no filter" is
    /// how a scoped query quietly becomes a tenant-wide one.
    /// </para>
    /// </remarks>
    Task<VisitOutcomeCounts> CountByOutcomeAsync(
        IReadOnlyCollection<Guid> outletIds,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How many <b>planned calls</b> were actually made at these shops — coverage's numerator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not a property on <see cref="VisitOutcomeCounts"/>, and the reason is the unit.</b> That
    /// record counts <i>visits</i>; this counts <i>planned calls</i>, deduplicated. Putting a number
    /// in a different unit beside <c>Total</c> would invite exactly one division — fulfilled ÷ total
    /// visits — which is not coverage and is not anything.
    /// </para>
    /// <para>
    /// <b>Distinct, because two visits can claim one call.</b> Nothing stops a rep checking in twice
    /// at the same shop against the same planned call, and neither should — <c>BR-VIS-2</c>'s whole
    /// posture is to record rather than refuse. But a call that was made twice was still one call
    /// covered, and counting visits here would let coverage exceed the round.
    /// </para>
    /// <para>
    /// <b>Unplanned visits are not counted at all.</b> A rep who drops in on a shop that was not on
    /// the round has done work, and it shows in <see cref="CountByOutcomeAsync"/>; what it has not
    /// done is keep a promise, so it must not push coverage above 100%. That asymmetry is the
    /// argument for this being a separate question rather than a filter the caller applies.
    /// </para>
    /// <para>
    /// <b>Dated by the visit, not by the call.</b> A Tuesday call made on Wednesday counts on
    /// Wednesday, consistently with <see cref="CountByOutcomeAsync"/>. Over a month — the window
    /// these are asked about — the two dates agree everywhere except the edges, and dating by the
    /// plan would mean a visit could land in a window in which nobody worked.
    /// </para>
    /// </remarks>
    Task<int> CountFulfilledCallsAsync(
        IReadOnlyCollection<Guid> outletIds,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);
}
