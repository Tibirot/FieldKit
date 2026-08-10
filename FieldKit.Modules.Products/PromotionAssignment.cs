using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products;

/// <summary>
/// Where a promotion applies — a whole channel, or one outlet (<c>PRD-05</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly one of <see cref="ChannelId"/> and <see cref="OutletId"/> is set</b>, enforced by a
/// check constraint rather than trusted to the endpoint, for the reasons
/// <see cref="PriceListAssignment"/> gives at length: a row with both is a rule with two scopes and
/// no meaning, a row with neither applies nowhere, and neither is a state a reader could sensibly
/// handle.
/// </para>
/// <para>
/// <b>This is very nearly a copy of <see cref="PriceListAssignment"/>, and that is deliberate.</b>
/// The obvious alternative — one <c>scope_assignment</c> table with a discriminator naming what it
/// points at — would buy one fewer table and cost the thing that makes both of these safe: the
/// tenant-keyed composite foreign key back to a specific parent. A shared table can only carry a
/// nullable <c>price_list_id</c> and a nullable <c>promotion_id</c>, which is two more nullable
/// columns and a third check constraint to keep them honest, or a polymorphic id with no foreign key
/// at all. Two small tables that each say exactly one thing beat one table that has to explain
/// itself.
/// </para>
/// <para>
/// Neither id has a foreign key: both point into Outlets, and a constraint across a module boundary
/// is the coupling schema-per-module (ADR-0005) exists to prevent. The endpoint checks them through
/// <c>IOutletClassification</c> and <c>IOutletCatalog</c>.
/// </para>
/// </remarks>
public sealed class PromotionAssignment : AggregateRoot, ITenantOwned, IAuditable, ISyncTracked
{
    /// <summary>Set by the row-version interceptor, never here (ADR-0013). W8 slice 8f.</summary>
    public long RowVersion { get; set; }

    public Guid Id { get; private set; }

    public Guid PromotionId { get; private set; }

    /// <summary>Set when this applies to a whole channel. Null when <see cref="OutletId"/> is set.</summary>
    public Guid? ChannelId { get; private set; }

    /// <summary>Set when this applies to one outlet. Null when <see cref="ChannelId"/> is set.</summary>
    public Guid? OutletId { get; private set; }

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private PromotionAssignment() { } // EF

    public static PromotionAssignment ToChannel(Guid promotionId, Guid channelId) =>
        new() { Id = Guid.CreateVersion7(), PromotionId = promotionId, ChannelId = channelId };

    public static PromotionAssignment ToOutlet(Guid promotionId, Guid outletId) =>
        new() { Id = Guid.CreateVersion7(), PromotionId = promotionId, OutletId = outletId };
}

/// <summary>
/// Published when a promotion's scope changes — Sync turns this into a reference delta
/// (<c>PRD-05</c>, module registry).
/// </summary>
/// <remarks>
/// <para>
/// <b>Named <i>Activated</i> by the registry, and raised on assignment, which is the same moment.</b>
/// A promotion with a type, a value, targets and a window still discounts nobody until it is pointed
/// at a channel or an outlet — that is the moment it starts to affect what a rep sees, and so the
/// moment a device needs to hear about. It is raised on withdrawal too, with both counts at zero:
/// "this promotion now reaches nobody" is a change a consumer needs as much as any other, and a
/// device that never hears it keeps offering a deal that has been pulled.
/// </para>
/// <para>
/// It carries the type, window and priority rather than only the id, so a consumer deciding whether
/// this delta matters to it need not call back. The value, the tiers, the bundle and the targets are
/// <i>not</i> here — an event carrying them would be a replica of four tables, going stale the moment
/// any of them changed.
/// </para>
/// <para>
/// Declared here rather than in a <c>Products.Contracts</c> assembly, which does not exist — exactly
/// as <see cref="PriceListPublished"/> and <c>ProductCreated</c> are. That is a real limitation: no
/// other module can reference this type today, so nothing can subscribe. It is also not worth fixing
/// before Sync (W8) is the consumer that would shape it. The outbox stores the event regardless, so
/// the record exists when a subscriber arrives.
/// </para>
/// </remarks>
public sealed record PromotionActivated(
    Guid Id,
    DateTimeOffset OccurredOn,
    Guid PromotionId,
    PromotionType Type,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    int Priority,
    int ChannelCount,
    int OutletCount) : IIntegrationEvent;
