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

/// <summary>Reading what was ordered (<c>ORD-01</c>, reporting read-side).</summary>
public interface IOrderQuery
{
    /// <summary>The order taken during this visit, or null. At most one — see <c>Order</c>.</summary>
    Task<OrderDescriptor?> ForVisitAsync(Guid visitId, CancellationToken cancellationToken = default);

    /// <summary>This outlet's orders, newest first.</summary>
    Task<IReadOnlyList<OrderDescriptor>> ForOutletAsync(
        Guid outletId, CancellationToken cancellationToken = default);
}

