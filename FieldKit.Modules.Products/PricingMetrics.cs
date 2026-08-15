using System.Diagnostics.Metrics;
using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products;

/// <summary>
/// How long it takes to price an order (<c>observability §2</c>, §6) — W13 slice 4.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one metric in this week with a number written down beside it.</b> `observability §6` sets
/// a sub-millisecond budget for the hot pricing path — on the device, where the arithmetic is pure
/// and the catalogue is local. This measures the <i>server</i> side, which is a different job: three
/// gathering queries whose cost grows with a tenant's price lists and promotions rather than with the
/// order. So the budget does not transfer, and pretending it did would produce an alert that fires
/// on a healthy system.
/// </para>
/// <para>
/// What it is for instead: the shape over time. Pricing is on the path a rep waits on, and the
/// failure this catches is a tenant whose promotion table has grown until gathering is slow — which
/// no functional test would ever notice, because every one of them runs against a handful of rows.
/// </para>
/// <para>
/// <b>No outlet tag.</b> "This shop is slow to price" is a real question and it is a *trace*
/// question — `products.pricing.resolve` carries the outlet (slice 2). One series per outlet is the
/// unbounded-tag mistake `Telemetry` exists to refuse.
/// </para>
/// </remarks>
public sealed class PricingMetrics
{
    private readonly Histogram<double> _duration;

    public PricingMetrics(IMeterFactory factory) =>
        _duration = factory.Create(Telemetry.MeterName).CreateHistogram<double>(
            "fieldkit.pricing.resolve.duration",
            unit: "ms",
            description: "How long the server took to gather and price one order's lines.");

    /// <summary>Records one pricing pass.</summary>
    /// <remarks>
    /// Recorded for an outlet this tenant does not have as well — that call answers <c>null</c> after
    /// one query rather than four, and dropping it would quietly bias the distribution towards the
    /// expensive path.
    /// </remarks>
    public void Resolved(TenantId tenant, TimeSpan elapsed) =>
        _duration.Record(
            elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>(Telemetry.Tags.Tenant, tenant.Value.ToString()));
}
