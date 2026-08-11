using FieldKit.Modules.Order.Contracts;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FieldKit.Modules.Order;

/// <summary>One line of a stored order.</summary>
/// <param name="PackSize">Units per pack at capture; null when sold loose.</param>
public sealed record OrderLineResponse(
    Guid ProductId,
    decimal Quantity,
    string UnitOfMeasure,
    int? PackSize,
    decimal UnitPrice,
    decimal LineTotal);

/// <summary>An order, as a reader sees it.</summary>
/// <param name="Status">
/// The name, not the ordinal — rendered here rather than by a converter on the contract. The repo
/// carries three per-property <c>JsonStringEnumConverter</c>s already because nothing registers a
/// global one, and W11 slice 0b is where that stops; this is the pattern that needs no band-aid,
/// the same one <c>SurveyQuestionResponse</c> uses.
/// </param>
/// <param name="Total">
/// <b>The device's total.</b> Not recomputed, and deliberately so — see <c>BR-ORD-6</c> and W11
/// slice 0. From slice 2 this response gains the server's recomputation <i>beside</i> it; a reader
/// today is seeing what the rep and the shopkeeper agreed, which is what an order is.
/// </param>
public sealed record OrderResponse(
    Guid Id,
    Guid VisitId,
    Guid OutletId,
    string UserId,
    string Status,
    string CurrencyCode,
    decimal Total,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<OrderLineResponse> Lines);

/// <summary>
/// Reading orders (<c>ORD-01</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no POST here, and there is not going to be one.</b> An order is captured at a counter
/// with no signal and arrives through <c>/sync/push</c>; a create endpoint would be an API no planned
/// screen calls and a second writer into a record whose conflict story (<c>B7</c>) depends on there
/// being one. The same call <c>AuditEndpoints</c> made in W10 slice 3a.
/// </para>
/// <para>
/// <b>Gated on <c>visit:read</c> rather than an <c>order:read</c> of its own</b>, following Audit —
/// and the argument is weaker here, so it is worth stating rather than inheriting. An audit
/// <i>is</i> what happened during a visit. An order is commercial: a finance or customer-service
/// reader might reasonably see order values without seeing where a rep stood or how long they
/// stayed, and that is a real requirement this reuse would not serve.
/// </para>
/// <para>
/// It is reused anyway, for now, because permissions are Keycloak realm roles — and a new one needs a
/// realm change that <b>a deploy does not apply</b>
/// (<see href="../docs/engineering/deploying.md">the runbook</see>, W10's finding). Minting
/// <c>order:read</c> before a reader exists who needs it apart from visits would put a role in every
/// tenant's realm that nothing checks and no one was given. When that reader appears, this is the
/// line that changes.
/// </para>
/// </remarks>
internal static class OrderEndpoints
{
    /// <summary>
    /// A literal because <c>VisitPermissions</c> lives in Visit's implementation assembly, which this
    /// module may not reference (AT-1) — the same shape <c>AuditEndpoints</c> is forced into.
    /// </summary>
    private const string VisitRead = "visit:read";

    public static void MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var orders = endpoints.MapGroup("/api/orders").WithTags("Order");

        orders.MapGet("/by-visit/{visitId:guid}", async (
            Guid visitId, IOrderQuery query, CancellationToken ct) =>
        {
            var order = await query.ForVisitAsync(visitId, ct);

            return order is null ? Results.NotFound() : Results.Ok(Respond(order));
        }).RequirePermission(VisitRead);

        orders.MapGet("/by-outlet/{outletId:guid}", async (
            Guid outletId, IOrderQuery query, CancellationToken ct) =>
        {
            var all = await query.ForOutletAsync(outletId, ct);

            return all.Select(Respond).ToList();
        }).RequirePermission(VisitRead);
    }

    private static OrderResponse Respond(OrderDescriptor order) => new(
        order.Id,
        order.VisitId,
        order.OutletId,
        order.UserId,
        order.Status.ToString(),
        order.CurrencyCode,
        order.Total,
        order.CapturedAtUtc,
        [.. order.Lines.Select(line => new OrderLineResponse(
            line.ProductId,
            line.Quantity,
            line.UnitOfMeasure,
            line.PackSize,
            line.UnitPrice,
            line.LineTotal))]);
}
