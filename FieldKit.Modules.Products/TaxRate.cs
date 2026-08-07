using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products;

/// <summary>
/// What one country charges one <see cref="TaxClass"/>, for a window of time (<c>PRD-07</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The rate the class deliberately does not carry.</b> <see cref="TaxClass"/> says what kind of
/// thing a product is — standard, reduced, zero-rated — and this says what that costs where. Bottled
/// water is reduced-rate in several European markets and standard-rated in others, and the
/// percentage moves without the classification moving, so folding the two together would make a
/// tenant selling in two countries pick which one to be wrong about.
/// </para>
/// <para>
/// <b>The window is half-open <c>[EffectiveFrom, EffectiveTo)</c></b>, like a price list's and a
/// promotion's, and it is not decoration here: VAT rates change on announced dates, and an order
/// re-priced after one moves has to compute the tax that applied when it was taken. An inclusive end
/// would leave the changeover day either double-covered or uncovered, which is the one day everyone
/// looks at.
/// </para>
/// <para>
/// <b>Zero is a real rate, unlike a zero discount.</b> A promotion at 0% is refused because it wins a
/// priority contest and then does nothing; a tax rate at 0% is how zero-rated goods are actually
/// taxed, and refusing it would force a tenant to express "no VAT on this" as "no rate authored",
/// which is the state that means <i>unknown</i>. The two must stay distinguishable.
/// </para>
/// </remarks>
public sealed class TaxRate : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid Id { get; private set; }

    public Guid TaxClassId { get; private set; }

    /// <summary>ISO-3166-1 alpha-2, upper-cased.</summary>
    public string CountryCode { get; private set; } = null!;

    /// <summary>A percentage, so 19 means 19%. In <c>0 ≤ p ≤ 100</c>.</summary>
    public decimal Percentage { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }

    /// <summary>Exclusive. Null means open-ended — it applies until something replaces it.</summary>
    public DateOnly? EffectiveTo { get; private set; }

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private TaxRate() { } // EF

    public static TaxRate Create(
        Guid taxClassId, string countryCode, decimal percentage, DateOnly from, DateOnly? to) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TaxClassId = taxClassId,
            CountryCode = countryCode.ToUpperInvariant(),
            Percentage = percentage,
            EffectiveFrom = from,
            EffectiveTo = to,
        };
}
