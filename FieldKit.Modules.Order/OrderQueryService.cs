using FieldKit.Modules.Order.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Order;

/// <summary>Reading what was ordered (<c>ORD-01</c>) — W11 slice 1.</summary>
/// <remarks>
/// <c>AsNoTracking</c> throughout: every caller here is a reader, and tracking a graph of orders and
/// their lines to hand back descriptors is work spent on changes nobody will make.
/// </remarks>
internal sealed class OrderQueryService(OrderDbContext db) : IOrderQuery
{
    public async Task<OrderDescriptor?> ForVisitAsync(
        Guid visitId, CancellationToken cancellationToken = default)
    {
        var order = await db.Orders
            .AsNoTracking()
            .Include(row => row.Lines)
            .Include(row => row.Submissions)
            .SingleOrDefaultAsync(row => row.VisitId == visitId, cancellationToken);

        return order?.Describe();
    }

    public async Task<IReadOnlyList<OrderDescriptor>> ForOutletAsync(
        Guid outletId, CancellationToken cancellationToken = default)
    {
        var orders = await db.Orders
            .AsNoTracking()
            .Include(row => row.Lines)
            .Include(row => row.Submissions)
            // Newest first, by when the rep *captured* it rather than when this server heard —
            // an order taken on Tuesday and pushed on Thursday belongs on Tuesday.
            .Where(row => row.OutletId == outletId)
            .OrderByDescending(row => row.CapturedAtUtc)
            .ToListAsync(cancellationToken);

        return [.. orders.Select(order => order.Describe())];
    }

    public async Task<OrderSummary> SummariseAsync(
        IReadOnlyCollection<Guid> outletIds,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        // Nothing in scope is not the same as no filter — said out loud here, as in the other three.
        if (outletIds.Count == 0) return Empty;

        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var orders = db.Orders
            .AsNoTracking()
            .Where(order => outletIds.Contains(order.OutletId))
            .Where(order => order.CapturedAtUtc >= start && order.CapturedAtUtc < end);

        // Submitted and Accepted are orders somebody expects to be delivered. Rejected and Cancelled
        // are counted, but never as value — see `OrderSummary`.
        var standing = orders.Where(order =>
            order.Status == OrderStatus.Submitted || order.Status == OrderStatus.Accepted);

        var counts = await orders
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Rejected = group.Count(order => order.Status == OrderStatus.Rejected),
                Cancelled = group.Count(order => order.Status == OrderStatus.Cancelled),
            })
            .SingleOrDefaultAsync(cancellationToken);

        var standingCounts = await standing
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Orders = group.Count(),
                Lines = group.Sum(order => order.Lines.Count),

                /*
                 * `Order.Agreement` is a computed property and cannot cross into SQL, so its rule is
                 * spelled out again here. That is a second implementation of a rule this codebase
                 * keeps in one place, and the mitigation is a test rather than a comment: the
                 * summary's count is asserted against the descriptors' own `Agreement` read back
                 * through `ForVisitAsync`, so the two cannot drift apart silently.
                 *
                 * `NotRepriced` is deliberately not counted. An outlet whose price list would not
                 * resolve is a different problem from a price the server disputes, and one number
                 * covering both would be actionable for neither.
                 */
                Disagreements = group.Count(order =>
                    order.ServerTotal != null
                    && order.ServerTaxTotal != null
                    && (order.ServerTotal != order.Total || order.ServerTaxTotal != order.TaxTotal)),
            })
            .SingleOrDefaultAsync(cancellationToken);

        // Per currency, because adding two of them is not arithmetic. Grouped in the database, so a
        // tenant with one currency pays for one row and a tenant with three pays for three.
        var value = await standing
            .GroupBy(order => order.CurrencyCode)
            .Select(group => new
            {
                CurrencyCode = group.Key,
                Net = group.Sum(order => order.Total),
                Tax = group.Sum(order => order.TaxTotal),
                Orders = group.Count(),
            })
            .OrderBy(row => row.CurrencyCode)
            .ToListAsync(cancellationToken);

        return new OrderSummary(
            Orders: standingCounts?.Orders ?? 0,
            Lines: standingCounts?.Lines ?? 0,
            Rejected: counts?.Rejected ?? 0,
            Cancelled: counts?.Cancelled ?? 0,
            PriceDisagreements: standingCounts?.Disagreements ?? 0,
            Value: [.. value.Select(row => new OrderValue(row.CurrencyCode, row.Net, row.Tax, row.Orders))]);
    }

    /// <summary>
    /// No orders, and therefore no value in any currency.
    /// </summary>
    /// <remarks>
    /// A summary rather than a null, for the reason <c>PerfectStoreSummary</c> gives: the empty state
    /// is one a dashboard has to render either way, and a nullable return would make every caller
    /// write it twice.
    /// </remarks>
    private static OrderSummary Empty => new(0, 0, 0, 0, 0, []);
}

