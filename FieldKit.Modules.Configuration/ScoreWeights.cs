using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Configuration;

/// <summary>
/// A pillar of the perfect-store score (<c>AUD-06</c>, <c>BR-AUD-4</c>).
/// </summary>
/// <remarks>
/// <b>A closed set, and deliberately not tenant-defined.</b> A tenant chooses how much each pillar
/// is worth — including nothing — but not what the pillars *are*: each one is computed from data
/// captured in a particular way, so a pillar nobody wrote a measurement for would be a weight with
/// no number behind it. `AUD-09`'s trend views also compare pillars across tenants, which a free-text
/// vocabulary would make meaningless.
/// </remarks>
public enum ScorePillar
{
    /// <summary>Availability against the outlet's MSL (<c>AUD-01</c>, <c>BR-AUD-1</c>).</summary>
    Availability = 0,

    /// <summary>Share of shelf, from facings over the captured category total (<c>AUD-02</c>).</summary>
    ShareOfShelf = 1,

    /// <summary>Observed shelf price against the expected one (<c>AUD-03</c>, <c>BR-AUD-3</c>).</summary>
    PriceCompliance = 2,
}

/// <summary>What one pillar is worth, as a percentage of the whole score.</summary>
public sealed class ScoreWeight : ITenantOwned
{
    public Guid Id { get; private set; }

    public Guid ScoreWeightSetId { get; private set; }

    public ScorePillar Pillar { get; private set; }

    /// <summary>
    /// Percentage points, <c>0</c>–<c>100</c>.
    /// </summary>
    /// <remarks>
    /// <b><c>decimal</c>, not <c>double</c>, and not because these numbers are money.</b> They are
    /// multiplied into a score that has to come out identically on a phone and on a server
    /// (<c>BR-AUD-5</c>), and a weight of <c>33.3</c> that is really <c>33.299999999999997</c> is
    /// exactly where those two answers start to differ. The same discipline pricing uses, for the
    /// same reason.
    /// </remarks>
    public decimal Percentage { get; private set; }

    public TenantId TenantId { get; set; }

    private ScoreWeight() { } // EF

    internal static ScoreWeight Create(Guid setId, ScorePillar pillar, decimal percentage) => new()
    {
        Id = Guid.CreateVersion7(),
        ScoreWeightSetId = setId,
        Pillar = pillar,
        Percentage = percentage,
    };
}

/// <summary>Why a weight set was refused. <see cref="None"/> means it was not.</summary>
public enum WeightSetRefusal
{
    None,

    /// <summary>The percentages do not add up to 100 (<c>BR-AUD-4</c>).</summary>
    DoesNotSumToOneHundred,

    /// <summary>The same pillar appears twice.</summary>
    DuplicatePillar,

    /// <summary>A percentage outside <c>0</c>–<c>100</c>.</summary>
    PercentageOutOfRange,

    /// <summary>No pillars at all — a score of nothing is not a score.</summary>
    Empty,

    /// <summary>Already published, and publishing is one-way (<c>BR-AUD-8</c>).</summary>
    AlreadyPublished,
}

/// <summary>
/// One version of a tenant's perfect-store weighting (<c>AUD-06</c>, <c>AUD-07</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Published sets are frozen, and that is the whole point of versioning them</b> (W10 slice 0,
/// <c>BR-AUD-8</c>). The server recomputes a pushed audit with the weights it was scored against, and
/// that sentence only means something if those weights are a fixed set of numbers. Re-weighting a
/// tenant publishes a *new* version; the old one stays readable forever, because sealed audits point
/// at it.
/// </para>
/// <para>
/// <b>Draft, then published, and publishing is one-way</b> — the same lifecycle a journey plan has,
/// chosen deliberately over a soft "current version" flag. A flag would leave the question "can I
/// still edit this?" answered by a column somebody has to remember to check; making the transition
/// terminal answers it in the aggregate, once.
/// </para>
/// <para>
/// <b>Versions are per tenant and contiguous from 1.</b> An audit records a number, and a number a
/// person can say out loud — "scored on version 3" — is worth more in a support conversation than a
/// GUID. The identity is still the id; the version is what the audit stores.
/// </para>
/// </remarks>
public sealed class ScoreWeightSet : AggregateRoot, ITenantOwned, IAuditable, ISyncTracked
{
    /// <summary>Set by the row-version interceptor, never here (ADR-0013).</summary>
    /// <remarks>
    /// On the root only. The weights have no path of their own — a device holds a *set*, and a
    /// weight that moved without its set moving would be a change no consumer could act on.
    /// </remarks>
    public long RowVersion { get; set; }

    private readonly List<ScoreWeight> _weights = [];

    public Guid Id { get; private set; }

    /// <summary>Monotonic within the tenant, from 1. What an audit records (<c>BR-AUD-8</c>).</summary>
    public int Version { get; private set; }

    /// <summary>Null until published. Publishing is one-way.</summary>
    public DateTimeOffset? PublishedAtUtc { get; private set; }

    public bool IsPublished => PublishedAtUtc is not null;

    public IReadOnlyList<ScoreWeight> Weights => _weights;

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private ScoreWeightSet() { } // EF

    /// <summary>
    /// Drafts a version. <paramref name="version"/> is supplied because only the caller can see the
    /// tenant's other sets.
    /// </summary>
    public static (ScoreWeightSet? Set, WeightSetRefusal Refusal) Draft(
        int version, IReadOnlyList<(ScorePillar Pillar, decimal Percentage)> weights)
    {
        if (Check(weights) is var refusal && refusal is not WeightSetRefusal.None)
        {
            return (null, refusal);
        }

        var set = new ScoreWeightSet { Id = Guid.CreateVersion7(), Version = version };
        set.Replace(weights);

        return (set, WeightSetRefusal.None);
    }

    /// <summary>Replaces the weights of a set that has not been published.</summary>
    public WeightSetRefusal Set(
        IReadOnlyList<(ScorePillar Pillar, decimal Percentage)> weights, IClock clock)
    {
        // The rule slice 0 exists for. A published set that could still be edited would make
        // "recompute with version 3" mean whatever version 3 says today.
        if (IsPublished) return WeightSetRefusal.AlreadyPublished;

        if (Check(weights) is var refusal && refusal is not WeightSetRefusal.None) return refusal;

        Replace(weights);
        ModifiedAtUtc = clock.UtcNow;

        return WeightSetRefusal.None;
    }

    /// <summary>Freezes this version. One-way.</summary>
    public WeightSetRefusal Publish(IClock clock)
    {
        if (IsPublished) return WeightSetRefusal.AlreadyPublished;

        PublishedAtUtc = clock.UtcNow;
        ModifiedAtUtc = clock.UtcNow;

        return WeightSetRefusal.None;
    }

    /// <summary>
    /// Whether a set of weights is one this module will store.
    /// </summary>
    /// <remarks>
    /// <b>Checked on draft *and* on every edit, not only at publish.</b> Refusing at publish would
    /// let an administrator build an invalid set over an afternoon and be told at the end; and it
    /// would make the stored shape "sometimes valid", which every reader would then have to
    /// re-check. `BR-AUD-4` is a property of a weight set, not of a published one.
    /// </remarks>
    private static WeightSetRefusal Check(IReadOnlyList<(ScorePillar Pillar, decimal Percentage)> weights)
    {
        if (weights.Count == 0) return WeightSetRefusal.Empty;

        if (weights.Select(weight => weight.Pillar).Distinct().Count() != weights.Count)
        {
            return WeightSetRefusal.DuplicatePillar;
        }

        if (weights.Any(weight => weight.Percentage is < 0 or > 100))
        {
            return WeightSetRefusal.PercentageOutOfRange;
        }

        /*
         * Summed as `decimal`, and exactly — no tolerance.
         *
         * A tolerance would be the right call for floating point and is the wrong one here: these
         * are decimal percentages an administrator typed, `30 + 30 + 40` is exactly 100 in this
         * type, and `33.33 × 3` is exactly 99.99 and should be refused. Admitting "close enough"
         * would let a set through whose weights the score then renormalises against a total that is
         * not 100 — silently rescaling every audit stored under it.
         */
        return weights.Sum(weight => weight.Percentage) == 100m
            ? WeightSetRefusal.None
            : WeightSetRefusal.DoesNotSumToOneHundred;
    }

    private void Replace(IReadOnlyList<(ScorePillar Pillar, decimal Percentage)> weights)
    {
        _weights.Clear();

        foreach (var (pillar, percentage) in weights)
        {
            _weights.Add(ScoreWeight.Create(Id, pillar, percentage));
        }
    }
}
