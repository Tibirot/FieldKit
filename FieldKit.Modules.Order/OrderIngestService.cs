using FieldKit.Modules.Order.Contracts;
using FieldKit.SharedKernel;
using FieldKit.Modules.Visit.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Order;

/// <summary>
/// Applies an order a device captured offline (<c>ORD-07</c>, <c>OFF-04</c>) — W11 slice 1.
/// </summary>
/// <remarks>
/// <para>
/// <b>It goes through the aggregate, not around it.</b> Everything a stored order must be true of
/// lives in <see cref="Order.Record"/>; what this adds is the three things the aggregate cannot know
/// — that the visit exists, that it is this rep's and still open, and that no order is already filed
/// against it.
/// </para>
/// <para>
/// <b>The assortment gate is not here yet</b>, and that is scheduling rather than oversight.
/// <c>BR-ORD-1</c> says only assorted products can be ordered, and <c>ORD-12</c> says the answer when
/// they are not is a <i>rejection the rep can fix</i> — a very different outcome from a refusal that
/// leaves the push nowhere to go. Both arrive in slice 4, together, because a gate without the
/// re-open path would strand exactly the work <c>BR-ORD-9</c> exists to protect.
/// </para>
/// </remarks>
internal sealed class OrderIngestService(OrderDbContext db, IVisitContext visits, IClock clock)
    : IOrderIngest
{
    public async Task<OrderIngestResult> IngestAsync(
        CapturedOrder captured,
        Guid mutationId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        /*
         * The replay check comes first, before the visit is even looked up.
         *
         * Order and Sync commit separately, so a mutation can land here and lose its ledger entry;
         * the device retries. That retry has to succeed — a device told "refused" forever about work
         * that is done has no way back — and it has to succeed even once the visit it belongs to has
         * been sealed, which is the case the check below would get wrong. The same window
         * `IAuditIngest` and `IVisitIngest.AlreadyExists` close.
         *
         * **It tests the mutation id, not the order id, and that is W11 slice 3's whole point.**
         * Keying on the order alone made every second push of an order a "replay" — including one
         * carrying different lines, which is an edit after submit and exactly what `BR-ORD-4`
         * forbids. The lock was written down in slice 1 and enforced by nothing.
         */
        var existing = await db.Orders
            .Include(order => order.Lines)
            .Include(order => order.Submissions)
            .SingleOrDefaultAsync(order => order.Id == captured.OrderId, cancellationToken);

        if (existing is not null)
        {
            /*
             * A replay: this exact push already ran. Its outcome stands, whatever that outcome was.
             *
             * Including a rejection — `BR-ORD-9` calls the rejected submission's id **terminal**, and
             * this is where that word is enforced. Re-applying it would either re-reject an order the
             * rep has since corrected or, worse, take a superseded set of lines as current.
             */
            if (existing.Submissions.Any(submission => submission.MutationId == mutationId))
            {
                return OrderIngestResult.Ok();
            }

            /*
             * A different push naming an order that already exists. Rejected is the one state where
             * that is legitimate (`BR-ORD-9`) — the rep fixed the flagged line and is trying again
             * under a new mutation id, which is the whole mechanism that keeps their work from being
             * stranded. Every other state is `BR-ORD-4`'s lock doing its job.
             */
            if (existing.Status is not OrderStatus.Rejected)
            {
                return new OrderIngestResult(
                    OrderIngestRefusal.AlreadySubmitted,
                    "That order was already submitted. A submitted order cannot be changed.");
            }

            /*
             * The visit is deliberately *not* re-checked here, and it is the same call the replay
             * above makes. A rejection can take days to come back and be corrected, by which time the
             * rep has long since checked out — refusing the fix because the visit is sealed would
             * strand exactly the work `BR-ORD-9` exists to protect.
             */
            if (existing.Resubmit(captured, mutationId, clock) is var invalid
                && invalid is not OrderRefusal.None)
            {
                return new OrderIngestResult(OrderIngestRefusal.Invalid, Explain(invalid));
            }

            // The replaced lines and the new submission need no announcing: they hang off a tracked
            // parent with client-set keys, which is precisely what `ClientGeneratedKeyConvention`
            // ended in slice 0b-i. Dropping the old lines is orphan removal, which EF always handled.
            await db.SaveChangesAsync(cancellationToken);

            return OrderIngestResult.Ok();
        }

        if (await visits.FindAsync(captured.VisitId, cancellationToken) is not { } visit
            || visit.UserId != userId)
        {
            return new OrderIngestResult(
                OrderIngestRefusal.UnknownVisit,
                "That visit is not one of yours, or no longer exists.");
        }

        /*
         * An order belongs to a visit *being worked*.
         *
         * A sealed visit refuses a new order for the reason a sealed visit refuses a new audit: the
         * visit was filed as done, and attaching a fresh order to it would change a record already
         * counted. The replay above is deliberately ahead of this, so a retry of an order that
         * arrived before check-out still succeeds after it.
         */
        if (visit.Sealed)
        {
            return new OrderIngestResult(
                OrderIngestRefusal.UnknownVisit,
                "That visit is not one of yours, or no longer exists.");
        }

        var (order, refusal) = Order.Record(captured, visit.OutletId, userId, mutationId, clock);

        if (refusal is not OrderRefusal.None)
        {
            return new OrderIngestResult(OrderIngestRefusal.Invalid, Explain(refusal));
        }

        // The lines and the submission ride along: `Add` on a *new* root paints the whole graph
        // `Added` whatever the keys hold. This was never the defect the other five sites had — those
        // hung new children on an already-tracked parent — and the comment that used to sit here
        // claiming otherwise was a copy of a workaround into a place that never needed one.
        db.Orders.Add(order!);

        await db.SaveChangesAsync(cancellationToken);

        return OrderIngestResult.Ok();
    }

    private static string Explain(OrderRefusal refusal) => refusal switch
    {
        OrderRefusal.Empty => "An order needs at least one line.",
        OrderRefusal.TooManyLines => $"An order carries at most {Order.MaximumLines} lines.",
        OrderRefusal.NonPositiveQuantity => "A line needs a quantity greater than zero.",
        OrderRefusal.DuplicateProduct => "The same product appears on more than one line.",
        OrderRefusal.UnitOfMeasureMissing => "A line needs the unit its quantity is counted in.",
        OrderRefusal.CurrencyInvalid => "A currency is a three-letter ISO-4217 code, e.g. EUR.",
        _ => "That order was refused.",
    };
}


