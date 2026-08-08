namespace FieldKit.Modules.Journey;

/// <summary>
/// How often an outlet should be visited: <c>VisitsPerCycle</c> calls over <c>CycleLengthDays</c>
/// days (<c>JRN-01</c>).
/// </summary>
/// <remarks>
/// <para>
/// The pair is the unit, not two independent numbers — "twice" means nothing without "over what",
/// and 2×/week and 2×/month are different instructions to a rep. Keeping them together means a rule
/// cannot be stored half-changed, and it gives generation one thing to read.
/// </para>
/// <para>
/// <b>The cycle is a number of days, not a calendar period.</b> Seven and fourteen say exactly what
/// they mean; "monthly" does not, because months are 28 to 31 days and a generator distributing
/// visits across working days would have to pick one. Approximating a month as 28 days is a tenant's
/// choice to make explicitly rather than a meaning this type quietly assigns. If real calendar
/// months are ever needed — <c>JRN-10</c>'s compliance metric is where that would bite — this is the
/// type that changes, which is the reason it is a type.
/// </para>
/// </remarks>
public readonly record struct CallFrequency
{
    /// <summary>The longest cycle this accepts, in days.</summary>
    /// <remarks>
    /// A year, and it is a sanity bound rather than a business rule. A cycle longer than the
    /// planning horizon is a typo — 3650 instead of 365 — and it produces a rule that schedules
    /// nothing while looking configured, which is the failure worth refusing at the door.
    /// </remarks>
    public const int MaximumCycleLengthDays = 365;

    private CallFrequency(int visitsPerCycle, int cycleLengthDays)
    {
        VisitsPerCycle = visitsPerCycle;
        CycleLengthDays = cycleLengthDays;
    }

    /// <summary>How many visits the outlet should get in one cycle.</summary>
    public int VisitsPerCycle { get; }

    /// <summary>How long that cycle is, in days.</summary>
    public int CycleLengthDays { get; }

    /// <summary>
    /// Builds one, or explains why the numbers are not a frequency.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Zero visits is refused rather than stored as "never".</b> A rule that plans nothing and an
    /// outlet with no rule at all are the same outcome and different intentions, and the second is
    /// already expressible by not having a rule. Allowing the first would make "why is this shop
    /// never planned?" a question with two places to look.
    /// </para>
    /// <para>
    /// It does not check that the visits fit the cycle — 20×/week is absurd but arithmetically
    /// meaningful, and whether a rep can actually be sent somewhere twenty times is a question about
    /// their <i>capacity</i>, which is <c>BR-JRN-3</c> and the generator's to answer with a calendar
    /// this type has never seen.
    /// </para>
    /// </remarks>
    public static bool TryCreate(int visitsPerCycle, int cycleLengthDays, out CallFrequency frequency)
    {
        frequency = default;

        if (visitsPerCycle < 1) return false;
        if (cycleLengthDays is < 1 or > MaximumCycleLengthDays) return false;

        frequency = new CallFrequency(visitsPerCycle, cycleLengthDays);
        return true;
    }

    public override string ToString() => $"{VisitsPerCycle}×/{CycleLengthDays}d";
}
