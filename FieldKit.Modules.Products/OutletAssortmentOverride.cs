using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products;

/// <summary>Whether an override puts a product into an outlet's assortment or takes it out.</summary>
public enum AssortmentOverrideKind
{
    Added = 0,
    Removed = 1,
}

/// <summary>
/// One outlet's departure from its channel's assortment (<c>PRD-02</c>, <c>B2</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Overrides rather than a per-outlet assortment.</b> A shop's list is almost always its
/// channel's, with a handful of exceptions — a line the buyer refuses, a local speciality nobody
/// else carries. Storing the whole list per outlet would mean a tenant with 4,000 shops and 800
/// products keeps 3.2 million rows to express a few hundred deliberate differences, and adding a
/// product to a channel would mean rewriting every one of them.
/// </para>
/// <para>
/// <b>The override is what is stored; the effective assortment is computed.</b> There is no
/// materialised per-outlet list to keep in step, so a change to the channel assortment is
/// immediately true everywhere it should be, without a backfill that can half-fail.
/// </para>
/// <para>
/// <see cref="IsMustStock"/> applies only to <see cref="AssortmentOverrideKind.Added"/>: a product
/// added for one outlet still needs to say whether it is expected on the shelf there, and inheriting
/// a flag from a channel assortment it is not in would be inheriting from nothing.
/// </para>
/// </remarks>
public sealed class OutletAssortmentOverride : AggregateRoot, ITenantOwned, IAuditable, ISyncTracked
{
    /// <summary>Set by the row-version interceptor, never here (ADR-0013). W8 slice 8d.</summary>
    public long RowVersion { get; set; }

    public Guid Id { get; private set; }

    /// <summary>The outlet this is about — an Outlets id, unenforceable here by design.</summary>
    public Guid OutletId { get; private set; }

    public Guid ProductId { get; private set; }

    public AssortmentOverrideKind Kind { get; private set; }

    /// <summary>Meaningful only when <see cref="Kind"/> is <c>Added</c>.</summary>
    public bool IsMustStock { get; private set; }

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private OutletAssortmentOverride() { } // EF

    public static OutletAssortmentOverride Create(
        Guid outletId, Guid productId, AssortmentOverrideKind kind, bool isMustStock) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            OutletId = outletId,
            ProductId = productId,
            Kind = kind,
            IsMustStock = kind is AssortmentOverrideKind.Added && isMustStock,
        };

    public void Change(AssortmentOverrideKind kind, bool isMustStock, IClock clock)
    {
        Kind = kind;
        IsMustStock = kind is AssortmentOverrideKind.Added && isMustStock;
        ModifiedAtUtc = clock.UtcNow;
    }
}
