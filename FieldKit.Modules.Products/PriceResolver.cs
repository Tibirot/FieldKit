namespace FieldKit.Modules.Products;

/// <summary>Which kind of assignment a resolved price came from.</summary>
/// <remarks>
/// <para>
/// Returned rather than inferred, so a caller can explain the answer. "Why is this shop paying
/// this?" is the question a field rep asks a supervisor, and the specificity is most of the answer.
/// </para>
/// <para>
/// <b>Ordered least-specific first, and <see cref="PriceResolver"/> compares these values with
/// <c>&gt;</c>.</b> That is a real coupling and it is why the numbers are written out: a scope
/// inserted in the middle would silently re-rank every price in the system, with no test failing
/// except the vectors. A new scope goes at the end if it is more specific than <see cref="Outlet"/>,
/// and otherwise the members get renumbered <i>and</i> the wire format checked — these cross the API
/// by name (<c>ResolvedPriceResponse.Scope</c>), so renumbering is safe for clients but never for
/// stored ordinals.
/// </para>
/// </remarks>
public enum PriceScope
{
    /// <summary>Set for every outlet trading in a channel.</summary>
    Channel = 0,

    /// <summary>Set for one shop, deliberately — an exception to its channel's price.</summary>
    Outlet = 1,
}

/// <summary>
/// One price this product could have on this date, and where it came from.
/// </summary>
/// <remarks>
/// Deliberately flat and self-contained: no ids to follow, no entities, nothing to load. That is
/// what lets <see cref="PriceResolver"/> be a pure function over data the caller has already
/// gathered — and what lets the same shape exist in TypeScript for the device mirror (<c>PRD-08</c>)
/// without dragging an ORM behind it.
/// </remarks>
/// <param name="Amount">Net, in <paramref name="Currency"/>. A decimal, never a float (<c>BR-PRD-8</c>).</param>
public sealed record PriceCandidate(
    Guid PriceListId,
    PriceScope Scope,
    string Currency,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    decimal Amount);

/// <summary>The price that applies, and which candidate won.</summary>
public sealed record ResolvedPrice(
    Guid PriceListId, PriceScope Scope, string Currency, decimal Amount);

/// <summary>
/// Picks the price that applies to one product at one outlet on one date (<c>PRD-04</c>,
/// <c>BR-PRD-2</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure and side-effect-free</b> (<c>BR-PRD-7</c>): candidates in, one answer out. No database, no
/// clock, no tenant context, no logging. That is not tidiness — it is the requirement that makes the
/// same rules runnable on a device that is offline, and testable against vectors a TypeScript
/// implementation must also satisfy (<c>PRD-08</c>).
/// </para>
/// <para>
/// The <i>date</i> is a parameter rather than read from a clock for the same reason. Resolution has
/// to be reproducible: the price an order was placed at must still resolve to that price when the
/// order syncs three days later, and a function that asks what day it is cannot promise that.
/// </para>
/// </remarks>
public static class PriceResolver
{
    /// <summary>
    /// The applicable price, or null when nothing covers <paramref name="on"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null is a real answer.</b> A product with no price list covering the date is not an error
    /// here — it is a product this outlet cannot be sold, which is a decision for the caller
    /// (<c>BR-PRD-4</c> territory) rather than an exception thrown from inside a pure function.
    /// </para>
    /// <para>
    /// Order of preference, per <c>BR-PRD-2</c>:
    /// </para>
    /// <list type="number">
    /// <item>candidates whose window covers the date — everything else is not a candidate at all;</item>
    /// <item><see cref="PriceScope.Outlet"/> beats <see cref="PriceScope.Channel"/>, always, even
    /// when the channel list starts later. A price set for one shop is a deliberate exception, and a
    /// newer channel-wide list should not silently overwrite it;</item>
    /// <item>within one scope, the latest <c>EffectiveFrom</c> wins — the most recent decision;</item>
    /// <item>still tied, the higher <c>PriceListId</c> wins, ordered as bytes. See below.</item>
    /// </list>
    /// </remarks>
    public static ResolvedPrice? Resolve(IReadOnlyList<PriceCandidate> candidates, DateOnly on)
    {
        ResolvedPrice? best = null;
        PriceCandidate? winner = null;

        foreach (var candidate in candidates)
        {
            if (!Covers(candidate, on)) continue;
            if (winner is not null && !Beats(candidate, winner)) continue;

            winner = candidate;
            best = new ResolvedPrice(
                candidate.PriceListId, candidate.Scope, candidate.Currency, candidate.Amount);
        }

        return best;
    }

    /// <summary>Half-open: <c>[EffectiveFrom, EffectiveTo)</c>, matching <see cref="PriceList"/>.</summary>
    private static bool Covers(PriceCandidate candidate, DateOnly on) =>
        on >= candidate.EffectiveFrom
        && (candidate.EffectiveTo is not { } end || on < end);

    /// <summary>
    /// Whether <paramref name="challenger"/> should displace <paramref name="holder"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The last comparison is the one worth explaining.</b> Two lists at the same scope with the
    /// same effective date is a data problem — an author has said two contradictory things — and no
    /// tiebreak makes it *right*. What a tiebreak buys is determinism: the same inputs give the same
    /// answer on the server and on every device, so a rep and a supervisor looking at the same shop
    /// see the same number, and an order re-priced during sync does not change.
    /// </para>
    /// <para>
    /// Comparing ids gives that. Ids here are UUIDv7, which are creation-ordered, so the higher id is
    /// the more recently authored list — the same instinct as the effective-date rule above, applied
    /// one level down.
    /// </para>
    /// <para>
    /// <b>Ordered as big-endian bytes, deliberately not <c>Guid.CompareTo</c>.</b> .NET compares a
    /// Guid's first three fields as native-endian <i>signed</i> integers, so <c>ffffffff-…</c> sorts
    /// <i>below</i> <c>00000001-…</c> — the sign bit of <c>_a</c> inverts half the ordering, and the
    /// endianness scrambles it within each field. TypeScript, given the same two ids as canonical
    /// strings, would compare them lexicographically and reach the opposite answer. Two engines that
    /// are supposed to agree by construction would then disagree on a rule neither author thought was
    /// interesting. Big-endian byte order is what the canonical string spells out, so both languages
    /// can implement it from the same sentence.
    /// </para>
    /// </remarks>
    private static bool Beats(PriceCandidate challenger, PriceCandidate holder)
    {
        if (challenger.Scope != holder.Scope) return challenger.Scope > holder.Scope;

        if (challenger.EffectiveFrom != holder.EffectiveFrom)
        {
            return challenger.EffectiveFrom > holder.EffectiveFrom;
        }

        Span<byte> left = stackalloc byte[16];
        Span<byte> right = stackalloc byte[16];
        challenger.PriceListId.TryWriteBytes(left, bigEndian: true, out _);
        holder.PriceListId.TryWriteBytes(right, bigEndian: true, out _);

        return left.SequenceCompareTo(right) > 0;
    }
}
