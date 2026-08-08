namespace FieldKit.Modules.Outlets.Contracts;

/// <summary>
/// Where an outlet is, and how close a rep has to be to count as there (<c>OUT-08</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The coordinates are nullable and the radius is not.</b> An outlet without coordinates is
/// ordinary — onboarding data routinely arrives without them (<c>OUT-01</c>) — and a caller has to
/// decide what that means rather than be handed a default position. A radius without a place to
/// centre it is meaningless, so the two travel together and the caller reads them together.
/// </para>
/// </remarks>
/// <param name="RadiusMetres">
/// How far from the outlet still counts as at it.
/// <para>
/// Today this is always <see cref="IOutletGeofence.DefaultRadiusMetres"/>. Making it configurable
/// per outlet or per channel is <c>OUT-08</c>, a <i>Should</i> that is not built — and the shape is
/// here now so that building it changes one query rather than every caller.
/// </para>
/// </param>
public sealed record OutletGeofence(
    Guid OutletId, double? Latitude, double? Longitude, int RadiusMetres);

/// <summary>
/// Where an outlet is, for deciding whether a rep is at it (<c>OUT-08</c>, <c>BR-VIS-2</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A contract of its own rather than a wider <c>IOutletCatalog</c></b>, because that one says so:
/// "it exposes no address, no coordinates, no contacts and no channel… a caller needing more should
/// ask for a contract that says what it needs, not for this one to grow." Visit needs one question
/// answered — where is this shop, and how close is close enough — and this is that question. The
/// Visit spec's own module-contract list says <c>IOutletCatalog (geofence)</c>; that line predates
/// the rule and is corrected there.
/// </para>
/// <para>
/// <b>The check itself is not here.</b> Outlets says where the shop is; Visit decides what being
/// eighty metres away means, because that is a rule about a visit rather than a fact about an
/// outlet — and <c>BR-VIS-2</c> answers it differently depending on whether the channel expects
/// presence at all (<c>IVisitWorkflow</c>).
/// </para>
/// <para>
/// Per outlet rather than batched: a check-in happens at one shop, and the caller is a rep standing
/// in front of it.
/// </para>
/// </remarks>
public interface IOutletGeofence
{
    /// <summary>
    /// The radius used until <c>OUT-08</c> makes it configurable.
    /// </summary>
    /// <remarks>
    /// A hundred and fifty metres, which is a compromise rather than a measurement: consumer GPS is
    /// routinely twenty to fifty metres out in a street of tall buildings, and a shopping centre is
    /// bigger than its pin. Tighter would flag honest reps standing inside the shop; much looser
    /// would stop flagging the thing the rule is for. It is a constant so that the number has one
    /// home and a reason attached, rather than being typed into a comparison somewhere.
    /// </remarks>
    public const int DefaultRadiusMetres = 150;

    /// <summary>
    /// Where <paramref name="outletId"/> is, or null when this tenant has no such outlet.
    /// </summary>
    /// <remarks>
    /// Null means "no such outlet", which the caller must tell apart from an outlet that exists and
    /// has no coordinates — the second is a shop nobody has placed yet, and refusing a check-in
    /// there would strand a rep at a real shop over missing master data.
    /// </remarks>
    Task<OutletGeofence?> ForOutletAsync(Guid outletId, CancellationToken cancellationToken = default);
}
