using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products;

/// <summary>
/// What kind of discount a promotion applies (<c>PRD-05</c>,
/// <see href="../../docs/product/decisions-and-assumptions.md">B1</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>All four types B1 names</b>, complete as of <c>BuyXGetY</c>. The check constraint on
/// <c>promotion</c> is written per type and no longer has an <c>ELSE</c> that permits anything: a
/// type nobody has constrained is now a type nobody can store.
/// </para>
/// <para>
/// The four differ in <i>where their discount lives</i>, which is the whole reason the constraint is
/// per type rather than one expression over the columns. <see cref="PercentOff"/> and
/// <see cref="FixedAmountOff"/> are flat — one discount, whatever the quantity — and carry it on the
/// promotion. <see cref="VolumeTiered"/> carries none, because it has as many discounts as it has
/// thresholds. <see cref="BuyXGetY"/> carries none either, but for a different reason: it does not
/// reduce a price at all.
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

    /// <summary>
    /// Buy N or more → discount, with the discount rising by threshold. Carries no value of its own;
    /// see <see cref="PromotionTier"/>.
    /// </summary>
    VolumeTiered,

    /// <summary>
    /// Buy X, get Y at a discount — BOGO when that discount is 100%. Carries its own quantities;
    /// see <c>Promotion.BuyQuantity</c>.
    /// </summary>
    BuyXGetY,
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

    /// <summary>How many must be bought for <see cref="PromotionType.BuyXGetY"/>. Null otherwise.</summary>
    public int? BuyQuantity { get; private set; }

    /// <summary>How many are then given at <see cref="GetPercentOff"/>. Null otherwise.</summary>
    public int? GetQuantity { get; private set; }

    /// <summary>The discount on the given units. <c>100</c> is free — the classic BOGO. Null otherwise.</summary>
    /// <remarks>
    /// A percentage rather than a free/discounted flag, because "buy two get one free" and "buy two
    /// get one half price" are the same offer with a different number, and a boolean would have made
    /// the second one a new type. 100 is not a special case in the storage, only in what a shopper
    /// calls it.
    /// </remarks>
    public decimal? GetPercentOff { get; private set; }

    /// <summary>
    /// What is given. <b>Null means the same product that was bought</b> — the classic BOGO.
    /// </summary>
    /// <remarks>
    /// Null rather than requiring the author to name the product again, because the promotion's
    /// targets may be a whole category: "buy two of anything in Beverages, get one free" gives one of
    /// <i>whichever</i> was bought, and there is no single id to write down. Naming a product turns
    /// the same mechanism into a cross-sell bundle — buy three cases of water, get a crate of cola at
    /// half price — without a second type.
    /// </remarks>
    public Guid? GetProductId { get; private set; }

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
    /// Buy N or more → discount. The discounts themselves live on <see cref="PromotionTier"/>.
    /// </summary>
    /// <remarks>
    /// No value here, deliberately: a tiered promotion has as many discounts as it has thresholds,
    /// and putting one of them on the promotion would make it the odd tier out — the one an author
    /// edits in a different place from all the others.
    /// </remarks>
    public static Promotion VolumeTiered(string name, DateOnly from, DateOnly? to, int priority) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Type = PromotionType.VolumeTiered,
            ValidFrom = from,
            ValidTo = to,
            Priority = priority,
        };

    /// <summary>
    /// Buy <paramref name="buyQuantity"/>, get <paramref name="getQuantity"/> at
    /// <paramref name="getPercentOff"/>% off. <paramref name="getProductId"/> null gives the same
    /// product that was bought.
    /// </summary>
    /// <remarks>
    /// <b>The only type that does not reduce a price.</b> The others answer "what does this line
    /// cost"; this one answers "what else comes with it", which is why it carries quantities instead
    /// of a value and why <c>PRD-06</c> will have to add a line rather than adjust one. The
    /// distinction is worth keeping visible here, because it is the reason
    /// <see cref="CarriesItsOwnValue"/> excludes it even though its columns are non-null.
    /// </remarks>
    public static Promotion BuyXGetY(
        string name,
        int buyQuantity,
        int getQuantity,
        decimal getPercentOff,
        Guid? getProductId,
        DateOnly from,
        DateOnly? to,
        int priority) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Type = PromotionType.BuyXGetY,
            BuyQuantity = buyQuantity,
            GetQuantity = getQuantity,
            GetPercentOff = getPercentOff,
            GetProductId = getProductId,
            ValidFrom = from,
            ValidTo = to,
            Priority = priority,
        };

    /// <summary>Re-states what a <see cref="PromotionType.BuyXGetY"/> promotion gives away.</summary>
    public void Rebundle(
        int buyQuantity, int getQuantity, decimal getPercentOff, Guid? getProductId, IClock clock)
    {
        BuyQuantity = buyQuantity;
        GetQuantity = getQuantity;
        GetPercentOff = getPercentOff;
        GetProductId = getProductId;
        ModifiedAtUtc = clock.UtcNow;
    }

    /// <summary>Whether this type's discount lives on the promotion rather than on child rows.</summary>
    /// <remarks>
    /// The question the endpoint asks in three places — whether to expect a <c>value</c>, whether to
    /// return one, whether to let an update change one. Written once here rather than as three
    /// separate <c>is not VolumeTiered</c> checks that would each have to learn about BOGO
    /// separately.
    /// </remarks>
    public static bool CarriesItsOwnValue(PromotionType type) =>
        type is PromotionType.PercentOff or PromotionType.FixedAmountOff;

    /// <summary>
    /// Re-names, re-dates and re-prioritises; re-values only the types that carry a value.
    /// The <b>type is not here</b>, deliberately.
    /// </summary>
    /// <remarks>
    /// Changing a promotion's type would reinterpret its value — 15 meaning "15% off" becoming 15
    /// meaning "€15 off" is not an edit, it is a different rule keeping the old one's id, and every
    /// order already priced against it would be explained by a rule that no longer exists. The same
    /// reasoning keeps a currency off <see cref="PriceList.Update"/>. A tenant that wants the other
    /// type authors the other promotion and withdraws this one.
    /// </remarks>
    public void Update(
        string name, decimal? value, DateOnly from, DateOnly? to, int priority, IClock clock)
    {
        Name = name;
        ValidFrom = from;
        ValidTo = to;
        Priority = priority;

        // Null for the types whose discount lives on child rows. Assigning it anyway would put a
        // value on a promotion the check constraint requires to have none.
        if (value is { } amount)
        {
            if (Type == PromotionType.PercentOff) PercentOff = amount;
            else AmountOff = amount;
        }

        ModifiedAtUtc = clock.UtcNow;
    }

    /// <summary>Whether this promotion is live on <paramref name="date"/> — half-open, as above.</summary>
    public bool Covers(DateOnly date) =>
        date >= ValidFrom && (ValidTo is not { } end || date < end);

    /// <summary>Announces that this promotion's scope changed.</summary>
    /// <remarks>
    /// On the aggregate rather than at the endpoint, so the event reaches the outbox in the same
    /// transaction as the rows that caused it (ADR-0006). Raising it from the handler after
    /// <c>SaveChanges</c> would be a dual write: the scope committed and the announcement lost, or
    /// the announcement sent for a scope that rolled back.
    /// <para>
    /// Called even when the scope is set to nothing — see <see cref="PromotionActivated"/> for why a
    /// withdrawal is an announcement too.
    /// </para>
    /// </remarks>
    public void Activate(int channelCount, int outletCount, IClock clock) =>
        Raise(new PromotionActivated(
            Guid.CreateVersion7(),
            clock.UtcNow,
            Id,
            Type,
            ValidFrom,
            ValidTo,
            Priority,
            channelCount,
            outletCount));
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

/// <summary>
/// One threshold of a <see cref="PromotionType.VolumeTiered"/> promotion: buy this many, get this
/// off (<c>PRD-05</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Each tier carries its own discount, self-describing</b> — a percentage, or an amount with its
/// own currency, under the same rule as a flat promotion. Storing the discount here rather than a
/// bare number that the promotion gives units to means a row read on its own still means something,
/// which is the same reason <see cref="Money"/> exists at all.
/// </para>
/// <para>
/// <b>Tiers within one promotion must agree on kind</b> — all percentages or all amounts. Nothing
/// about resolution requires it: tiers are selected by quantity, not compared to each other, so a
/// mixed set is perfectly well-defined. It is refused because it is almost certainly a mistake, and
/// because a set that means "5% off at 10, three euros off at 24" is one nobody can sanity-check at
/// a glance. That rule spans rows, so the endpoint enforces it and the database does not — tiers are
/// replaced wholesale in one request, which is what makes the check local enough to be reliable.
/// </para>
/// <para>
/// <b>The lower bound is inclusive and there is no upper bound.</b> A tier is "N or more", and
/// resolution takes the highest threshold the quantity reaches — so tiers do not need to say where
/// they stop, and cannot leave a gap between them by disagreeing about it. The same instinct as the
/// half-open date window, one dimension over.
/// </para>
/// </remarks>
public sealed class PromotionTier : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid Id { get; private set; }

    public Guid PromotionId { get; private set; }

    /// <summary>Inclusive. This tier applies from this quantity upward.</summary>
    public int MinQuantity { get; private set; }

    /// <summary>Set for a percentage tier, null otherwise. In <c>0 &lt; p ≤ 100</c>.</summary>
    public decimal? PercentOff { get; private set; }

    /// <summary>Set for an amount tier, null otherwise. Positive.</summary>
    public decimal? AmountOff { get; private set; }

    /// <summary>ISO-4217, upper-cased. Set with <see cref="AmountOff"/> and null without it.</summary>
    public string? Currency { get; private set; }

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private PromotionTier() { } // EF

    public static PromotionTier Percentage(Guid promotionId, int minQuantity, decimal percentOff) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            PromotionId = promotionId,
            MinQuantity = minQuantity,
            PercentOff = percentOff,
        };

    public static PromotionTier Amount(
        Guid promotionId, int minQuantity, decimal amountOff, string currency) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            PromotionId = promotionId,
            MinQuantity = minQuantity,
            AmountOff = amountOff,
            Currency = currency.ToUpperInvariant(),
        };
}
