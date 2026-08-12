using System.Globalization;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products;

/// <summary>Which scope an order minimum reached this outlet through.</summary>
/// <remarks>
/// Names rather than numbers, and the same two <see cref="PriceScope"/> carries — what crosses a
/// boundary is the word, and the ordinals are storage.
/// </remarks>
public enum OrderMinimumScope
{
    Channel = 0,
    Outlet = 1,
}

/// <summary>One minimum this order could have to meet, and where it came from.</summary>
/// <remarks>
/// <c>Amount</c> stays the string it arrived as, for the reason <see cref="PriceCandidate"/> gives:
/// resolution is a *selection* rule and parsing here would mean re-formatting on the way out.
/// </remarks>
public sealed record OrderMinimumCandidate(
    Guid OrderMinimumId, OrderMinimumScope Scope, string CurrencyCode, string Amount);

/// <summary>The minimum that applies, and which candidate won.</summary>
public sealed record ResolvedOrderMinimum(
    Guid OrderMinimumId, OrderMinimumScope Scope, string CurrencyCode, string Amount);

/// <summary>
/// Picks the minimum that applies to one outlet (<c>ORD-06</c>, <c>BR-ORD-5</c>) — W11 slice 8b-i.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure</b>, like the three resolvers beside it: candidates in, one answer out. No fetch, no
/// clock, no storage. That is what will let the device run the identical rule offline in 8b-ii, and
/// what lets one vector file check both languages.
/// </para>
/// <para>
/// <b>Null is the ordinary answer, not an error.</b> <c>BR-ORD-5</c> says a minimum applies *if
/// configured*, and most tenants will configure none — so "no minimum" has to be a first-class
/// result rather than something a caller infers from an empty list. An order with no minimum is
/// submittable at any value, which is the behaviour every order has had until now.
/// </para>
/// <para>
/// <b>Outlet beats channel, and there is no date.</b> The first half is <c>B1</c>'s "per
/// channel/outlet" read the way `BR-PRD-2` reads it. The second is worth stating because every other
/// rule in this module has one: a price list and a promotion both have windows, and a minimum does
/// not — nothing in <c>B1</c> or <c>ORD-06</c> asks for a minimum that starts on a date, and
/// inventing one would be a field with no requirement and a migration to remove.
/// </para>
/// <para>
/// <b>A tie is broken by id, for agreement rather than for correctness.</b> Two minimums at the same
/// scope is a data problem no tiebreak makes right; what it buys is that the server and every device
/// refuse the same order, which is the whole point of resolving on both sides.
/// </para>
/// </remarks>
public static class OrderMinimumResolver
{
    public static ResolvedOrderMinimum? Resolve(IReadOnlyList<OrderMinimumCandidate> candidates)
    {
        OrderMinimumCandidate? winner = null;

        foreach (var candidate in candidates)
        {
            if (winner is not null && !Beats(candidate, winner)) continue;

            winner = candidate;
        }

        return winner is null
            ? null
            : new ResolvedOrderMinimum(
                winner.OrderMinimumId, winner.Scope, winner.CurrencyCode, winner.Amount);
    }

    /// <summary>
    /// Whether <paramref name="challenger"/> should displace <paramref name="holder"/>.
    /// </summary>
    /// <remarks>
    /// Lower-cased before comparing the ids, which is load-bearing rather than cosmetic: in ASCII
    /// <c>'A'–'F' &lt; 'a'–'f'</c>, so an id spelled in upper case would sort below every lower-case
    /// one and the winner would depend on how somebody typed a GUID. The same rule
    /// <c>PriceResolver</c> states.
    /// </remarks>
    private static bool Beats(OrderMinimumCandidate challenger, OrderMinimumCandidate holder)
    {
        if (challenger.Scope != holder.Scope) return challenger.Scope > holder.Scope;

        return string.CompareOrdinal(
            challenger.OrderMinimumId.ToString("D").ToLowerInvariant(),
            holder.OrderMinimumId.ToString("D").ToLowerInvariant()) > 0;
    }

    /// <summary>
    /// Whether an order of <paramref name="total"/> meets <paramref name="minimum"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Separate from resolution on purpose.</b> Picking which rule applies and deciding whether an
    /// order satisfies it are two questions, and only the first has a precedence story; keeping them
    /// apart is what lets the device show the rep the threshold before they have added a line.
    /// </para>
    /// <para>
    /// <b>A mismatched currency is a refusal to answer, not a refusal of the order.</b> Comparing
    /// 50 EUR against 200 RON by their numbers alone would refuse orders that are comfortably over
    /// the intended threshold, and accept ones under it — and it would look like the rule working.
    /// <see cref="Money"/> throws across currencies, which is the right instinct in arithmetic and
    /// the wrong outcome for a rep at a counter, so this reports the disagreement instead.
    /// </para>
    /// </remarks>
    public static OrderMinimumVerdict Check(ResolvedOrderMinimum? minimum, Money total)
    {
        if (minimum is null) return OrderMinimumVerdict.None;

        if (!string.Equals(minimum.CurrencyCode, total.Currency, StringComparison.OrdinalIgnoreCase))
        {
            return OrderMinimumVerdict.CurrencyMismatch;
        }

        /*
         * The same parse every other decimal in this module gets, and the same refusal of thousands
         * separators: `NumberStyles.Number` would read "1,500" as 1500 under invariant culture, and
         * a minimum a tenant meant as one and a half would silently become fifteen hundred.
         */
        if (!decimal.TryParse(
                minimum.Amount,
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var threshold))
        {
            // Its own answer rather than borrowing the currency one: the write path validates the
            // amount, so reaching here means the stored row is broken — and telling a rep their
            // currencies disagree about that would send them looking for the wrong thing.
            return OrderMinimumVerdict.Unreadable;
        }

        return total.Amount >= threshold ? OrderMinimumVerdict.Met : OrderMinimumVerdict.NotMet;
    }
}

/// <summary>What an order minimum says about one order.</summary>
public enum OrderMinimumVerdict
{
    /// <summary>Nothing is configured for this outlet — every order passes.</summary>
    None = 0,

    Met = 1,
    NotMet = 2,

    /// <summary>
    /// The minimum and the order are in different currencies, so the rule cannot answer.
    /// </summary>
    /// <remarks>
    /// Its own value rather than folded into <see cref="NotMet"/>: a rep told "your order is too
    /// small" about a misconfiguration would add stock nobody asked for and still be refused.
    /// </remarks>
    CurrencyMismatch = 3,

    /// <summary>The stored amount is not a decimal — a broken row, not a small order.</summary>
    Unreadable = 4,
}
