using FieldKit.Modules.Order.Contracts;
using FieldKit.Modules.Outlets.Contracts;
using FieldKit.Modules.Products.Contracts;
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
/// <b>The assortment gate arrived in slice 4b</b>, one slice after the re-open path it needs.
/// <c>BR-ORD-1</c> says only assorted products can be ordered, and <c>ORD-12</c> says the answer when
/// they are not is a <i>rejection the rep can fix</i> — a very different outcome from a refusal that
/// leaves the push nowhere to go. Building the gate first would have been a way to strand exactly the
/// work <c>BR-ORD-9</c> exists to protect.
/// </para>
/// <para>
/// <b>It runs on a resubmission too.</b> A rep who fixes the flagged line and happens to add another
/// off-assortment one gets rejected again, which is the only answer that keeps <c>BR-ORD-1</c> true —
/// a correction is a submission, not an exemption.
/// </para>
/// </remarks>
internal sealed class OrderIngestService(
    OrderDbContext db,
    IVisitContext visits,
    IAssortmentService assortment,
    IPricingService pricing,
    IOutletCalendar calendar,
    IClock clock)
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

            // Gated again, because a correction is a submission and not an exemption: a rep who fixes
            // the flagged line and adds a different off-assortment one is rejected a second time.
            await GateOnAssortmentAsync(existing, existing.OutletId, cancellationToken);

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
         * An order belongs to a visit *being worked* — and W11 slice 8d found that the old reading of
         * that sentence refused every order a rep has ever taken offline.
         *
         * This used to test `visit.Sealed`. A pushed `CapturedVisit` is created **already checked
         * out** (`Visit.Ingest`: "sealed on arrival") and a device only enqueues one *at* check-out,
         * so an order captured at a counter has no window: `UnknownVisit` before the visit lands,
         * and this refusal after it. W11 slice 8c held the order back until the visit had been
         * accepted, which was right about the ordering and moved the failure rather than removing it
         * — the browser run for slice 9a is where that finally showed.
         *
         * What the rule means is *captured* after the seal. Both timestamps come from the same
         * device's clock, so the comparison holds even on a phone that is wrong about the time.
         *
         * The refusal stays `UnknownVisit` rather than gaining a value of its own: the device cannot
         * do anything different about it, and `OrderIngestRefusal` is a public contract whose reasons
         * a rep's screen already maps.
         */
        if (!visit.WasOpenAt(captured.CapturedAtUtc))
        {
            return new OrderIngestResult(
                OrderIngestRefusal.UnknownVisit,
                "That visit was checked out before this order was taken.");
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

        await GateOnAssortmentAsync(order!, visit.OutletId, cancellationToken);

        await RepriceAsync(order!, visit.OutletId, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return OrderIngestResult.Ok();
    }

    /// <summary>
    /// Prices the order again here and records what came out (<c>BR-ORD-6</c>, <c>ORD-08</c>) —
    /// W11 slice 14.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>As of the day it was captured, never today.</b> An order taken on Tuesday and pushed on
    /// Thursday must resolve against Tuesday's price list, or an ordinary mid-week price change would
    /// be reported as the device getting it wrong. <c>IPricingService</c> takes the date for exactly
    /// this reason and refuses to read a clock.
    /// </para>
    /// <para>
    /// <b>The shop's day, not Greenwich's</b> (<c>BR-PRD-6</c>) — W11½ R6b. <c>CapturedAtUtc</c> is an
    /// instant; a price list runs by calendar day, and an outlet in Bucharest changes day six hours
    /// before one in London. This took the UTC date until R6b, while the device took the *rep's
    /// phone's* — two different rules rather than one rule rounded twice, so an order captured before
    /// 03:00 local was flagged as disagreeing with a server that had asked a different question
    /// (regression F6). <see cref="IOutletCalendar"/> now answers for both.
    /// </para>
    /// <para>
    /// <b>A shop whose day cannot be resolved is not re-priced at all.</b> That is the same answer
    /// this method already gives an outlet the pricing service does not know, and for the same
    /// reason: an order is a commercial fact that has already happened, and re-pricing it against a
    /// day the server had to guess would manufacture a disagreement rather than find one.
    /// </para>
    /// <para>
    /// <b>It never refuses.</b> A pricing service that cannot answer — an outlet it does not know, a
    /// product nothing prices — leaves the annotation null, and <see cref="Order.Agreement"/> says
    /// *not re-priced* rather than *differs*. An order is a commercial fact that has already happened;
    /// the server failing to check it is the server's problem, not the rep's.
    /// </para>
    /// </remarks>
    private async Task RepriceAsync(Order order, Guid outletId, CancellationToken cancellationToken)
    {
        var days = await calendar.BusinessDaysAsync([outletId], order.CapturedAtUtc, cancellationToken);

        // Absent rather than defaulted: an unknown outlet, or a zone this runtime does not recognise.
        // Both mean the server cannot say which day's prices to compare against, and a guess would
        // report the rep as wrong about a day nobody established.
        if (!days.TryGetValue(outletId, out var on)) return;

        var priced = await pricing.PriceAsync(
            outletId,
            on,
            [.. order.Lines.Select(line => new LineToPrice(line.ProductId, line.Quantity))],
            cancellationToken);

        /*
         * Nothing recorded when the outlet is unknown, and nothing recorded when a line is unpriced.
         *
         * `Unpriced` means the tenant has no price for that product at that shop — a configuration
         * gap, not a disagreement. Totalling around it would compare the device's whole order against
         * the server's partial one and report a difference the size of the missing line, which reads
         * as "the rep charged too much" and is nothing of the kind.
         */
        if (priced is null || priced.Unpriced.Count > 0) return;

        order.RecordServerPrice(priced.Net.Amount, priced.Tax.Amount, clock.UtcNow);
    }

    /// <summary>
    /// Rejects the order if any line names a product this outlet may not be sold (<c>BR-ORD-1</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It rejects rather than refuses, and that distinction is the whole reason this waited for
    /// slice 4.</b> A refusal would answer the push with "no" and leave the rep's work nowhere — the
    /// order is on their device, sealed, and the only thing they could do is lose it. A rejection
    /// stores the order, names the offending line, and flows back down so they can fix it and
    /// resubmit (<c>ORD-12</c>, <c>F4</c>). Slice 1 deferred the gate for exactly this: a gate without
    /// somewhere to go is a way to strand work.
    /// </para>
    /// <para>
    /// <b>So the push still succeeds.</b> The device asked this server to record an order and it did;
    /// that the order was then refused by a rule is an outcome, not a transport failure, and the rep
    /// learns it from the pull feed like every other thing that happened to their work. Answering the
    /// push with a refusal would also make the mutation look unapplied, and the retry would arrive to
    /// find the order already there.
    /// </para>
    /// <para>
    /// <b>The first offending line, in line order.</b> An order can name several off-assortment
    /// products and the rejection carries one, because <c>F4</c>'s rejection is whole-order with
    /// <i>an</i> offending line. Taking the first by position rather than whichever the set happens
    /// to yield makes the same order reject the same way twice.
    /// </para>
    /// </remarks>
    private async Task GateOnAssortmentAsync(
        Order order, Guid outletId, CancellationToken cancellationToken)
    {
        var ordered = order.Lines.Select(line => line.ProductId).ToList();

        var allowed = await assortment.AssortedAsync(outletId, ordered, cancellationToken);

        var offending = order.Lines
            .OrderBy(line => line.Position)
            .FirstOrDefault(line => !allowed.Contains(line.ProductId));

        if (offending is null) return;

        order.Reject(OrderRejectionReason.OffAssortment, offending.ProductId, note: null, clock);
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






