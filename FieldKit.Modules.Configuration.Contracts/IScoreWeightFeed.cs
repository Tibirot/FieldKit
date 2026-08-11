using FieldKit.SharedKernel;

namespace FieldKit.Modules.Configuration.Contracts;

/// <summary>One pillar's weight, as it crosses the wire to a device.</summary>
/// <remarks>
/// The pillar travels as its <b>name</b> and the percentage as a <b>string</b>. The first for the
/// reason every enum on this protocol does; the second because a percentage is a decimal, and
/// <c>JSON.parse</c> turns a bare <c>33.34</c> into a float before the device's scorer ever sees it
/// — which is the one thing <c>BR-AUD-5</c>'s parity cannot survive. The same rule
/// <c>vectors/README.md</c> enforces, applied to the wire it was written about.
/// </remarks>
public sealed record ScoreWeightSnapshot(string Pillar, string Percentage);

/// <summary>
/// One published weighting as the device holds it (<c>AUD-06</c>, <c>BR-AUD-8</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Only published sets travel.</b> A draft is a thing an administrator is still moving sliders on;
/// a device that scored an audit against one would produce a number the server could not reproduce,
/// and would then have that audit refused on push (W10 slice 6). The device should never see a
/// version it cannot legitimately name.
/// </para>
/// <para>
/// <b>The weights travel inside the set</b>, for the reason a workflow's steps do — a device holding
/// two of three pillars would renormalise over a weighting the tenant never wrote.
/// </para>
/// </remarks>
public sealed record ScoreWeightSetSnapshot(
    Guid Id,
    int Version,
    DateTimeOffset PublishedAtUtc,
    IReadOnlyList<ScoreWeightSnapshot> Weights,
    long RowVersion);

/// <summary>One page of weighting changes.</summary>
public sealed record ScoreWeightChangePage(
    IReadOnlyList<ScoreWeightSetSnapshot> Upserts,
    IReadOnlyList<ReferenceTombstone> Tombstones,
    long Cursor);

/// <summary>
/// The perfect-store weightings a device should hold, as a delta (<c>OFF-03</c>, W10 slice 7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every published version, not just the newest — and that is the difference from every other
/// feed here.</b> A workflow or a form has one current shape and a device only needs that. A
/// weighting is different: an audit records the version it was scored against (<c>BR-AUD-8</c>), and
/// a device holding work captured last week still has to be able to show the rep what that audit
/// scored. Sending only the latest would leave a queued audit's breakdown unreadable on the device
/// that produced it.
/// </para>
/// <para>
/// It is also cheap in a way that argument usually is not. A published set is immutable, so a device
/// downloads each version exactly once and never again; the payload is three rows per version, and a
/// tenant re-weights a handful of times a year.
/// </para>
/// <para>
/// <b>Tombstones exist but should never fire.</b> Nothing deletes a published weight set — sealed
/// audits point at them forever — and the feed carries them only because the shape is shared. A
/// tombstone arriving here would mean somebody wrote SQL.
/// </para>
/// </remarks>
public interface IScoreWeightFeed
{
    /// <summary>
    /// Published weightings whose row version is above <paramref name="cursor"/>.
    /// </summary>
    Task<ScoreWeightChangePage> GetChangesAsync(
        long cursor, int limit, CancellationToken cancellationToken = default);
}
