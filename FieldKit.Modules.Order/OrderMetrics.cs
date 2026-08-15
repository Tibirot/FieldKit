using System.Diagnostics.Metrics;
using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Order;

/// <summary>
/// The commercial signal: what orders are worth (<c>observability §2</c>) — W13 slice 4.
/// </summary>
/// <remarks>
/// <para>
/// <b>A distribution, not a ledger.</b> This says what a typical order looks like and when that
/// changes — an order value p50 that halves overnight is a pricing or assortment problem long before
/// anyone reconciles a month. It is <b>not</b> revenue and must not be summed into one: an order
/// rejected under `BR-ORD-1` is counted here because it was submitted, a correction under `BR-ORD-9`
/// is counted again because it was submitted again, and neither is a mistake in the metric. Revenue
/// is a question for the order table, which has states.
/// </para>
/// <para>
/// <b>Currency is a tag and is load-bearing.</b> A histogram mixing RON and EUR describes nothing at
/// all — the buckets are the same numbers with different meanings — so an amount recorded without one
/// is worse than no amount. `BR-PRD-8` already treats currency as part of a figure rather than
/// decoration; this is the same rule at the metrics layer.
/// </para>
/// <para>
/// <b>The device's total, not the server's</b> (`BR-ORD-2`). The server re-prices and flags; the
/// number the rep and the shopkeeper agreed on is the record, so it is the one measured. Charting the
/// server's would produce a commercial signal nobody in the field ever saw.
/// </para>
/// </remarks>
public sealed class OrderMetrics
{
    private readonly Histogram<double> _value;

    public OrderMetrics(IMeterFactory factory) =>
        _value = factory.Create(Telemetry.MeterName).CreateHistogram<double>(
            "fieldkit.orders.submitted.value",
            unit: "{currency}",
            description: "What a submitted order was worth, as the device totalled it.");

    /// <summary>Records one submitted order's value.</summary>
    /// <remarks>
    /// <c>decimal</c> to <c>double</c> is a real narrowing and is fine <i>here</i>: this is a
    /// histogram bucket, not money to be paid. Money crosses every other boundary in this codebase as
    /// <c>Money</c> or as a string precisely so that this conversion never happens where it matters,
    /// and doing it at the one place it cannot hurt is what keeps that true elsewhere.
    /// </remarks>
    public void Submitted(TenantId tenant, string currencyCode, decimal total) =>
        _value.Record(
            (double)total,
            new KeyValuePair<string, object?>(Telemetry.Tags.Tenant, tenant.Value.ToString()),
            new KeyValuePair<string, object?>(Telemetry.Tags.Currency, currencyCode));
}
