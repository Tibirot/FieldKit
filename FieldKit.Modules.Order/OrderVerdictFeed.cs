using FieldKit.Modules.Order.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Order;

/// <summary>What the back office made of a rep's orders, as a delta (<c>BR-ORD-9</c>) — W12 F5a.</summary>
internal sealed class OrderVerdictFeed(OrderDbContext db) : IOrderVerdictFeed
{
    public async Task<OrderVerdictPage> GetChangesAsync(
        long cursor, string userId, int limit, CancellationToken cancellationToken = default)
    {
        /*
         * `UserId` is a column here, where the journey feed needed a join.
         *
         * A planned call belongs to a rep because the *plan* names them, so the feed filters through
         * the aggregate root. An order names its own rep — copied from the visit at capture, on
         * purpose, so the record of who sold survives a visit being re-read — which makes this a
         * single-table scan and not a denormalisation anybody has to keep in step.
         */
        var page = await db.Orders
            .AsNoTracking()
            .Where(order => order.UserId == userId && order.RowVersion > cursor)
            .OrderBy(order => order.RowVersion)
            .Take(limit)
            .Include(order => order.Submissions)
            .ToListAsync(cancellationToken);

        var upserts = page.Select(Describe).ToList();

        // The highest version *in this page*, never the table's maximum — a truncated page must
        // resume rather than skip everything between the last row sent and the high-water mark.
        var highest = upserts.Count > 0 ? Math.Max(cursor, upserts[^1].RowVersion) : cursor;

        return new OrderVerdictPage(upserts, Tombstones, highest);
    }

    /// <summary>The verdict, from the latest attempt — never the history.</summary>
    /// <remarks>
    /// The same rule <c>OrderRejectionDescriptor</c> states: a rep needs to know what to fix
    /// <i>now</i>, and which of three earlier attempts said what is a question the submissions
    /// answer and no device has asked.
    /// </remarks>
    private static OrderVerdictSnapshot Describe(Order order) => new(
        order.Id,
        order.Status.ToString(),
        order.Describe().Rejection,
        order.RowVersion);

    /*
     * Always empty, and a statement about the domain rather than a gap.
     *
     * Nothing deletes an order. `BR-ORD-4` denies it to everybody — an order is a commercial fact
     * and the back office may refuse one, never erase it — so the strongest thing that can happen to
     * a submitted order is `Rejected`, which is an update carrying a new row version and travels as
     * an ordinary upsert. The interceptor would write a tombstone if anything deleted; nothing does.
     *
     * Reading the table anyway would be worse than useless: a tombstone records that a row is gone,
     * so there is no longer an order to ask whose it was, and sending every deleted order in the
     * tenant would tell one rep how much churn there is on everybody else's. That is the same
     * argument `JourneyChangeFeed` makes, and it lands harder here because an order carries money.
     */
    private static readonly IReadOnlyList<SharedKernel.ReferenceTombstone> Tombstones = [];
}
