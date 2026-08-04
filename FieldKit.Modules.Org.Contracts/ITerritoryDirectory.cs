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
}
