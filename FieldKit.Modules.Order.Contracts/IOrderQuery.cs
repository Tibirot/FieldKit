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
/// <see cref="Status"/> is the enum and carries no <c>JsonStringEnumConverter</c>, deliberately. This
/// is a module contract, not a wire type — the endpoint that serialises it renders the name into a
/// <c>string</c> field of its own, the way <c>SurveyQuestionResponse</c> does. Attaching a converter
/// here would be the fourth per-property band-aid in the repo for a gap W11 slice 0b fixes globally,
/// and it would put a serialisation concern in an assembly whose whole point is not having any.
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
