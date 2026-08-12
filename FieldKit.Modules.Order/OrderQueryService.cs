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
}

