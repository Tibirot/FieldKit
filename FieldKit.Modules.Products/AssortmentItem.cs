using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products;

/// <summary>
/// One product that belongs in a channel's assortment (<c>PRD-02</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A flat row per (channel, product) rather than an <c>Assortment</c> aggregate holding lines.</b>
/// The aggregate would exist to enforce an invariant across the set, and there is none: adding a
/// product to a channel is independent of every other product in it. What the set is actually asked
/// is "is this product in this outlet's assortment" (<c>BR-PRD-4</c>) and "what should be on this
/// shelf" — both of which read rows, not an object graph. Modelling the collection would mean
/// loading a whole channel's assortment to answer a question about one product.
/// </para>
/// <para>
/// <b>The must-stock list is a flag, not a second table.</b> <c>B2</c> defines MSL as "the subset of
/// an assortment flagged must-stock", and a subset of a set is a predicate on its rows. A separate
/// table would let a product be must-stock without being in the assortment at all, which is a state
/// with no meaning that something would eventually have to check for.
/// </para>
/// <para>
/// <see cref="ChannelId"/> points into Outlets, which this module cannot see. The endpoint checks it
/// through <c>IOutletClassification.ChannelExistsAsync</c>; there is no foreign key, because a
/// database constraint across a module boundary is exactly the coupling schema-per-module
/// (ADR-0005) exists to prevent.
/// </para>
/// </remarks>
public sealed class AssortmentItem : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid Id { get; private set; }

    /// <summary>The channel this belongs to — an Outlets id, unenforceable here by design.</summary>
    public Guid ChannelId { get; private set; }

    public Guid ProductId { get; private set; }

    /// <summary>
    /// Whether this is a must-stock line, which drives audit availability checks and the order
    /// suggested-list.
    /// </summary>
    public bool IsMustStock { get; private set; }

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private AssortmentItem() { } // EF

    public static AssortmentItem Create(Guid channelId, Guid productId, bool isMustStock) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ChannelId = channelId,
            ProductId = productId,
            IsMustStock = isMustStock,
        };

    public void Flag(bool isMustStock, IClock clock)
    {
        IsMustStock = isMustStock;
        ModifiedAtUtc = clock.UtcNow;
    }
}
