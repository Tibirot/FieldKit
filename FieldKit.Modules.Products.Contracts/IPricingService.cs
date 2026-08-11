using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products.Contracts;

/// <summary>One thing to price: a product and how much of it.</summary>
/// <param name="Quantity">
/// In the product's own unit of measure. Decimal, because a unit can be a weight — the same reason
/// an order line carries one.
/// </param>
public sealed record LineToPrice(Guid ProductId, decimal Quantity);

/// <summary>
/// What one line came to, and why.
/// </summary>
/// <param name="PriceListId">
/// Which list said so. Half of the answer to "why am I being charged this" — a rep told a price they
/// did not expect asks their supervisor, and a supervisor who can name the list can answer without
/// opening a database.
/// </param>
/// <param name="PromotionId">The one promotion that applied, or null.</param>
public sealed record PricedOrderLine(
    Guid ProductId,
    decimal Quantity,
    Money UnitPrice,
    Guid PriceListId,
    Guid? PromotionId,
    Money Subtotal,
    Money Discount,
    Money Net,
    Money Tax,
    Money Total);

/// <summary>
/// What a whole order came to.
/// </summary>
/// <param name="Unpriced">
/// Products this outlet has no price for on this date, in the order they were asked about.
/// <para>
/// <b>Reported rather than refused, and rather than priced at zero.</b> A missing price is a
/// configuration gap — a product outside every list that reaches this shop — and the caller needs to
/// tell "the tenant charges nothing for this" apart from "nobody said". Zero would silently pass the
/// first meaning off as the second.
/// </para>
/// </param>
public sealed record PricedOrder(
    string CurrencyCode,
    IReadOnlyList<PricedOrderLine> Lines,
    Money Subtotal,
    Money Discount,
    Money Net,
    Money Tax,
    Money Total,
    IReadOnlyList<Guid> Unpriced);

/// <summary>
/// What an order costs at an outlet on a date (<c>ORD-02</c>, <c>ORD-03</c>, <c>BR-ORD-2</c>).
/// </summary>
/// <remarks>
/// <para>
/// Named in the [order spec §8](../docs/product/23-order-capture.md) as something Order consumes, and
/// built here now that it has a caller — the rule this codebase applies to every contract. Order
/// cannot reach <c>PriceResolver</c>, <c>PromotionResolver</c>, <c>TaxEngine</c> or
/// <c>LinePricing</c>: they live in this module's implementation assembly and AT-1 refuses the
/// reference. This is the seam.
/// </para>
/// <para>
/// <b>The date is a parameter, never a clock read.</b> Resolution has to be reproducible: an order
/// re-priced during sync must resolve against the day it was <i>taken</i>, not the day it arrived,
/// and an outlet in Bucharest changes day six hours before one in London (<c>BR-PRD-6</c>). Every
/// resolver in this module takes the date for the same reason, and a service that defaulted it would
/// undo all four.
/// </para>
/// <para>
/// <b>It answers, it does not decide.</b> Whether an order may be placed at all — the assortment gate
/// (<c>BR-ORD-1</c>), the minimum (<c>BR-ORD-5</c>) — is Order's, and lives in Order's aggregate.
/// This says what the goods cost, which is a different question from whether they may be sold.
/// </para>
/// </remarks>
public interface IPricingService
{
    /// <summary>
    /// Prices <paramref name="lines"/> for <paramref name="outletId"/> as of <paramref name="on"/>,
    /// or null when this tenant has no such outlet.
    /// </summary>
    /// <remarks>
    /// Null rather than an empty order: "no such outlet" and "an outlet whose products are all
    /// unpriced" are different facts, and a caller showing a rep an empty total needs to know which
    /// it is looking at.
    /// </remarks>
    Task<PricedOrder?> PriceAsync(
        Guid outletId,
        DateOnly on,
        IReadOnlyList<LineToPrice> lines,
        CancellationToken cancellationToken = default);
}
