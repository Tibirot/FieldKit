using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products;

/// <summary>
/// What kind of thing a product is, for tax — Standard, Reduced, Zero-rated (<c>PRD-01</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A tax class is not a rate.</b> It is the category a jurisdiction taxes; the rate is what a
/// given country charges that category this year. Bottled water is "reduced" in several European
/// markets and standard-rated in others, and the percentage moves without the classification moving.
/// Folding a percentage onto this entity would make a tenant selling in two countries choose which
/// one to be wrong about.
/// </para>
/// <para>
/// So this is the classification only. The rate — keyed by <c>(tax class, country)</c>, with its own
/// effective window — arrives with tax computation in W6 slice 9 (<c>PRD-07</c>), designed against
/// the resolver that reads it rather than guessed at here. Until then a product can be classified
/// and nothing computes tax from it, which is the honest state: <c>BR-PRD-5</c> stores prices net
/// and computes tax at order time, so nothing needs a rate before there is an order.
/// </para>
/// </remarks>
public sealed class TaxClass : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid Id { get; private set; }

    /// <summary>Unique within the tenant.</summary>
    public string Name { get; private set; } = null!;

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private TaxClass() { } // EF

    public static TaxClass Create(string name) => new() { Id = Guid.CreateVersion7(), Name = name };

    public void Rename(string name, IClock clock)
    {
        Name = name;
        ModifiedAtUtc = clock.UtcNow;
    }
}
