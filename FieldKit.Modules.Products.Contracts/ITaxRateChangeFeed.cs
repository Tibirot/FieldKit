using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products.Contracts;

/// <summary>
/// What one country charges one tax class, as the device holds it (<c>PRD-07</c>) — W11 slice 7b.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Percentage"/> is a <c>string</c></b>, for the reason established across this
/// protocol in slice 7a: a bare <c>19.00</c> is a JSON number, and <c>JSON.parse</c> makes an
/// IEEE-754 float of it before <c>decimal.js</c> is handed anything. Tax is the last place that can
/// be tolerated — it is the final multiplication on a line, so its error lands directly on the total
/// the rep reads out to the shopkeeper.
/// </para>
/// <para>
/// <b>The window travels</b>, like a price list's and a promotion's, and it matters more here: VAT
/// rates change on announced dates, and a device pricing an order dated last Tuesday needs the rate
/// that applied last Tuesday. The window is half-open <c>[EffectiveFrom, EffectiveTo)</c>, which is
/// what keeps the changeover day from being either double-covered or uncovered.
/// </para>
/// </remarks>
public sealed record TaxRateSnapshot(
    Guid Id,
    Guid TaxClassId,
    /// <summary>ISO-3166-1 alpha-2, upper-cased — matched against the outlet's own country.</summary>
    string CountryCode,
    string Percentage,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    long RowVersion);

/// <summary>One page of tax-rate changes.</summary>
public sealed record TaxRateChangePage(
    IReadOnlyList<TaxRateSnapshot> Upserts,
    IReadOnlyList<ReferenceTombstone> Tombstones,
    long Cursor);

/// <summary>
/// The tax rates a device should hold, as a delta (<c>OFF-03</c>, <c>PRD-07</c>) — W11 slice 7b.
/// </summary>
/// <remarks>
/// <para>
/// <b>The input the device was missing.</b> Prices, promotions and the assortment all reached a
/// device in W8; rates did not, and <c>TaxRate</c> was not even <c>ISyncTracked</c> — so there was no
/// delta to send. The effect was quiet rather than broken: <c>priceLine</c> reads a null rate as
/// <i>unknown</i> and charges nothing, so a rep saw a correct-looking net total that the server's
/// recomputation would exceed by exactly the tax on every single order.
/// </para>
/// <para>
/// <b>Tenant-wide, not territory-scoped.</b> A rate is a statement about a country and a class, not
/// about a shop — and the same argument the price-list feed records applies with less discomfort:
/// there is nothing commercially sensitive in "this country charges 19% on standard goods", and
/// narrowing it would need a per-device record of which classes are reachable through the rep's
/// assortment, which changes when the assortment does rather than when a rate does.
/// </para>
/// <para>
/// <b>Expired rates are sent, not filtered</b>, exactly as expired promotions are: a device pricing
/// an order dated before a VAT change needs the rate that was in force then, and filtering here would
/// make an offline device disagree with the server about an order neither of them thinks is unusual.
/// </para>
/// </remarks>
public interface ITaxRateChangeFeed
{
    /// <summary>Tax rates whose row version is above <paramref name="cursor"/>.</summary>
    Task<TaxRateChangePage> GetChangesAsync(
        long cursor, int limit, CancellationToken cancellationToken = default);
}
