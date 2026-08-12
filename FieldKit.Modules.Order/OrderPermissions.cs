namespace FieldKit.Modules.Order;

/// <summary>
/// What the Order module gates on (W11 slice 4a).
/// </summary>
/// <remarks>
/// Reading is not here: orders are read under <c>visit:read</c>, borrowed from Visit because Order may
/// not reference Visit's implementation assembly (AT-1). <c>OrderEndpoints</c> holds that literal and
/// says why it is still a borrow.
/// </remarks>
public static class OrderPermissions
{
    /// <summary>
    /// Refusing a submitted order back to the rep (<c>ORD-12</c>).
    /// </summary>
    /// <remarks>
    /// Named for the act, not the table. A holder can refuse an order; they cannot change one, which
    /// is what <c>order:write</c> would have implied and what <c>BR-ORD-4</c> denies to everybody.
    /// </remarks>
    public const string Reject = "order:reject";
}
