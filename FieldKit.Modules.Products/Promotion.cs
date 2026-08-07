using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products;

/// <summary>
/// What kind of discount a promotion applies (<c>PRD-05</c>,
/// <see href="../../docs/product/decisions-and-assumptions.md">B1</see>).
/// </summary>
/// <remarks>
/// <para>
/// Two of the four types B1 names. <c>VolumeTiered</c> (buy N+ → discount) and <c>BuyXGetY</c>
/// (BOGO / bundle) arrive in the second promotion PR: both need child rows — a tier table, a
/// get-this-for-that pair — which is a different shape of change from the two flat ones here, and
/// the delivery plan budgets `PRD-05` as two PRs for that reason.
/// </para>
/// <para>
/// <b>Stored as a string, unlike this module's other enums</b> (<c>ProductStatus</c>,
/// <c>OverrideKind</c>, which are ints). Not a slip: this value appears inside a database check
/// constraint, and an integer there is both unreadable to anyone looking at the schema and silently
/// wrong the day a member is renumbered. Outlets stores its status the same way. Where an enum stays
/// inside the application, the ordinal is cheaper and fine.
/// </para>
/// </remarks>
public enum PromotionType
{
    /// <summary>A percentage off the line's net price. Carries <c>PercentOff</c>.</summary>
    PercentOff,

    /// <summary>A fixed sum off, in its own currency. Carries <c>AmountOff</c> and <c>Currency</c>.</summary>
    FixedAmountOff,
}

/// <summary>
/// A discount rule: a type, a value, a window, and a priority (<c>PRD-05</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Authoring only.</b> Nothing here decides whether a promotion applies to an order line — that
/// is <c>PRD-06</c>, resolved by priority within the validity window (<c>BR-PRD-3</c>,
/// <c>BR-PRD-6</c>), and it will read this aggregate rather than live in it. Where a promotion
/// reaches — which channels and outlets — is the next slice, exactly as <see cref="PriceList"/> was
/// authored before <see cref="PriceListAssignment"/> said where it applied. Until then a promotion is
/// a rule that exists and discounts nobody.
/// </para>
/// <para>
/// <b>The window is half-open <c>[ValidFrom, ValidTo)</c> and made of dates</b>, for the same two
/// reasons as <see cref="PriceList"/>: an inclusive end leaves a gap or an overlap depending on who
/// reads it, and a promotion runs from a business day, which begins at a different instant in every
/// timezone (<c>BR-PRD-6</c> evaluates it in the outlet's).
/// </para>
/// </remarks>
public sealed class Promotion : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid Id { get; private set; }

    public string Name { get; private set; } = null!;

    public PromotionType Type { get; private set; }

    /// <summary>Set for <see cref="PromotionType.PercentOff"/>, null otherwise. In <c>0 &lt; p ≤ 100</c>.</summary>
    public decimal? PercentOff { get; private set; }

    /// <summary>Set for <see cref="PromotionType.FixedAmountOff"/>, null otherwise. Positive.</summary>
    public decimal? AmountOff { get; private set; }

    /// <summary>ISO-4217, upper-cased. Set with <see cref="AmountOff"/> and null without it.</summary>
    /// <remarks>
    /// A fixed-amount discount carries its own currency rather than borrowing the price list's,
    /// because a promotion is authored once and may reach outlets priced in more than one. Resolution
    /// refusing to discount a EUR line by an RON amount is <c>BR-PRD-1</c> holding — a mismatch is a
    /// promotion that does not apply, not a conversion. That check belongs to <c>PRD-06</c>; storing
    /// the currency is what makes it possible to make.
    /// </remarks>
    public string? Currency { get; private set; }

    public DateOnly ValidFrom { get; private set; }

    /// <summary>Exclusive. Null means open-ended — it runs until something withdraws it.</summary>
    public DateOnly? ValidTo { get; private set; }

    /// <summary>
    /// Which promotion wins when several are in scope. <b>Higher beats lower</b> (<c>BR-PRD-3</c>).
    /// </summary>
    /// <remarks>
    /// The direction is a real decision and the opposite convention is common — "priority 1" reads
    /// like *first* in most people's heads. Higher-wins is chosen because of what each does to the
    /// data over time: with lowest-wins, a promotion that must beat everything already authored means
    /// renumbering the others, and once something sits at 1 the next one needs 0, then -1. With
    /// higher-wins the author picks a bigger number and touches nothing else.
    /// <para>
    /// Ties are possible and are not refused here — two promotions at the same priority are a
    /// legitimate intermediate state while an author is editing, and a uniqueness rule would block
    /// the edit rather than the mistake. Breaking the tie deterministically is <c>PRD-06</c>'s job,
    /// on the same reasoning as price resolution: the answer must not depend on which device asked.
    /// </para>
    /// </remarks>
    public int Priority { get; private set; }

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private Promotion() { } // EF

    /// <summary>A percentage off. <paramref name="percentOff"/> is a percentage, so 15 means 15%.</summary>
    public static Promotion PercentageOff(
        string name, decimal percentOff, DateOnly from, DateOnly? to, int priority) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Type = PromotionType.PercentOff,
            PercentOff = percentOff,
            ValidFrom = from,
            ValidTo = to,
            Priority = priority,
        };

    /// <summary>A fixed sum off, in its own currency.</summary>
    public static Promotion FixedAmountOff(
        string name, decimal amountOff, string currency, DateOnly from, DateOnly? to, int priority) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Type = PromotionType.FixedAmountOff,
            AmountOff = amountOff,
            Currency = currency.ToUpperInvariant(),
            ValidFrom = from,
            ValidTo = to,
            Priority = priority,
        };

    /// <summary>
    /// Re-values, re-dates and re-prioritises. The <b>type is not here</b>, deliberately.
    /// </summary>
    /// <remarks>
    /// Changing a promotion's type would reinterpret its value — 15 meaning "15% off" becoming 15
    /// meaning "€15 off" is not an edit, it is a different rule keeping the old one's id, and every
    /// order already priced against it would be explained by a rule that no longer exists. The same
    /// reasoning keeps a currency off <see cref="PriceList.Update"/>. A tenant that wants the other
    /// type authors the other promotion and withdraws this one.
    /// </remarks>
    public void Update(
        string name, decimal value, DateOnly from, DateOnly? to, int priority, IClock clock)
    {
        Name = name;
        ValidFrom = from;
        ValidTo = to;
        Priority = priority;

        if (Type == PromotionType.PercentOff) PercentOff = value;
        else AmountOff = value;

        ModifiedAtUtc = clock.UtcNow;
    }

    /// <summary>Whether this promotion is live on <paramref name="date"/> — half-open, as above.</summary>
    public bool Covers(DateOnly date) =>
        date >= ValidFrom && (ValidTo is not { } end || date < end);
}

/// <summary>
/// What a promotion discounts — one product, or everything in one category (<c>PRD-05</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly one of <see cref="ProductId"/> and <see cref="CategoryId"/> is set</b>, enforced by a
/// check constraint rather than only by the endpoint — the same shape, and the same argument, as
/// <see cref="PriceListAssignment"/>: a row with both targets two things and means neither, a row
/// with neither targets nothing.
/// </para>
/// <para>
/// <b>A category target is not expanded into its products here.</b> Storing the expansion would
/// freeze the category's membership at authoring time, so a product added to Beverages next week
/// would silently miss a promotion that says it covers Beverages. Resolution walks the hierarchy
/// instead (<c>PRD-06</c>) — which costs a join at read time and is the only version that stays true.
/// </para>
/// </remarks>
public sealed class PromotionTarget : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid Id { get; private set; }

    public Guid PromotionId { get; private set; }

    /// <summary>Set when this targets one product. Null when <see cref="CategoryId"/> is set.</summary>
    public Guid? ProductId { get; private set; }

    /// <summary>Set when this targets a category and its descendants. Null when a product is set.</summary>
    public Guid? CategoryId { get; private set; }

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private PromotionTarget() { } // EF

    public static PromotionTarget Product(Guid promotionId, Guid productId) =>
        new() { Id = Guid.CreateVersion7(), PromotionId = promotionId, ProductId = productId };

    public static PromotionTarget Category(Guid promotionId, Guid categoryId) =>
        new() { Id = Guid.CreateVersion7(), PromotionId = promotionId, CategoryId = categoryId };
}
