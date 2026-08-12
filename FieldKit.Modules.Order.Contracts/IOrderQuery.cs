namespace FieldKit.Modules.Order.Contracts;

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
public sealed record OrderDescriptor(
    Guid Id,
    Guid VisitId,
    Guid OutletId,
    string UserId,
    OrderStatus Status,
    string CurrencyCode,
    decimal Total,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<OrderLineDescriptor> Lines);

/// <summary>Reading what was ordered (<c>ORD-01</c>, reporting read-side).</summary>
public interface IOrderQuery
{
    /// <summary>The order taken during this visit, or null. At most one — see <c>Order</c>.</summary>
    Task<OrderDescriptor?> ForVisitAsync(Guid visitId, CancellationToken cancellationToken = default);

    /// <summary>This outlet's orders, newest first.</summary>
    Task<IReadOnlyList<OrderDescriptor>> ForOutletAsync(
        Guid outletId, CancellationToken cancellationToken = default);
}
