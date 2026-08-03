using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Org;

/// <summary>
/// A node in the sales hierarchy — Country → Region → Area → Team, or whatever depth and labels a
/// tenant chooses (<c>ORG-01</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Adjacency list, no materialized path.</b> The parent pointer is the whole truth: no `Depth`
/// column, no `/country/region/` string. Both are denormalizations that have to be rewritten across
/// every descendant when a unit moves, and the read they buy is one this module does not make — an
/// org tree is tens of nodes, so the endpoint loads a tenant's units and shapes the tree in memory.
/// </para>
/// <para>
/// That trade stops holding somewhere in the thousands of units per tenant, which no FMCG sales
/// hierarchy reaches ([B6](../../docs/product/decisions-and-assumptions.md) bounds this to ~20
/// tenants). If it ever does, the fix is a recursive CTE or a path column added then — against a
/// real query, rather than guessed at now.
/// </para>
/// </remarks>
public sealed class OrgUnit : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid Id { get; private set; }

    /// <summary>Unique among siblings, not tenant-wide — see <see cref="OrgDbContext"/> for why.</summary>
    public string Name { get; private set; } = null!;

    /// <summary>Null for a root. A tenant may have more than one.</summary>
    public Guid? ParentId { get; private set; }

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private OrgUnit() { } // EF

    public static OrgUnit Create(string name, Guid? parentId) =>
        new() { Id = Guid.CreateVersion7(), Name = name, ParentId = parentId };

    public void Rename(string name, IClock clock)
    {
        Name = name;
        ModifiedAtUtc = clock.UtcNow;
    }

    /// <summary>
    /// Moves this unit under a new parent, or to the root when <paramref name="parentId"/> is null.
    /// </summary>
    /// <remarks>
    /// The caller checks that the move does not create a cycle — see
    /// <see cref="OrgHierarchy.WouldCreateCycle"/>. The entity cannot: it can see its own parent and
    /// nothing else, and an aggregate that has to load the rest of the tree to validate itself is an
    /// aggregate drawn at the wrong boundary.
    /// </remarks>
    public void MoveTo(Guid? parentId, IClock clock)
    {
        ParentId = parentId;
        ModifiedAtUtc = clock.UtcNow;
    }
}
