using FieldKit.SharedKernel;

namespace FieldKit.Modules.Order.Contracts;

/// <summary>
/// What the back office made of an order the rep already sent (<c>ORD-12</c>, <c>BR-ORD-9</c>) —
/// W12 F5a.
/// </summary>
/// <remarks>
/// <para>
/// <b>The verdict, and deliberately not the order.</b> Every other entity on the pull feed is
/// reference data — a copy of something the server owns, which the device holds so it can work with
/// no signal. This is the first that travels the other way round: the device *authored* the order,
/// and what comes back is an annotation on work it already has.
/// </para>
/// <para>
/// That difference is why this carries a status and a rejection and nothing else. <c>BR-ORD-6</c> is
/// explicit that the device's numbers are the record and the server's arithmetic is an annotation
/// beside them — so a snapshot with <c>Total</c> on it would put the commercial fact the rep and the
/// shopkeeper agreed on the wire, pointed at a store that already holds a different copy of it, with
/// no type saying which one wins. A verdict cannot be applied wrongly because there is nothing on it
/// to overwrite.
/// </para>
/// <para>
/// <b>Scoped to the rep who captured it</b>, the way the journey feed is scoped to the rep the plan
/// names. An order belongs to the person who took it and never changes hands, so membership only
/// ever changes by the row being created — which stamps a row version above every cursor by
/// construction, and makes a baseline call the unreachable branch it is for journeys too.
/// </para>
/// </remarks>
/// <param name="Status">
/// The name, not the ordinal — <c>Submitted</c>, <c>Rejected</c>, <c>Cancelled</c>. The same rule
/// every snapshot on this feed follows, so a device binds to a word rather than to a position that
/// moves when a member is inserted.
/// </param>
/// <param name="Rejection">
/// Why it stands rejected and which line to look at, or null. Null for every status other than
/// <c>Rejected</c>, and the device reads the pair together: a status with no reason beside it is
/// what <c>OFF-09</c> and W11½ R5 were written to stop happening.
/// </param>
public sealed record OrderVerdictSnapshot(
    Guid OrderId,
    string Status,
    OrderRejectionDescriptor? Rejection,
    long RowVersion);

/// <summary>One page of verdicts: what to apply, what to drop, and how far the device now is.</summary>
public sealed record OrderVerdictPage(
    IReadOnlyList<OrderVerdictSnapshot> Upserts,
    IReadOnlyList<ReferenceTombstone> Tombstones,
    long Cursor);

/// <summary>
/// The verdicts a device should hold, as a delta (<c>OFF-03</c>, <c>BR-ORD-9</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> `BR-ORD-9` describes a rep correcting a flagged line and
/// resubmitting under a new mutation id. Every part of that was built in W11 slice 4a —
/// <c>POST /api/orders/{id}/rejection</c>, <c>Order.Resubmit</c>, the terminal-mutation rule — and
/// **no rep could begin, because none could learn their order had been rejected** (regression F5).
/// The device's own store recorded the gap in a comment: there is no <c>rejected</c> status there,
/// because a status the store could not keep true would be worse than one it does not have.
/// </para>
/// <para>
/// <b>No baseline method</b>, for the reason <c>IJourneyChangeFeed</c> gives at length: a cursor is
/// sufficient when membership changes only by creation, and a baseline would be an unreachable
/// branch dressed as symmetry.
/// </para>
/// <para>
/// <b>Every order, not only the rejected ones</b> — the row-version interceptor stamps on insert, so
/// an order appears here the moment it is stored, carrying <c>Submitted</c> and no rejection. That
/// is redundant for a device that has just pushed it, and it is deliberate: a feed that sent only
/// bad news could never send the good news that follows it. <c>BR-ORD-9</c>'s correction returns an
/// order to <c>Submitted</c>, and the device has to learn that its rejection is no longer current —
/// which is the same shape of message as the first one.
/// </para>
/// <para>
/// The cost is a first pull that pages through the rep's history. It is bounded by <c>limit</c>, it
/// is four fields per order, and it is the trade <c>journeys</c> already makes for a rep's whole
/// published round.
/// </para>
/// </remarks>
public interface IOrderVerdictFeed
{
    /// <summary>
    /// Verdicts on <paramref name="userId"/>'s orders whose row version is above
    /// <paramref name="cursor"/>.
    /// </summary>
    /// <param name="limit">
    /// A page size. A rep returning from leave with a fortnight of decisions behind them would
    /// otherwise ask for all of them at once, on the connection least able to carry it.
    /// </param>
    Task<OrderVerdictPage> GetChangesAsync(
        long cursor, string userId, int limit, CancellationToken cancellationToken = default);
}
