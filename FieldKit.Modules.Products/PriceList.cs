using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products;

/// <summary>
/// A set of prices in one currency, valid for a window of time (<c>PRD-03</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>One currency per list, and it lives here rather than on each price</b> (<c>BR-PRD-1</c>). A
/// list whose lines could each carry their own currency is a list that cannot be summed, compared or
/// discounted without asking what the numbers mean — and the moment two lines disagree, every rule
/// keyed to "the price" has to decide which currency it is working in. Holding it once makes
/// cross-currency arithmetic not merely forbidden but unrepresentable: a line has an amount, and the
/// list says what that amount is.
/// </para>
/// <para>
/// <b>The window is half-open: <c>[EffectiveFrom, EffectiveTo)</c>.</b> An inclusive end makes
/// "ends on the 31st" and "starts on the 1st" leave a gap or an overlap depending on how someone
/// reads it, and price resolution then has to break a tie that should never have existed. Half-open
/// means the successor list starts on exactly the instant the old one stops.
/// </para>
/// <para>
/// <b>Dates, not instants.</b> A price list runs from a business day, and a business day means
/// different instants in different places — an outlet's timezone decides when "the 1st" begins
/// there (<c>BR-PRD-6</c> makes the same point for promotions). Storing an instant would freeze one
/// place's midnight into a rule every other place has to live with.
/// </para>
/// </remarks>
public sealed class PriceList : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid Id { get; private set; }

    public string Name { get; private set; } = null!;

    /// <summary>ISO-4217, upper-cased. Every line in this list is in it.</summary>
    public string Currency { get; private set; } = null!;

    public DateOnly EffectiveFrom { get; private set; }

    /// <summary>Exclusive. Null means open-ended — the list applies until something replaces it.</summary>
    public DateOnly? EffectiveTo { get; private set; }

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private PriceList() { } // EF

    public static PriceList Create(string name, string currency, DateOnly from, DateOnly? to) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Currency = currency.ToUpperInvariant(),
            EffectiveFrom = from,
            EffectiveTo = to,
        };

    /// <summary>
    /// Renames and re-dates. The currency is not here, deliberately.
    /// </summary>
    /// <remarks>
    /// Changing a list's currency would silently reinterpret every price in it — 12.50 EUR becoming
    /// 12.50 RON is not a conversion, it is a different number wearing the old one's clothes. A
    /// tenant that needs the same prices in another currency needs another list, priced for it.
    /// </remarks>
    public void Update(string name, DateOnly from, DateOnly? to, IClock clock)
    {
        Name = name;
        EffectiveFrom = from;
        EffectiveTo = to;
        ModifiedAtUtc = clock.UtcNow;
    }

    /// <summary>Whether this list covers <paramref name="date"/> — half-open, as above.</summary>
    public bool Covers(DateOnly date) =>
        date >= EffectiveFrom && (EffectiveTo is not { } end || date < end);

    /// <summary>Announces that this list's scope changed.</summary>
    /// <remarks>
    /// On the aggregate rather than at the endpoint, so the event goes to the outbox in the same
    /// transaction as the rows that caused it (ADR-0006). Raising it from the handler after
    /// <c>SaveChanges</c> would be a dual write: the scope committed and the announcement lost, or
    /// the announcement sent for a scope that rolled back.
    /// <para>
    /// Called even when the scope is set to nothing. "This list now reaches nobody" is a change a
    /// consumer needs as much as any other — it is how a list is withdrawn, and a device that never
    /// hears it keeps pricing against a list that no longer applies.
    /// </para>
    /// </remarks>
    public void Publish(int channelCount, int outletCount, IClock clock) =>
        Raise(new PriceListPublished(
            Guid.CreateVersion7(),
            clock.UtcNow,
            Id,
            Currency,
            EffectiveFrom,
            EffectiveTo,
            channelCount,
            outletCount));
}

/// <summary>
/// What one product costs in one price list (<c>PRD-03</c>).
/// </summary>
/// <remarks>
/// Stores the amount only; the currency comes from the list. Reassembled into a
/// <see cref="SharedKernel.Money"/> when it leaves the module, so nothing downstream ever handles a
/// bare decimal it has to remember the units of.
/// <para>
/// <b>Net of tax</b> (<c>BR-PRD-5</c>): tax is computed at order time from the product's tax class,
/// because the same product is taxed differently in different countries and a gross price would bake
/// one of them in.
/// </para>
/// </remarks>
public sealed class PriceListLine : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid Id { get; private set; }

    public Guid PriceListId { get; private set; }

    public Guid ProductId { get; private set; }

    /// <summary>Net, in the list's currency. See <see cref="PriceList.Currency"/>.</summary>
    public decimal Amount { get; private set; }

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private PriceListLine() { } // EF

    public static PriceListLine Create(Guid priceListId, Guid productId, decimal amount) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            PriceListId = priceListId,
            ProductId = productId,
            Amount = amount,
        };

    public void Reprice(decimal amount, IClock clock)
    {
        Amount = amount;
        ModifiedAtUtc = clock.UtcNow;
    }
}
