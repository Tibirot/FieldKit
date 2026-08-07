using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products;

/// <summary>One rate that could apply, and when it does.</summary>
/// <remarks>
/// Flat and self-contained, like <see cref="PriceCandidate"/> and <see cref="PromotionCandidate"/>.
/// The country is not here: the caller has already filtered to one jurisdiction, because a rate for
/// somewhere else is not a candidate at all rather than a losing one.
/// </remarks>
public sealed record TaxRateCandidate(
    Guid TaxRateId, decimal Percentage, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

/// <summary>A net amount, the tax on it, and the two added up.</summary>
/// <remarks>
/// All three are returned rather than leaving the caller to add them, so the rounding happens once
/// and in one place. A caller that computed <c>gross = net + tax</c> itself would agree with this
/// today and diverge the day the policy changes.
/// </remarks>
public sealed record TaxedAmount(Money Net, Money Tax, Money Gross);

/// <summary>
/// Which tax rate applies, and what it does to a line (<c>PRD-07</c>, <c>BR-PRD-5</c>).
/// </summary>
/// <remarks>
/// <para>
/// Pure and side-effect-free (<c>BR-PRD-7</c>), like the two resolvers before it, and for the sharpest
/// reason of the three: this is where <c>BR-PRD-9</c>'s rounding policy actually bites. A device and
/// the server disagreeing by one cent on a VAT line is a reconciliation problem someone chases
/// through a ledger, so the arithmetic lives in one function with vectors either language can run.
/// </para>
/// <para>
/// <b>Tax is computed at order time, not stored</b> (<c>BR-PRD-5</c>). Prices are net; the same
/// product sold in two countries is taxed differently, and a gross price would bake one jurisdiction
/// into the catalogue.
/// </para>
/// </remarks>
public static class TaxEngine
{
    /// <summary>
    /// The rate applying on <paramref name="on"/>, or null when none does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null is not zero, and the distinction is the point.</b> No rate authored for this class in
    /// this country means <i>unknown</i>; a rate of 0% means zero-rated. Collapsing them would let a
    /// missing setup step invoice as tax-free and look deliberate — which is why authoring accepts a
    /// 0% rate rather than making tenants express it by omission.
    /// </para>
    /// <para>
    /// Rules: the window must cover the date (half-open); the latest <c>EffectiveFrom</c> wins, which
    /// is how a rate change on an announced date takes over from its predecessor; and a tie is broken
    /// by the higher id as big-endian bytes, the same deterministic last resort the other two
    /// resolvers use for the same reason.
    /// </para>
    /// </remarks>
    public static TaxRateCandidate? Resolve(IReadOnlyList<TaxRateCandidate> candidates, DateOnly on)
    {
        TaxRateCandidate? best = null;

        foreach (var candidate in candidates)
        {
            if (on < candidate.EffectiveFrom) continue;
            if (candidate.EffectiveTo is { } end && on >= end) continue;
            if (best is not null && !Beats(candidate, best)) continue;

            best = candidate;
        }

        return best;
    }

    /// <summary>
    /// Applies <paramref name="percentage"/> to <paramref name="net"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On the rounded net line, and rounded again after.</b> Two roundings, both deliberate. The
    /// net is rounded first because that is the figure printed on the order and the one a shopkeeper
    /// checks — computing tax on an unrounded intermediate would produce a tax that does not match
    /// the net anyone can see. The tax is then rounded because it is money in its own right, going on
    /// its own line of an invoice.
    /// </para>
    /// <para>
    /// Half-up (away from zero) at both steps, per <c>BR-PRD-9</c> — <see cref="Money.Round"/> owns
    /// that policy, and this defers to it rather than restating it. Banker's rounding, which is
    /// .NET's default and would have been the accident of writing <c>Math.Round</c> without thinking,
    /// disagrees on exactly the half-cent cases a tax line hits constantly.
    /// </para>
    /// <para>
    /// <b>Gross is net plus tax, not net times 1.19.</b> Those differ once rounding is involved, and
    /// the first is the one an invoice has to be able to show as three consistent numbers.
    /// </para>
    /// </remarks>
    public static TaxedAmount Apply(Money net, decimal percentage)
    {
        var rounded = net.Round();
        var tax = new Money(rounded.Amount * percentage / 100m, rounded.Currency).Round();

        return new TaxedAmount(rounded, tax, rounded + tax);
    }

    /// <summary>Latest effective date, then the higher id as big-endian bytes.</summary>
    /// <remarks>
    /// Byte order rather than <c>Guid.CompareTo</c>, which reads .NET's first Guid field as a signed
    /// native-endian int and sorts <c>ffffffff-…</c> below <c>00000001-…</c>. The device mirror
    /// comparing canonical strings would disagree. Explained at length on
    /// <see cref="PriceResolver"/>; repeated here in shape but not in prose.
    /// </remarks>
    private static bool Beats(TaxRateCandidate challenger, TaxRateCandidate holder)
    {
        if (challenger.EffectiveFrom != holder.EffectiveFrom)
        {
            return challenger.EffectiveFrom > holder.EffectiveFrom;
        }

        Span<byte> left = stackalloc byte[16];
        Span<byte> right = stackalloc byte[16];
        challenger.TaxRateId.TryWriteBytes(left, bigEndian: true, out _);
        holder.TaxRateId.TryWriteBytes(right, bigEndian: true, out _);

        return left.SequenceCompareTo(right) > 0;
    }
}
