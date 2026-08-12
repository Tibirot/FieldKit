using System.Text.Json.Serialization;
using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using FieldKit.Modules.Order.Contracts;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Order;

/// <summary>One line of an order (<c>ORD-01</c>).</summary>
public sealed class OrderLine : ITenantOwned
{
    /// <summary>The column width for a unit of measure. Matches Product's.</summary>
    public const int MaximumUnitOfMeasureLength = 16;

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    public Guid ProductId { get; private set; }

    /// <summary>Where it sits on the order. Contiguous from 1, assigned rather than accepted.</summary>
    public int Position { get; private set; }

    /// <summary>How much, in <see cref="UnitOfMeasure"/>.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>
    /// The unit as it was when the rep captured the line, copied and never reached for.
    /// </summary>
    /// <remarks>
    /// A product's UoM can be corrected in the back office. A line that read the current one would
    /// re-describe what the shopkeeper asked for — "12" is meaningless without the word beside it,
    /// and "12 cases" becoming "12 bottles" is a tenfold error nobody typed. The same bargain
    /// <c>SurveyAnswerEntry</c> makes with its question text.
    /// </remarks>
    public string UnitOfMeasure { get; private set; } = null!;

    /// <summary>Units per pack at capture; null when the product is sold loose.</summary>
    public int? PackSize { get; private set; }

    /// <summary>What the device charged per unit. The record, not a suggestion (<c>BR-ORD-6</c>).</summary>
    public decimal UnitPrice { get; private set; }

    /// <summary>What the device made of the line after any promotion it applied.</summary>
    public decimal LineTotal { get; private set; }

    public TenantId TenantId { get; set; }

    private OrderLine() { } // EF

    internal static OrderLine Create(Guid orderId, int position, CapturedOrderLine captured) => new()
    {
        Id = Guid.CreateVersion7(),
        OrderId = orderId,
        Position = position,
        ProductId = captured.ProductId,
        Quantity = captured.Quantity,
        UnitOfMeasure = captured.UnitOfMeasure.Trim(),
        PackSize = captured.PackSize,
        UnitPrice = captured.UnitPrice,
        LineTotal = captured.LineTotal,
    };
}

/// <summary>
/// One attempt to submit an order (<c>BR-ORD-9</c>) — W11 slice 3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only, and the reason the lock is checkable.</b> W11 slice 0 settled that an order has
/// one identity and many submissions: without this collection, "the original submission's mutation id
/// is terminal" has nothing to be terminal <i>against</i> — the aggregate would hold only the latest
/// attempt, and a replay of a rejected id would be indistinguishable from a first arrival.
/// </para>
/// <para>
/// It is also what reconciles a re-opened order with <c>B7</c>. Device-owned data is append-only, and
/// moving an order <c>Rejected → Draft</c> looks like an exception to that until you notice the
/// <i>history</i> is what appends while the <i>aggregate</i> is what re-opens.
/// </para>
/// <para>
/// <b>The outcome arrived in slice 4a</b>, with the rejection that gives it a second value. Slice 0
/// named the column and slice 3 left it out on the grounds that a column with one possible value is a
/// schema version spent on nothing; this is the change that makes it mean something.
/// </para>
/// </remarks>
public sealed class OrderSubmission : ITenantOwned
{
    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    /// <summary>
    /// The device-generated id of the push that carried it.
    /// </summary>
    /// <remarks>
    /// Unique per tenant, in the schema: it is the same id Sync's ledger keys on, and two orders
    /// claiming one mutation would make "has this already been applied" ambiguous in the one place
    /// that must not be.
    /// </remarks>
    public Guid MutationId { get; private set; }

    /// <summary>When this server accepted it — its own clock, unlike the order's capture time.</summary>
    public DateTimeOffset SubmittedAtUtc { get; private set; }

    /// <summary>
    /// What became of this attempt. <see cref="SubmissionOutcome.Accepted"/> until something rejects it.
    /// </summary>
    /// <remarks>
    /// <b>It is set on the submission, not only on the order</b>, and that is what makes
    /// <c>BR-ORD-9</c>'s "the original submission's id is terminal" a checkable statement rather than a
    /// sentence. The order carries one status; the rejection belongs to the <i>attempt</i> that earned
    /// it, so a rep who fixes a line and resubmits leaves the rejected attempt legible behind them.
    /// </remarks>
    public SubmissionOutcome Outcome { get; private set; }

    /// <summary>Why it was rejected, or null while it stands accepted.</summary>
    public OrderRejectionReason? RejectionReason { get; private set; }

    /// <summary>
    /// The line the rejection is about, or null when the rejection is about the whole order.
    /// </summary>
    /// <remarks>
    /// <c>F4</c> asks for "a reason + the offending line", and the nullable is the honest reading of
    /// its own examples: an off-assortment SKU points at a line the rep can fix, while a closed outlet
    /// points at nothing they can edit. Both are whole-order rejections — the order is refused, not the
    /// line — and the product id says where to look, when there is anywhere.
    /// </remarks>
    public Guid? OffendingProductId { get; private set; }

    /// <summary>
    /// What the operator wrote, when the code alone does not say enough. Null otherwise.
    /// </summary>
    /// <remarks>
    /// It exists because <see cref="OrderRejectionReason.Other"/> would be useless without it — a rep
    /// told only "Other" has nothing to act on. It is deliberately <i>not</i> the rejection's meaning:
    /// the device branches on the code, and this is prose a human reads afterwards.
    /// </remarks>
    public string? Note { get; private set; }

    public TenantId TenantId { get; set; }

    /// <summary>The longest note this column takes. Beyond it the operator is writing an email.</summary>
    public const int MaximumNoteLength = 500;

    private OrderSubmission() { } // EF

    internal static OrderSubmission Of(Guid orderId, Guid mutationId, DateTimeOffset at) => new()
    {
        Id = Guid.CreateVersion7(),
        OrderId = orderId,
        MutationId = mutationId,
        SubmittedAtUtc = at,
        Outcome = SubmissionOutcome.Accepted,
    };

    internal void Reject(OrderRejectionReason reason, Guid? offendingProductId, string? note)
    {
        Outcome = SubmissionOutcome.Rejected;
        RejectionReason = reason;
        OffendingProductId = offendingProductId;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }
}

/// <summary>What became of one attempt to submit an order.</summary>
/// <remarks>
/// Two values and no <c>Pending</c>: an attempt that reached this server was applied or refused before
/// the response went back, so there is no moment at which a stored submission is undecided. A refusal
/// that never became a submission is not here at all — it is an <see cref="OrderIngestRefusal"/>, and
/// the difference is whether an order exists to have a history.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<SubmissionOutcome>))]
public enum SubmissionOutcome
{
    /// <summary>Taken. Every submission starts here.</summary>
    Accepted = 0,

    /// <summary>Refused whole-order, and terminal — see <see cref="OrderRejectionReason"/>.</summary>
    Rejected = 1,
}

/// <summary>
/// Why a <i>rejection</i> would not be recorded. <see cref="None"/> means it was.
/// </summary>
/// <remarks>
/// Separate from <see cref="OrderRefusal"/> because the two answer opposite questions. That one is
/// "this order cannot be stored"; this one is "this attempt to reject a stored order is itself
/// wrong" — an operator's mistake, not a rep's.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<OrderRejectionRefusal>))]
public enum OrderRejectionRefusal
{
    None,

    /// <summary>Only a <c>Submitted</c> order can be rejected — see <c>Order.Reject</c>.</summary>
    NotSubmitted,

    /// <summary>The named product is not on this order, so it cannot be the line at fault.</summary>
    UnknownLine,

    /// <summary>Longer than <see cref="OrderSubmission.MaximumNoteLength"/>.</summary>
    NoteTooLong,
}

/// <summary>Why an order was refused. <see cref="None"/> means it was not.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OrderRefusal>))]
public enum OrderRefusal
{
    None,

    /// <summary>No lines. An order for nothing is not an order.</summary>
    Empty,

    /// <summary>More lines than <see cref="Order.MaximumLines"/>.</summary>
    TooManyLines,

    /// <summary>A quantity of zero or less. A line nobody ordered is a line nobody should store.</summary>
    NonPositiveQuantity,

    /// <summary>The same product twice. Two answers to "how many", and no rule picking one.</summary>
    DuplicateProduct,

    /// <summary>A line with no unit, or one longer than the column.</summary>
    UnitOfMeasureMissing,

    /// <summary>Not three letters. <c>BR-ORD-7</c>'s currency is ISO-4217.</summary>
    CurrencyInvalid,
}

/// <summary>
/// An order a rep took at a counter (<c>ORD-01</c>, <c>ORD-07</c>, <c>B4</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>It arrives already sealed.</b> <c>Draft</c> is a state on the device (<c>B4</c>) — an order is
/// built and edited with no signal and pushed when the rep submits it, so the first status this
/// server ever writes is <see cref="OrderStatus.Submitted"/>. There is no create-a-draft path here
/// and there should not be: the conflict story rests on exactly one writer (<c>B7</c>), and a second
/// door would be a second writer.
/// </para>
/// <para>
/// <b>The device's money is the record.</b> Every price and total on this aggregate is what the
/// device computed at the counter. W11 slice 0 settled why: an order's prices are what a human being
/// agreed to buy at, so a server recomputation is stored *beside* them and never over them
/// (<c>BR-ORD-6</c>) — the deliberate opposite of <c>BR-AUD-8</c>, where the server's recomputed
/// score replaces the device's because a score is a measurement rather than a promise. Slice 2 adds
/// the recomputation; this slice stores what arrived.
/// </para>
/// <para>
/// <b>One order per visit</b>, which is a decision and not an obvious one. A rep at one counter on
/// one call places one order; two would be an accident of the device — a double-tap on submit, or a
/// draft resumed twice — far more often than an intention, and there is no rule downstream for
/// picking between them. A tenant that genuinely needs two orders per call has a requirement, and
/// this is where it would land. Enforced in the schema too, because "what did this shop order on
/// Tuesday" having two answers is not something a later reader can resolve.
/// </para>
/// <para>
/// <b>What this aggregate deliberately does not check:</b> whether each product is in the outlet's
/// assortment (<c>BR-ORD-1</c>). That question belongs to Products and its answer is a
/// <i>rejection</i> rather than a refusal — <c>ORD-12</c> has a rejected order re-open on the device
/// so the rep can fix the offending line, which is a very different outcome from a push the server
/// will not accept at all. It lands in slice 4 with the rejection path that gives it somewhere to go,
/// and with the Products contract that answers it. Refusing it here would strand the work
/// <c>BR-ORD-9</c> exists to protect.
/// </para>
/// </remarks>
public sealed class Order : AggregateRoot, ITenantOwned, IAuditable
{
    /// <summary>
    /// The most lines one order can carry.
    /// </summary>
    /// <remarks>
    /// A sanity bound rather than a business rule, and higher than a survey's fifty: a rep works a
    /// form in one standing go, but an order against a full assortment in a supermarket is a
    /// legitimately long document. Five hundred is well past any real call and still small enough
    /// that a runaway client is refused rather than stored.
    /// </remarks>
    public const int MaximumLines = 500;

    private readonly List<OrderLine> _lines = [];
    private readonly List<OrderSubmission> _submissions = [];

    public Guid Id { get; private set; }

    /// <summary>The visit it was taken during. One order per visit — see the class remarks.</summary>
    public Guid VisitId { get; private set; }

    /// <summary>
    /// Copied from the visit rather than reached through it.
    /// </summary>
    /// <remarks>
    /// "What has this outlet been ordering" is the read this module exists to serve, and reaching it
    /// through Visit would make every such query a cross-module join this architecture does not have
    /// (ADR-0005). The same copy <c>Audit.OutletId</c> makes, for the same reason.
    /// </remarks>
    public Guid OutletId { get; private set; }

    /// <summary>The rep, as the token named them. Never supplied by the request.</summary>
    public string UserId { get; private set; } = null!;

    public OrderStatus Status { get; private set; }

    /// <summary>ISO-4217, from the price list the device resolved (<c>BR-ORD-7</c>).</summary>
    public string CurrencyCode { get; private set; } = null!;

    /// <summary>The device's order total.</summary>
    public decimal Total { get; private set; }

    /// <summary>When the rep sealed it, from the device's clock.</summary>
    /// <remarks>
    /// The device's, not the server's, and the difference is the whole point of an offline product:
    /// an order captured in a basement on Tuesday and pushed from a car park on Thursday happened on
    /// Tuesday. <c>CreatedAtUtc</c> records when this server heard about it, and the two are both
    /// kept because the gap between them is a fact about the sync, not about the order.
    /// </remarks>
    public DateTimeOffset CapturedAtUtc { get; private set; }

    public IReadOnlyList<OrderLine> Lines => _lines;

    /// <summary>Every attempt to submit this order, oldest first.</summary>
    public IReadOnlyList<OrderSubmission> Submissions => _submissions;

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private Order() { } // EF

    /// <summary>
    /// Records a submitted order, or says why it will not be recorded.
    /// </summary>
    /// <remarks>
    /// Everything checked here is something an order cannot be stored *without* — a shape question,
    /// answerable from the payload alone. Whether the products may be ordered at this outlet is a
    /// different kind of question with a different kind of answer; see the class remarks.
    /// </remarks>
    public static (Order? Order, OrderRefusal Refusal) Record(
        CapturedOrder captured, Guid outletId, string userId, Guid mutationId, IClock clock)
    {
        if (Check(captured) is var refusal && refusal is not OrderRefusal.None)
        {
            return (null, refusal);
        }

        var order = new Order
        {
            Id = captured.OrderId,
            VisitId = captured.VisitId,
            OutletId = outletId,
            UserId = userId,
            // The only status this server writes on the way in. Accepted/Rejected are the back
            // office's to set, and Draft belongs to the device.
            Status = OrderStatus.Submitted,
            CurrencyCode = captured.CurrencyCode.Trim().ToUpperInvariant(),
            Total = captured.Total,
            CapturedAtUtc = captured.CapturedAtUtc,
        };

        var position = 1;

        foreach (var line in captured.Lines)
        {
            order._lines.Add(OrderLine.Create(order.Id, position++, line));
        }

        order._submissions.Add(OrderSubmission.Of(order.Id, mutationId, clock.UtcNow));

        /*
         * Raised here rather than by the ingest service, and that is the seal.
         *
         * `BR-ORD-4` locks an order the moment it is submitted, and on this server there is no
         * earlier moment: the aggregate has exactly one factory and it produces a `Submitted` order
         * with a submission already recorded. There is no window in which a stored order is
         * unannounced, and no path that could construct one without announcing it.
         *
         * The event goes to the outbox in the same transaction as the rows (ADR-0006), so a
         * subscriber cannot learn of an order that failed to store, and a stored order cannot go
         * unlearned-of.
         */
        order.Raise(new OrderSubmitted(
            Guid.CreateVersion7(),
            clock.UtcNow,
            order.Id,
            order.VisitId,
            order.OutletId,
            order.UserId,
            order.CurrencyCode,
            order.Total,
            order._lines.Count,
            order.CapturedAtUtc));

        return (order, OrderRefusal.None);
    }

    /// <summary>
    /// Refuses this order whole, naming why and — when there is one — which line (<c>ORD-12</c>,
    /// <c>F4</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Whole-order, never per line.</b> <c>F4</c> is explicit, and the reason is commercial rather
    /// than technical: an order is a thing a shopkeeper agreed to as a whole, and quietly dropping one
    /// line of it would deliver something nobody ordered at a total nobody was quoted. The offending
    /// product says where to look; the refusal is still of the order.
    /// </para>
    /// <para>
    /// <b>The latest submission is what carries it</b>, and it becomes terminal by doing so: that
    /// mutation id can never move this order again, which is exactly what <c>BR-ORD-9</c> needs to
    /// keep the push idempotent while the rep tries again under a new one.
    /// </para>
    /// <para>
    /// <b>No <c>OrderRejected</c> integration event.</b> <c>OrderSubmitted</c> exists because the
    /// [order spec §8](../docs/product/23-order-capture.md) names it as this module's published fact;
    /// nothing names a rejection, and nothing subscribes. The rep learns through the pull feed — a
    /// rejected order flows back down to their device (<c>F4</c>) — which is a different mechanism
    /// from the reporting boundary and the one <c>ORD-12</c> actually asks for.
    /// </para>
    /// </remarks>
    public OrderRejectionRefusal Reject(
        OrderRejectionReason reason, Guid? offendingProductId, string? note, IClock clock)
    {
        /*
         * Only a submitted order can be rejected, and each of the other states is a different mistake.
         *
         * `Rejected` twice is the interesting one: it is refused rather than treated as idempotent,
         * because the second rejection would carry its own reason and silently overwrite the one the
         * rep is already acting on — a rep looking at "off assortment" and a server holding "outlet
         * closed" is worse than an error nobody sees.
         */
        if (Status is not OrderStatus.Submitted) return OrderRejectionRefusal.NotSubmitted;

        if (note is { Length: > OrderSubmission.MaximumNoteLength })
        {
            return OrderRejectionRefusal.NoteTooLong;
        }

        // A line that is not on this order cannot be the one at fault. Storing it would send the rep
        // hunting for a product they never ordered.
        if (offendingProductId is { } productId && _lines.All(line => line.ProductId != productId))
        {
            return OrderRejectionRefusal.UnknownLine;
        }

        Latest().Reject(reason, offendingProductId, note);

        Status = OrderStatus.Rejected;
        ModifiedAtUtc = clock.UtcNow;

        return OrderRejectionRefusal.None;
    }

    /// <summary>
    /// Takes a corrected resubmission of a rejected order — <c>BR-ORD-9</c>'s exception to
    /// <c>BR-ORD-4</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The order keeps its identity and gains a submission.</b> <c>F4</c> settles that a rejected
    /// order re-opens rather than being replaced, so "how many orders did this outlet place" counts
    /// intent rather than attempts — and the history is what appends, which is how a re-open stays
    /// honest with <c>B7</c>'s append-only rule for device-owned data.
    /// </para>
    /// <para>
    /// <b>The lines are replaced, because that is what the rep fixed.</b> The rejected line is the
    /// whole reason there is a second submission; keeping the old ones would store an order the rep
    /// has already corrected, and merging them would invent a third order nobody entered.
    /// </para>
    /// </remarks>
    public OrderRefusal Resubmit(CapturedOrder captured, Guid mutationId, IClock clock)
    {
        if (Check(captured) is var refusal && refusal is not OrderRefusal.None) return refusal;

        _lines.Clear();

        var position = 1;

        foreach (var line in captured.Lines)
        {
            _lines.Add(OrderLine.Create(Id, position++, line));
        }

        CurrencyCode = captured.CurrencyCode.Trim().ToUpperInvariant();
        Total = captured.Total;
        CapturedAtUtc = captured.CapturedAtUtc;

        Status = OrderStatus.Submitted;
        ModifiedAtUtc = clock.UtcNow;

        _submissions.Add(OrderSubmission.Of(Id, mutationId, clock.UtcNow));

        // Announced again, and it is not a duplicate: a resubmitted order is a different set of lines
        // at a different total, and a reader that saw only the first would hold what the rep corrected.
        Raise(new OrderSubmitted(
            Guid.CreateVersion7(),
            clock.UtcNow,
            Id,
            VisitId,
            OutletId,
            UserId,
            CurrencyCode,
            Total,
            _lines.Count,
            CapturedAtUtc));

        return OrderRefusal.None;
    }

    /// <summary>The most recent attempt. An order always has one — <see cref="Record"/> makes it.</summary>
    public OrderSubmission Latest() =>
        _submissions.OrderByDescending(submission => submission.SubmittedAtUtc).First();

    private static OrderRefusal Check(CapturedOrder captured)
    {
        if (captured.Lines.Count == 0) return OrderRefusal.Empty;
        if (captured.Lines.Count > MaximumLines) return OrderRefusal.TooManyLines;

        // Three letters, and the case is normalised on the way in rather than refused — a device
        // sending "eur" means EUR, and refusing it would strand an order over capitalisation.
        if (captured.CurrencyCode?.Trim().Length != 3
            || !captured.CurrencyCode.Trim().All(char.IsAsciiLetter))
        {
            return OrderRefusal.CurrencyInvalid;
        }

        /*
         * BR-ORD-7 is a *currency* rule and there is nothing here to compare against.
         *
         * Worth saying because the rule reads like a per-line check: "no cross-currency lines in one
         * order". A line carries no currency — it carries an amount, and the order says what those
         * amounts are in. Modelling it that way is what makes a mixed-currency order unexpressible
         * rather than refused, which is the stronger form of the same guarantee.
         */

        if (captured.Lines.Any(line => line.Quantity <= 0)) return OrderRefusal.NonPositiveQuantity;

        if (captured.Lines.Any(line =>
                string.IsNullOrWhiteSpace(line.UnitOfMeasure)
                || line.UnitOfMeasure.Trim().Length > OrderLine.MaximumUnitOfMeasureLength))
        {
            return OrderRefusal.UnitOfMeasureMissing;
        }

        // One line per product. Two lines for the same SKU are two answers to "how many", and
        // silently summing them would invent a quantity nobody typed.
        if (captured.Lines.Select(line => line.ProductId).Distinct().Count() != captured.Lines.Count)
        {
            return OrderRefusal.DuplicateProduct;
        }

        return OrderRefusal.None;
    }

    /// <summary>This order as another module sees it.</summary>
    public OrderDescriptor Describe() => new(
        Id,
        VisitId,
        OutletId,
        UserId,
        Status,
        CurrencyCode,
        Total,
        CapturedAtUtc,
        [.. _lines
            .OrderBy(line => line.Position)
            .Select(line => new OrderLineDescriptor(
                line.ProductId,
                line.Quantity,
                line.UnitOfMeasure,
                line.PackSize,
                line.UnitPrice,
                line.LineTotal))],
        Latest() is { Outcome: SubmissionOutcome.Rejected } rejected
            ? new OrderRejectionDescriptor(
                rejected.RejectionReason!.Value, rejected.OffendingProductId, rejected.Note)
            : null);
}

/// <summary>
/// An order a rep sealed and pushed (<c>ORD-07</c>) — the boundary event W11 slice 3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Value and a line count, not the lines.</b> The [order spec §8](../docs/product/23-order-capture.md)
/// asks for "value, lines summary", and a subscriber wanting the lines has <c>IOrderQuery</c> — an
/// event carrying them would be a second copy of the order, free to be read after the order itself
/// has moved on. What this says is the fact that happened: this shop ordered this much, then.
/// </para>
/// <para>
/// <b><see cref="CapturedAtUtc"/> and <see cref="OccurredOn"/> are both here and mean different
/// things.</b> The first is when the rep sealed it at a counter; the second is when this server heard.
/// An order taken in a basement on Tuesday and pushed from a car park on Thursday has a two-day gap
/// between them, and a reporting read that used the wrong one would file the sale in the wrong week.
/// </para>
/// </remarks>
public sealed record OrderSubmitted(
    Guid Id,
    DateTimeOffset OccurredOn,
    Guid OrderId,
    Guid VisitId,
    Guid OutletId,
    string UserId,
    string CurrencyCode,
    decimal Total,
    int LineCount,
    DateTimeOffset CapturedAtUtc) : IIntegrationEvent;


