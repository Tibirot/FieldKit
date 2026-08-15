using System.Diagnostics;
using FieldKit.BuildingBlocks;

namespace FieldKit.Modules.Products;

/// <summary>
/// The spans pricing leaves behind (<c>observability §1</c>) — W13 slice 2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pricing is on the list because it is the one hot path with a stated budget</b> — sub-ms on
/// device (<c>observability §6</c>) — and because the server side of it is <i>gathering</i>, not
/// arithmetic: three queries whose cost grows with a tenant's price lists and promotions rather than
/// with the order. When a rep waits, this is where the waiting is, and a span says which of the
/// three it was.
/// </para>
/// <para>
/// Its own source name would be a second subscription for one span. <c>Telemetry</c> settles that:
/// one name, and the span names carry the area — <c>products.pricing.resolve</c> reads as clearly
/// under one source as it would under two.
/// </para>
/// </remarks>
internal static class ProductsTracing
{
    private static readonly ActivitySource Source = new(Telemetry.ActivitySourceName);

    /// <summary>The span for pricing one order's worth of lines.</summary>
    /// <remarks>
    /// The outlet id goes on it, and it is unbounded — which is the point of a span rather than a
    /// metric. "This shop's orders are slow to price" is a question about one shop, and answering it
    /// from a tag on a histogram would mean a series per outlet in the tenant.
    /// </remarks>
    public static Activity? Pricing(Guid outletId, int lines)
    {
        var activity = Source.StartActivity("products.pricing.resolve");

        activity?.SetTag(Telemetry.Tags.Outlet, outletId.ToString());
        activity?.SetTag("fieldkit.pricing.lines", lines);

        return activity;
    }
}
