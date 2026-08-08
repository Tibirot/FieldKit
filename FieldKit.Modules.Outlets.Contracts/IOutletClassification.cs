namespace FieldKit.Modules.Outlets.Contracts;

/// <summary>How one outlet is classified — which channel it trades in, and where it is taxed.</summary>
/// <remarks>
/// <para>
/// A record rather than a bare <c>Guid</c> return, so the shape survives a second classification
/// dimension. Segment and banner are plain strings on the outlet and nothing branches on them yet;
/// when something does, this grows a property instead of the interface growing a method.
/// </para>
/// <para>
/// <b><see cref="CountryCode"/> is that second dimension arriving</b> — added for tax
/// (<c>PRD-07</c>), where the rate is keyed by <c>(tax class, country)</c>. It qualifies on the same
/// test channel did: something another module decides with, rather than a detail of the outlet.
/// Adding it here rather than growing a third contract keeps one call on a resolution path that
/// already makes it, and the record shape is what made the addition free for existing callers.
/// </para>
/// <para>
/// <b>Nullable, because an address is optional</b> (<c>OUT-01</c>). A shop entered without one is
/// classified but not placed, and a consumer that needs a jurisdiction has to say what it does about
/// that rather than assume a default — for tax, guessing a country is guessing a rate.
/// </para>
/// </remarks>
/// <para>
/// <b><see cref="Segment"/> is the third dimension, and it is the one this record's doc predicted</b>
/// — "when something does [branch on segment], this grows a property instead of the interface
/// growing a method". Journey is that something: a call frequency may be set per outlet or derived
/// from its segment (<c>JRN-01</c>), so the generator has to know which segment a shop is in. It
/// qualifies on the same test channel and country did — something another module *decides with*,
/// rather than a detail of the outlet. Banner still does not, and stays off.
/// </para>
/// <param name="CountryCode">ISO-3166-1 alpha-2, upper-cased, from the outlet's address.</param>
/// <param name="Segment">
/// The tenant's own segmentation label (A, B, C…), or null for a shop nobody has segmented. Free
/// text on the outlet, so it is compared as the tenant typed it — see <c>SegmentFrequency</c>.
/// </param>
public sealed record OutletClassification(
    Guid OutletId, Guid ChannelId, string? CountryCode, string? Segment);

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
