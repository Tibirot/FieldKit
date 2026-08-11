namespace FieldKit.Modules.Configuration.Contracts;

/// <summary>What one pillar is worth, as a percentage of the whole score.</summary>
public sealed record WeightedPillar(ScorePillar Pillar, decimal Percentage);

/// <summary>
/// One published version of a tenant's perfect-store weighting (<c>AUD-07</c>, <c>BR-AUD-4</c>).
/// </summary>
/// <param name="PublishedAtUtc">
/// When it was frozen. Carried because a consumer explaining a score wants to be able to say
/// <i>when</i> these numbers came into force, and the alternative is a second call.
/// </param>
public sealed record ScoreWeightSetDescriptor(
    int Version, DateTimeOffset PublishedAtUtc, IReadOnlyList<WeightedPillar> Weights);

/// <summary>
/// A tenant's perfect-store weighting, by version (<c>CFG-05</c>, <c>AUD-07</c>, <c>BR-AUD-8</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately absent from W10 slice 1, where the weight sets themselves shipped.</b> The rule
/// then was the one this codebase keeps applying: an interface waits for its caller, because a shape
/// designed against a consumer nobody has thought about is a guess that consumer has to live with.
/// Audit's ingest is that caller, and the shape follows from the one question it asks — <i>what did
/// version 3 say?</i> — rather than from the several that were imagined.
/// </para>
/// <para>
/// <b>Published sets only, and that is the whole point of the contract.</b> <c>BR-AUD-8</c> has the
/// server recompute a sealed audit with the weights it was scored against; a draft can still be
/// edited, so an audit scored against one would have a score nobody could reproduce. Returning null
/// for a draft is what makes "recompute with version 3" mean something.
/// </para>
/// <para>
/// <b>No "current version" method.</b> Nothing asks for one: the device pulls published sets through
/// a change feed (W10 slice 7), and the back office reads them through the API. A convenience method
/// here would be a third way to answer a question two paths already answer, and the first caller to
/// use it would be one that should have recorded a version instead.
/// </para>
/// </remarks>
public interface IScoreWeights
{
    /// <summary>
    /// The published weighting with this version, or null when the tenant has no such published set.
    /// </summary>
    /// <remarks>
    /// One answer for "no such version" and "that version is still a draft". A caller cannot act on
    /// the difference — both mean the score cannot be reproduced — and collapsing them keeps a
    /// device from learning which draft versions a tenant is working on.
    /// </remarks>
    Task<ScoreWeightSetDescriptor?> ByVersionAsync(
        int version, CancellationToken cancellationToken = default);
}
