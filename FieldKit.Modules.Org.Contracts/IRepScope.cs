namespace FieldKit.Modules.Org.Contracts;

/// <summary>
/// What one rep covers on one day: the territories assigned to them, and the outlets in those.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ids and nothing else</b>, unlike <see cref="TerritoryDescriptor"/>, which carries a name. The
/// difference is the caller: that one labels a territory on a screen, and this one feeds journey
/// generation, which decides *what to plan* and displays nothing. A name here would be a field the
/// generator does not read and a copy that goes stale — and a consumer that wants to label a
/// territory already has <see cref="ITerritoryDirectory"/>.
/// </para>
/// <para>
/// <b><see cref="OutletIds"/> is flat rather than grouped by territory</b>, because
/// <c>BR-ORG-1</c> gives an outlet exactly one territory, so grouping would carry no information a
/// second lookup could not recover and would push the generator into a nested loop over a shape that
/// can never have a duplicate in it.
/// </para>
/// <para>
/// <see cref="TerritoryIds"/> is here even though generation plans outlets, not territories: it is
/// the answer to "why is this outlet in the plan", and the published plan (<c>JRN-04</c>) is the
/// kind of artefact a supervisor argues with.
/// </para>
/// </remarks>
public sealed record RepCoverage(IReadOnlyList<Guid> TerritoryIds, IReadOnlyList<Guid> OutletIds);

/// <summary>
/// Which outlets a rep covers on a given day (<c>ORG-04</c>).
/// </summary>
/// <remarks>
/// <para>
/// Organization owns the answer: rep assignments are time-bounded rows in its schema and territory
/// membership is another, so a module that needed both would be reading across a schema boundary
/// twice (ADR-0005). This is how it asks instead.
/// </para>
/// <para>
/// <b>Journey generation is the only caller this was designed against</b>, which is the whole reason
/// it was not built before now — it has been named in the module registry since W1 and left unbuilt,
/// because an interface shaped before its consumer asks is a guess the consumer then has to live
/// with. <c>BR-JRN-1</c> plans only for outlets in the rep's active territory, and that sentence is
/// this method.
/// </para>
/// <para>
/// <b>A day, not a range, and singular rather than bulk</b> — both are deliberate, and both are the
/// opposite of the choice <see cref="ITerritoryDirectory"/> made. That one is bulk because its first
/// caller holds a page of outlets and a per-outlet signature would have made fifty rows fifty round
/// trips. This one is asked once per rep per generation run, and coverage is a *per-day* fact: an
/// assignment that ends mid-cycle covers the first half of it and not the second, so a range would
/// have to answer "covered when?" and hand back the periods — which is Organization's model leaking
/// into a caller that only wants a list.
/// </para>
/// </remarks>
public interface IRepScope
{
    /// <summary>
    /// What <paramref name="userId"/> covers on <paramref name="day"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty rather than null when the rep covers nothing that day — an unassigned rep, a rep
    /// between assignments, or a territory with no outlets in it yet are all ordinary states, and
    /// none of them is a failure the caller should have to tell apart from the others.
    /// </para>
    /// <para>
    /// <paramref name="day"/> is a date because an assignment is
    /// (<see cref="FieldKit.SharedKernel.DateRange"/>): "from 1 March" is a statement about days,
    /// and whoever asks decides which timezone made today today. The caller passes the day it means.
    /// </para>
    /// </remarks>
    /// <param name="userId">The Keycloak subject, the same identifier assignments are stored under.</param>
    /// <param name="day">The day to answer for.</param>
    Task<RepCoverage> ForRepAsync(
        string userId, DateOnly day, CancellationToken cancellationToken = default);
}
