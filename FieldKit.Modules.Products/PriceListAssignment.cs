using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products;

/// <summary>
/// Where a price list applies — a whole channel, or one outlet (<c>PRD-03</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly one of <see cref="ChannelId"/> and <see cref="OutletId"/> is set</b>, and the database
/// enforces it with a check constraint rather than trusting the endpoint. A row with both is a rule
/// with two scopes and no meaning; a row with neither is a rule that applies nowhere. Both are
/// states nothing downstream could sensibly handle, so the honest thing is to make them
/// unrepresentable rather than defensively skipped by every reader.
/// </para>
/// <para>
/// <b>Two scopes, not a specificity column.</b> <c>BR-PRD-2</c> resolves outlet override → channel →
/// default, and it would be possible to store that precedence as a number on the row. Storing which
/// *kind* of scope it is instead keeps the precedence in the resolver, where it can be read and
/// changed; a number would spread the rule across every row that has ever been written and make
/// changing it a backfill.
/// </para>
/// <para>
/// Neither id has a foreign key: both point into Outlets, and a constraint across a module boundary
/// is the coupling schema-per-module (ADR-0005) exists to prevent. The endpoint checks them through
/// <c>IOutletClassification</c> and <c>IOutletCatalog</c>.
/// </para>
/// </remarks>
public sealed class PriceListAssignment : AggregateRoot, ITenantOwned, IAuditable, ISyncTracked
{
    /// <summary>Set by the row-version interceptor, never here (ADR-0013). W8 slice 8e.</summary>
    public long RowVersion { get; set; }

    public Guid Id { get; private set; }

    public Guid PriceListId { get; private set; }

    /// <summary>Set when this applies to a whole channel. Null when <see cref="OutletId"/> is set.</summary>
    public Guid? ChannelId { get; private set; }

    /// <summary>Set when this applies to one outlet. Null when <see cref="ChannelId"/> is set.</summary>
    public Guid? OutletId { get; private set; }

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private PriceListAssignment() { } // EF

    public static PriceListAssignment ToChannel(Guid priceListId, Guid channelId) =>
        new() { Id = Guid.CreateVersion7(), PriceListId = priceListId, ChannelId = channelId };

    public static PriceListAssignment ToOutlet(Guid priceListId, Guid outletId) =>
        new() { Id = Guid.CreateVersion7(), PriceListId = priceListId, OutletId = outletId };
}

/// <summary>
/// Published when a price list's scope changes — Sync turns this into a reference delta
/// (<c>PRD-03</c>, module registry).
/// </summary>
/// <remarks>
/// <para>
/// <b>Raised on assignment, not on authoring.</b> A list with prices and no assignment is a draft:
/// it exists, it is priced, and it reaches nobody. What a device needs to hear about is the moment
/// that changes — which outlets are now priced differently — and that is what assigning does.
/// Re-pricing an already-assigned list will need its own event when the sync engine exists to want
/// one; this is deliberately the smaller claim.
/// </para>
/// <para>
/// It carries the currency and window rather than only the id, because a consumer deciding whether
/// this delta matters to it should not have to call back to find out. The prices themselves are not
/// here — an event that carried them would be a replica of the table, going stale the moment
/// anything changed.
/// </para>
/// <para>
/// Declared here rather than in a <c>Products.Contracts</c> assembly, which does not exist —
/// exactly as <c>ProductCreated</c> is. That is a real limitation: no other module can reference
/// this type today, so nothing can subscribe. It is also not worth fixing before Sync (W8) is the
/// consumer that would shape it, on the same reasoning that keeps <c>IAssortmentService</c> unbuilt.
/// The outbox stores the event regardless, so the record exists when a subscriber arrives.
/// </para>
/// </remarks>
public sealed record PriceListPublished(
    Guid Id,
    DateTimeOffset OccurredOn,
    Guid PriceListId,
    string Currency,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    int ChannelCount,
    int OutletCount) : IIntegrationEvent;
