using System.Text.Json.Serialization;

namespace FieldKit.Modules.Order.Contracts;

/// <summary>
/// What an order is at any moment (<c>B4</c>).
/// </summary>
/// <remarks>
/// <b><see cref="Draft"/> is not a state this server ever stores</b>, and it is in the enum anyway.
/// B4 names the lifecycle as <c>Draft → Submitted → Accepted | Rejected → Cancelled</c>, and the first
/// of those lives on the device: an order is edited at a counter and only leaves when the rep submits
/// it. Leaving the name out would make this enum disagree with the spec and with the device's own
/// store for no gain; a rejected order re-opening (<c>BR-ORD-9</c>) needs the word too.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<OrderStatus>))]
public enum OrderStatus
{
    /// <summary>Editable, on the device. Never ingested — see the note above.</summary>
    Draft = 0,

    /// <summary>Sealed by the rep and pushed. The first state this server sees.</summary>
    Submitted = 1,

    /// <summary>Taken by the back office.</summary>
    Accepted = 2,

    /// <summary>Refused whole-order, and re-openable on the device (<c>BR-ORD-9</c>).</summary>
    Rejected = 3,

    /// <summary>Abandoned rather than corrected — the answer when nothing can be fixed.</summary>
    Cancelled = 4,
}

/// <summary>
/// One line of an order, as the device captured it.
/// </summary>
/// <param name="ProductId">What was ordered.</param>
/// <param name="Quantity">
/// How much, in the unit the line records. A decimal rather than an int because a UoM can be
/// weight — half a kilo of loose produce is an order line, not a rounding error.
/// </param>
/// <param name="UnitOfMeasure">
/// The unit as it was at capture, copied rather than referenced.
/// <para>
/// The same bargain a survey answer makes with its question text: a product's UoM can be corrected
/// in the back office, and a line that reached back for the current one would re-describe what the
/// shopkeeper actually asked for. "12" means nothing without the word beside it.
/// </para>
/// </param>
/// <param name="PackSize">Units per pack at capture, for the same reason. Null when sold loose.</param>
/// <param name="UnitPrice">
/// What the device charged, per <see cref="UnitOfMeasure"/>. <b>This is the record</b> — the server
/// recomputes beside it rather than over it (<c>BR-ORD-6</c>, W11 slice 0). Slice 2 is what adds the
/// recomputation.
/// </param>
/// <param name="LineTotal">
/// What the device made of quantity × price after any line promotion. Carried rather than derived
/// here for the same reason <see cref="UnitPrice"/> is: it is what the rep and the shopkeeper agreed,
/// and a server that recomputed it into the same field would erase the disagreement it is supposed to
/// flag.
/// </param>
public sealed record CapturedOrderLine(
    Guid ProductId,
    decimal Quantity,
    string UnitOfMeasure,
    int? PackSize,
    decimal UnitPrice,
    decimal LineTotal);

/// <summary>
/// An order a rep captured offline, as it arrives over <c>/sync/push</c>.
/// </summary>
/// <param name="OrderId">
/// Client-generated (v7). The device names the order so a replay is recognisable, and so the
/// resubmission of a rejected one can point at the order it corrects (<c>BR-ORD-9</c>).
/// </param>
/// <param name="VisitId">The visit it was taken during. Confirmed to be this rep's, and open.</param>
/// <param name="CurrencyCode">
/// ISO-4217, from the price list the device resolved. Every line is in it — <c>BR-ORD-7</c> —
/// and the aggregate refuses a set that is not.
/// </param>
/// <param name="Total">The device's order total, for the reason each <see cref="CapturedOrderLine.LineTotal"/> is carried.</param>
/// <param name="CapturedAtUtc">When the rep sealed it, from the device's clock.</param>
/// <param name="Lines">At least one. An order for nothing is not an order.</param>
public sealed record CapturedOrder(
    Guid OrderId,
    Guid VisitId,
    string CurrencyCode,
    decimal Total,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<CapturedOrderLine> Lines);

/// <summary>Why an ingest was refused. <see cref="None"/> means it was not.</summary>
/// <remarks>
/// Deliberately coarse, and every miss that involves *someone else's* data answers
/// <see cref="UnknownVisit"/> — the same call <c>AuditIngestService</c> makes. A device sends ids it
/// read out of its own store; distinguishing "no such visit" from "not yours" would turn this into a
/// way to discover whose visits exist.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<OrderIngestRefusal>))]
public enum OrderIngestRefusal
{
    None,

    /// <summary>No such visit, another tenant's, another rep's, or already sealed.</summary>
    UnknownVisit,

    /// <summary>The aggregate refused it — see <see cref="OrderIngestResult.Message"/>.</summary>
    Invalid,

    /// <summary>
    /// This order is already submitted and this is a <b>different</b> submission (<c>BR-ORD-4</c>).
    /// </summary>
    /// <remarks>
    /// Not a replay: a replay carries the mutation id that was already applied and succeeds. This is
    /// a second, distinct push naming an order that is already sealed — which is an <i>edit after
    /// submit</i> wearing a retry's clothes, and the one thing the lock exists to refuse.
    /// <para>
    /// A rejected order is the documented exception (<c>BR-ORD-9</c>) and re-opens for exactly this;
    /// nothing can reject one until slice 4, so today every second submission lands here.
    /// </para>
    /// </remarks>
    AlreadySubmitted,
}

/// <summary>The outcome of applying one pushed order.</summary>
public sealed record OrderIngestResult(OrderIngestRefusal Refusal, string? Message = null)
{
    public static OrderIngestResult Ok() => new(OrderIngestRefusal.None);
}

/// <summary>
/// Applying an order a device captured (<c>ORD-01</c>, <c>ORD-07</c>, <c>OFF-04</c>).
/// </summary>
/// <remarks>
/// <para>
/// Sync owns the transport and the idempotency ledger; this owns what a stored order must be true of.
/// Sync writing the <c>order</c> schema directly would put <c>BR-ORD-1</c> and <c>BR-ORD-7</c> in a
/// module that has no business knowing them, and AT-1 would refuse the reference anyway.
/// </para>
/// <para>
/// <b>There is no <c>SubmitAsync</c> and no create-a-draft call</b>, and that is the shape rather than
/// an omission. An order is drafted, edited and sealed on a device with no signal; what reaches this
/// server is a submission, whole and already sealed. An endpoint that let one be created online would
/// be a second door into a record whose whole conflict story rests on having exactly one writer
/// (<c>B7</c>).
/// </para>
/// </remarks>
public interface IOrderIngest
{
    /// <summary>
    /// Applies a captured order for <paramref name="userId"/>, or says why not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A repeat is a success, and <paramref name="mutationId"/> is what makes "repeat" mean
    /// something.</b> Order and Sync commit separately, so a mutation can land here and lose its
    /// ledger entry; the device retries. That retry must succeed — a device told "refused" forever
    /// about work that is done has no way back — and it must succeed even once the visit has been
    /// sealed, which is the case a later check gets wrong.
    /// </para>
    /// <para>
    /// Until W11 slice 3 the replay test was the <i>order</i> id, which silently accepted a second,
    /// different push of the same order as though it were a retry — <c>BR-ORD-4</c>'s lock, unenforced.
    /// The mutation id tells a retry from an edit, and it is the same id Sync's ledger keys on: this
    /// module records it so the two agree about what has already been applied.
    /// </para>
    /// </remarks>
    /// <param name="mutationId">The device-generated id of the push carrying this order.</param>
    Task<OrderIngestResult> IngestAsync(
        CapturedOrder captured,
        Guid mutationId,
        string userId,
        CancellationToken cancellationToken = default);
}
