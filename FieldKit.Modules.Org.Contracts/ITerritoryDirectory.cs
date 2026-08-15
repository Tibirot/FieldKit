namespace FieldKit.Modules.Org.Contracts;

/// <summary>A territory, as a module that only needs to name it sees it.</summary>
/// <remarks>
/// Id and name, and deliberately nothing else. A caller showing "which territory covers this shop"
/// needs to label it and link to it; the org unit it hangs off, its rep and its coverage are
/// Organization's business, and a consumer that could read them here would soon be making decisions
/// from a copy that goes stale.
/// </remarks>
public sealed record TerritoryDescriptor(Guid Id, string Name);

/// <summary>
/// Which territory covers an outlet (<c>ORG-05</c>).
/// </summary>
/// <remarks>
/// <para>
/// Organization owns the answer: the membership rows live in its schema, and
/// <c>BR-ORG-1</c>'s "one territory per outlet" is a unique index there. This is how another module
/// asks, rather than reading across a schema boundary (ADR-0005).
/// </para>
/// <para>
/// <b>Bulk by design.</b> The first consumer is a list of outlets, and a per-outlet signature would
/// make a fifty-row table fifty round trips — the shape of the interface is what decides whether that
/// is even possible, so it is decided here rather than left to a caller's discipline.
/// </para>
/// <para>
/// <b>This is the direction the Organization module was originally written to avoid</b>, and the
/// reversal is deliberate — see <c>Territory.cs</c> for what changed and why the reason it was
/// avoided turned out not to apply.
/// </para>
/// </remarks>
public interface ITerritoryDirectory
{
    /// <summary>
    /// The territory covering each of <paramref name="outletIds"/>, for those that are in one.
    /// </summary>
    /// <remarks>
    /// An outlet in no territory is <b>absent from the result</b> rather than present with a null.
    /// Outlets are created before anyone decides who covers them, so "not yet assigned" is an
    /// ordinary state and not a failure — and a caller that has to handle a missing key cannot
    /// forget to.
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, TerritoryDescriptor>> ForOutletsAsync(
        IReadOnlyCollection<Guid> outletIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// The shops in <paramref name="territoryId"/>, or in every territory when it is null — the
    /// scope a report is totalled over (<c>ORG-05</c>) — W12 slice 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The other direction, and it exists because nothing could answer it.</b> Every reporting
    /// aggregate built in W12 takes outlet ids, and when the composition in
    /// <c>/api/reporting/summary</c> came to produce that list there was no contract that could:
    /// <c>IOutletCatalog</c> resolves ids it is given, <see cref="ForOutletsAsync"/> maps the other
    /// way, and <c>IRepScope</c> answers about one rep on one day. A caller reduced to enumerating
    /// outlets over HTTP to answer a territory question is the shape this contract exists to prevent.
    /// </para>
    /// <para>
    /// <b>An outlet in no territory is in no scope, including the unfiltered one.</b> Null here means
    /// "every territory", not "every outlet" — so a shop nobody has been made responsible for is
    /// absent from the dashboard entirely. That is the honest reading of a report about coverage: an
    /// unassigned shop has no round to be measured against, no rep who failed to call, and putting it
    /// in a denominator would make somebody accountable for work nobody was asked to do. It also
    /// means the per-territory figures add up to the unfiltered one, which they would not if the
    /// unfiltered case quietly included orphans.
    /// </para>
    /// <para>
    /// <b>Ids only</b>, for <see cref="RepCoverage"/>'s reason: the caller feeds them to four
    /// aggregates that display nothing, and a name here would be a field none of them reads.
    /// </para>
    /// <para>
    /// A territory that does not exist, or belongs to another tenant, comes back <b>empty</b> rather
    /// than as an error — the same nothing an empty territory gives. A caller cannot use this to
    /// discover whether somebody else's territory id is real.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<Guid>> OutletsInAsync(
        Guid? territoryId, CancellationToken cancellationToken = default);
}
