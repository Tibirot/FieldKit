using System.Text.Json.Serialization;

namespace FieldKit.Modules.Order.Contracts;

/// <summary>
/// Why a submitted order was rejected (<c>ORD-12</c>, <c>F4</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A closed set rather than free text</b>, because the device has to act on it: <c>F4</c> splits
/// rejections into the kind a rep fixes and resubmits and the kind they can only cancel, and a
/// sentence typed by an operator cannot be branched on. The rep-facing wording is the client's, from
/// the code, the way every ADR-0012 refusal already works.
/// </para>
/// <para>
/// <b>Which of the two a reason is, is not modelled yet</b>, and naming that is cheaper than
/// discovering it. Cancellation (<c>Rejected → Cancelled</c>) is a device-owned mutation that arrives
/// over <c>/sync/push</c>, so the flag that would drive it has no reader until the push arm exists.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<OrderRejectionReason>))]
public enum OrderRejectionReason
{
    /// <summary>
    /// A line names a product this outlet may not order (<c>BR-ORD-1</c>).
    /// </summary>
    /// <remarks>
    /// <b>The one reason this server raises on its own</b>, since W11 slice 4b: an order naming a
    /// product the outlet does not stock is stored and rejected on arrival rather than refused, so the
    /// rep gets it back with the line flagged instead of losing it. An operator can still select it by
    /// hand — a delisting the catalogue has not caught up with is a real thing to say.
    /// </remarks>
    OffAssortment = 0,

    /// <summary>The outlet shut between the rep capturing the order and the push arriving.</summary>
    OutletClosed = 1,

    /// <summary>
    /// The outlet may not be sold to right now — credit hold or similar (<c>ORD-15</c>).
    /// </summary>
    /// <remarks>
    /// <c>ORD-15</c> wants this checked automatically and <b>cannot be</b>: it needs an order-hold flag
    /// on the outlet, which does not exist and is Outlets' to add. An operator who knows the shop is on
    /// hold can say so, which is the whole reason this ships as an API before it ships as a rule.
    /// </remarks>
    OutletOnHold = 2,

    /// <summary>Something the codes above do not cover. The detail is in the rejection's note.</summary>
    Other = 3,
}

/// <summary>
/// Why an order stands rejected, and where to look (<c>ORD-12</c>, <c>F4</c>).
/// </summary>
/// <remarks>
/// <para>
/// The <i>current</i> rejection, taken from the latest attempt — not the history. A rep needs to know
/// what to fix now; which of three earlier attempts said what is a question the submissions answer,
/// and no caller has asked it.
/// </para>
/// <para>
/// <b>No timestamp of its own.</b> The order is <c>IAuditable</c> and a rejection is the only thing
/// that modifies one, so <c>ModifiedAtUtc</c> already says when — a second column would be the same
/// fact stored twice and free to disagree.
/// </para>
/// </remarks>
public sealed record OrderRejectionDescriptor(
    OrderRejectionReason Reason,
    Guid? OffendingProductId,
    string? Note);

/// <summary>One line of a stored order, as another module reads it.</summary>
public sealed record OrderLineDescriptor(
    Guid ProductId,
    decimal Quantity,
    string UnitOfMeasure,
    int? PackSize,
    decimal UnitPrice,
    decimal LineTotal);

/// <summary>
/// A stored order, as another module reads it.
/// </summary>
/// <remarks>
/// <see cref="Status"/> is the enum, and this record says nothing about how it is spelled. That was a
/// refusal to add the fourth per-property band-aid; since W11 slice 0b it is simply the rule —
/// <c>OrderStatus</c> declares its own wire form, and no record that mentions it has to.
/// <para>
/// This is still a module contract rather than a wire type: the endpoint that serialises an order
/// renders the status into a <c>string</c> field of its own, the way <c>SurveyQuestionResponse</c>
/// does.
/// </para>
/// </remarks>
/// <param name="Total">The device's net, as the rep and the shopkeeper settled it.</param>
/// <param name="TaxTotal">
/// The device's tax, beside <paramref name="Total"/>'s net.
/// </param>
/// <param name="ServerTotal">
/// What this server made the net when it re-priced the order, or null if it did not (<c>BR-ORD-2</c>).
/// </param>
/// <param name="ServerTaxTotal">The server's tax, beside <paramref name="ServerTotal"/>'s net.</param>
/// <param name="Agreement">
/// Whether the two sides agree — the rule, computed once, where the data is.
/// <para>
/// Derivable from the four numbers above, and sent anyway. A consumer re-deriving it is a second
/// implementation of a rule this codebase already has one place for, and comparing two decimals is
/// exactly the sort of thing two implementations get subtly differently.
/// </para>
/// </param>
public sealed record OrderDescriptor(
    Guid Id,
    Guid VisitId,
    Guid OutletId,
    string UserId,
    OrderStatus Status,
    string CurrencyCode,
    decimal Total,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<OrderLineDescriptor> Lines,
    OrderRejectionDescriptor? Rejection = null,
    decimal TaxTotal = 0m,
    decimal? ServerTotal = null,
    decimal? ServerTaxTotal = null,
    PriceAgreement Agreement = PriceAgreement.NotRepriced);

/// <summary>
/// What was ordered in one currency (<c>ORD-09</c>) — W12 slice 2c.
/// </summary>
/// <remarks>
/// <b>Money is reported per currency because adding two of them is not arithmetic.</b> An order
/// carries the currency it was taken in, and a tenant selling across a border has both; a single
/// <c>Total</c> summed over them would be a number with no unit. This is the same posture
/// <c>PerfectStoreSummary.WeightSetVersions</c> takes towards weight sets — except that mixing
/// rulers gives a misleading figure, and mixing currencies gives a meaningless one, so this is a
/// split rather than a warning.
/// </remarks>
/// <param name="CurrencyCode">ISO 4217, as the order stored it.</param>
/// <param name="Net">The device's net, summed — what the reps and the shopkeepers settled.</param>
/// <param name="Tax">The device's tax, summed, beside the net rather than inside it.</param>
/// <param name="Orders">How many orders in this currency stand behind the figures.</param>
public sealed record OrderValue(string CurrencyCode, decimal Net, decimal Tax, int Orders)
{
    /// <summary>Net plus tax — what the shopkeeper owes.</summary>
    public decimal Gross => Net + Tax;
}

/// <summary>
/// Order capture across a set of shops and a window (<c>ORD-09</c>) — W12 slice 2c.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only orders that stand are counted as value.</b> <c>Submitted</c> and <c>Accepted</c> are
/// orders somebody expects to be delivered; <c>Rejected</c> and <c>Cancelled</c> are not, and adding
/// them to a territory's number would report revenue the back office has already refused. They are
/// counted separately rather than dropped, because a territory writing a tenth of its orders off is
/// a fact about that territory — and a rejection rate is what <c>BR-ORD-9</c>'s whole re-open path
/// exists to move.
/// </para>
/// <para>
/// <b>The value is the <i>device's</i> total, and that is <c>BR-ORD-2</c> rather than an oversight.</b>
/// The server re-prices and <i>flags</i>, never applies: the order is what the rep and the shopkeeper
/// settled on at the counter. Reporting the server's total would report a number nobody agreed to.
/// <see cref="PriceDisagreements"/> is how the disagreement stays visible — a territory where a
/// third of orders disagree has a pricing-data problem, and a KPI that quietly averaged over it
/// would hide the one thing worth acting on.
/// </para>
/// <para>
/// <b>Promotion usage is not here, and it is not an omission I can fix in this module.</b> The
/// [KPI table](../../docs/product/00-product-overview.md#reporting--kpis-cross-cutting-read-side)
/// lists it under Order, but an <c>OrderLine</c> records what it cost and <b>not which promotion made
/// it cost that</b>: the device applies one and sends the net. It could be inferred from
/// <c>quantity × unit price</c> exceeding the line total — except that both sides are rounded
/// independently, so a line rounded down would report a discount nobody gave. A KPI with a tolerance
/// in it is a KPI nobody can act on, so the honest answer is that the schema has to carry the
/// promotion before the report can name it.
/// </para>
/// </remarks>
/// <param name="Orders">Orders that stand — submitted or accepted.</param>
/// <param name="Lines">Their lines, summed. The "lines per order" the KPI table asks for.</param>
/// <param name="Rejected">Refused whole-order by the back office (<c>BR-ORD-9</c>).</param>
/// <param name="Cancelled">
/// Abandoned rather than corrected.
/// <para>
/// <b>Neither <c>Accepted</c> nor <c>Cancelled</c> is reachable today</b>, and this is the first
/// thing to know about these two numbers. The only transition the server has is rejection
/// (<c>POST /api/orders/{id}/rejection</c>); nothing sets either of the other two, so a real tenant
/// sees submitted and rejected and nothing else, and these counts are consequently <b>not covered by
/// a test</b> — there is no way to produce one to count. They are classified rather than ignored
/// because the alternative is worse: an accepted order arriving after W12 slice 6 builds the back
/// office would otherwise fall out of both the value and the counts, and a KPI that silently drops a
/// state is harder to notice than one that reports zero.
/// </para>
/// </param>
/// <param name="PriceDisagreements">
/// Standing orders the server re-priced and disagreed with (<c>PriceAgreement.Differs</c>). Orders it
/// could not re-price at all are not counted here — an unresolved price list is a different problem
/// from a wrong price, and folding the two together would make neither actionable.
/// </param>
/// <param name="Value">One entry per currency present, ascending by code.</param>
public sealed record OrderSummary(
    int Orders,
    int Lines,
    int Rejected,
    int Cancelled,
    int PriceDisagreements,
    IReadOnlyList<OrderValue> Value)
{
    /// <summary>
    /// Lines per standing order, or <c>null</c> when none stands.
    /// </summary>
    /// <remarks>
    /// Null rather than zero, for the reason <c>VisitOutcomeCounts.StrikeRate</c> gives: "no orders
    /// yet" and "orders with nothing on them" are different weeks, and an order with no lines is a
    /// state <c>Order</c> refuses to store in the first place.
    /// </remarks>
    public decimal? LinesPerOrder => Orders == 0
        ? null
        : Math.Round((decimal)Lines / Orders, 2, MidpointRounding.AwayFromZero);
}

/// <summary>Reading what was ordered (<c>ORD-01</c>, reporting read-side).</summary>
public interface IOrderQuery
{
    /// <summary>The order taken during this visit, or null. At most one — see <c>Order</c>.</summary>
    Task<OrderDescriptor?> ForVisitAsync(Guid visitId, CancellationToken cancellationToken = default);

    /// <summary>This outlet's orders, newest first.</summary>
    Task<IReadOnlyList<OrderDescriptor>> ForOutletAsync(
        Guid outletId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Order capture across these shops over a closed date range — the KPI, not the orders.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Dated by capture, in UTC, both ends inclusive</b> — the choice its three siblings make. An
    /// order taken at a counter with no signal is a record of the day it was taken, not of the day
    /// the phone found a network, and <c>Order.CapturedAtUtc</c> is the column that says so.
    /// </para>
    /// <para>
    /// An empty <paramref name="outletIds"/> answers an empty summary rather than the tenant's.
    /// </para>
    /// </remarks>
    Task<OrderSummary> SummariseAsync(
        IReadOnlyCollection<Guid> outletIds,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);
}

