namespace FieldKit.Modules.Outlets.Contracts;

/// <summary>How one outlet is classified — today, which channel it trades in.</summary>
/// <remarks>
/// A record rather than a bare <c>Guid</c> return, so the shape survives a second classification
/// dimension. Segment and banner are plain strings on the outlet and nothing branches on them yet;
/// when something does, this grows a property instead of the interface growing a method.
/// </remarks>
public sealed record OutletClassification(Guid OutletId, Guid ChannelId);

/// <summary>
/// The classification other modules make decisions with (<c>OUT-01</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Channel, and nothing else.</b> An outlet carries a code, a name, a status, an address,
/// coordinates, contacts, a time zone and custom fields, and none of that is here — because none of
/// it is something another module decides with. Channel is: it selects which assortment applies
/// (<c>PRD-02</c>) and which price list (<c>BR-PRD-2</c>). Exposing the rest would be inviting
/// callers to reach for an outlet's details through the seam meant for its classification, and the
/// day one does, Outlets cannot change an address format without breaking Products.
/// </para>
/// <para>
/// <b>Shaped by its first real caller, which is assortments rather than pricing.</b> The delivery
/// plan expected this contract to arrive with the price resolver and put it late deliberately, on
/// the principle that an interface designed before its consumer is a guess other modules have to
/// live with. Assortments reached it first and asked for something the resolver would not have —
/// <see cref="ChannelExistsAsync"/> — which is the principle working rather than the plan failing.
/// </para>
/// <para>
/// Separate from <c>IOutletCatalog</c> on purpose. That answers "does this outlet exist, and what is
/// it called"; this answers "what kind of shop is it". Widening <c>OutletSummary</c> with a channel
/// would have been fewer types and would have made every existing consumer of that record bind to a
/// field it never asked for.
/// </para>
/// </remarks>
public interface IOutletClassification
{
    /// <summary>
    /// Classifies several outlets at once. Ids with no match are absent from the result rather than
    /// returned as nulls.
    /// </summary>
    /// <remarks>
    /// Batch, mirroring <c>IOutletCatalog.FindManyAsync</c> and for the same reason: the callers are
    /// resolving an assortment or a price for a round of outlets, and a per-outlet call turns one
    /// query into as many as the rep has stops.
    /// </remarks>
    Task<IReadOnlyList<OutletClassification>> ClassifyManyAsync(
        IReadOnlyCollection<Guid> outletIds, CancellationToken cancellationToken = default);

    /// <summary>Whether a channel exists for this tenant.</summary>
    /// <remarks>
    /// For the module authoring rules <i>against</i> a channel rather than reading one off an
    /// outlet. A channel assortment names a <c>ChannelId</c>, and Products cannot see the channel
    /// table (AT-1) — without this it would either accept an id that matches nothing, producing an
    /// assortment no outlet can ever fall into, or reach into another module's schema.
    /// <para>
    /// Deliberately a predicate rather than a list of channels. Products has no business rendering
    /// or iterating a tenant's channel vocabulary — the back office reads that from Outlets' own
    /// endpoint. All it needs to know is whether the id it was handed is real.
    /// </para>
    /// </remarks>
    Task<bool> ChannelExistsAsync(Guid channelId, CancellationToken cancellationToken = default);
}
