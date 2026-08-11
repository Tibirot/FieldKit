namespace FieldKit.Modules.Configuration.Contracts;

/// <summary>
/// A pillar of the perfect-store score (<c>AUD-06</c>, <c>BR-AUD-4</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A closed set, and deliberately not tenant-defined.</b> A tenant chooses how much each pillar
/// is worth — including nothing — but not what the pillars <i>are</i>: each one is computed from data
/// captured in a particular way, so a pillar nobody wrote a measurement for would be a weight with
/// no number behind it. <c>AUD-09</c>'s trend views also compare pillars across tenants, which a
/// free-text vocabulary would make meaningless.
/// </para>
/// <para>
/// <b>Here rather than inside the module, from W10 slice 4.</b> It shipped in slice 1 next to
/// <c>ScoreWeightSet</c>, which was right while Configuration was the only module that had a use for
/// it. Audit's scorer is the second, and a module may reference only another's <c>Contracts</c>
/// (AT-1) — so the vocabulary moves out and the aggregate that stores it stays in. The alternative
/// was Audit declaring its own three-member enum, which is how one closed vocabulary becomes two that
/// drift.
/// </para>
/// <para>
/// The move is additive: nothing loses access, and the ordinals are unchanged — which matters,
/// because <c>score_weight.Pillar</c> is stored <b>by name</b> and re-ordering the members would
/// re-interpret every stored weight.
/// </para>
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
