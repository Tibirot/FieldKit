using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products;

/// <summary>
/// A brand a product is sold under — Veridian, Aqua Pura (<c>PRD-01</c>).
/// </summary>
/// <remarks>
/// <para>
/// Flat, unlike <see cref="Category"/>. Brands nest in the real world — a house brand with
/// sub-brands — but nothing in this product asks a question that needs the nesting: promotions scope
/// to a brand, reports group by one, and both work on a flat list. A hierarchy costs a cycle check,
/// a delete guard and a tree-shaped UI, and none of that is bought by a consumer that exists. If one
/// appears, adding <c>ParentId</c> is an additive migration.
/// </para>
/// <para>
/// Reference data with a stable id, for the reason <see cref="Category"/> is: a promotion scoped to
/// a brand matches on the id, so renaming the brand changes a label and breaks nothing.
/// </para>
/// </remarks>
public sealed class Brand : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid Id { get; private set; }

    /// <summary>Unique within the tenant — two brands with one name are a data-entry accident.</summary>
    public string Name { get; private set; } = null!;

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private Brand() { } // EF

    public static Brand Create(string name) => new() { Id = Guid.CreateVersion7(), Name = name };

    public void Rename(string name, IClock clock)
    {
        Name = name;
        ModifiedAtUtc = clock.UtcNow;
    }
}
