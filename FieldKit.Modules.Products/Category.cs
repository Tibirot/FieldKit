using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products;

/// <summary>
/// A node in the product classification tree — Beverages → Water → Sparkling (<c>PRD-01</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Adjacency list, no materialized path.</b> The parent pointer is the whole truth: no depth, no
/// path column. Those are denormalizations that have to be rewritten across every descendant when a
/// category moves, and the read they buy — "everything under Beverages, transitively" — is one this
/// module does not make yet. The same reasoning <see cref="Category"/>'s counterpart in Organization
/// records, reached independently because the shape of the problem is the same.
/// </para>
/// <para>
/// <b>Reference data with a stable id</b>, for the reason `Channel` is: category is something rules
/// key off. An assortment scoped to a category, a promotion that discounts one, a share-of-shelf
/// report that groups by one — all of them match on the id, so renaming "Soft Drinks" to
/// "Carbonates" changes a label and breaks nothing. A free-text category would make those rules match
/// on spelling.
/// </para>
/// <para>
/// Tenant-owned, because a classification is a tenant's own commercial view. A distributor carrying
/// several brands' portfolios does not organize them the way any one of those brands would.
/// </para>
/// </remarks>
public sealed class Category : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid Id { get; private set; }

    /// <summary>Unique among its siblings, not tenant-wide — see <see cref="ProductsDbContext"/>.</summary>
    public string Name { get; private set; } = null!;

    /// <summary>Null for a root. The tree is expressed here and nowhere else.</summary>
    public Guid? ParentId { get; private set; }

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private Category() { } // EF

    public static Category Create(string name, Guid? parentId) =>
        new() { Id = Guid.CreateVersion7(), Name = name, ParentId = parentId };

    public void Rename(string name, IClock clock)
    {
        Name = name;
        ModifiedAtUtc = clock.UtcNow;
    }

    /// <summary>Re-parents this category; null makes it a root.</summary>
    /// <remarks>
    /// The caller checks that the move does not create a cycle — see
    /// <see cref="CategoryHierarchy.WouldCreateCycle"/>. The entity cannot: it can see its own parent
    /// and nothing above, so it has no way to know whether the destination is inside its own subtree.
    /// </remarks>
    public void MoveTo(Guid? parentId, IClock clock)
    {
        ParentId = parentId;
        ModifiedAtUtc = clock.UtcNow;
    }
}
