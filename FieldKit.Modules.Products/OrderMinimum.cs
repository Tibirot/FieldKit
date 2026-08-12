using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products;

/// <summary>
/// The smallest order a shop may place (<c>ORD-06</c>, <c>BR-ORD-5</c>) — W11 slice 8b-i.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per channel with a per-outlet override</b>, which is not a choice made here: <c>B1</c> says
/// "optional minimum order value per channel/outlet", and it is the third rule in this module to take
/// that shape after price list assignment and the assortment. Reusing it means the precedence a
/// reader already knows applies again, rather than a third scoping scheme to learn.
/// </para>
/// <para>
/// <b>It lives in Products, not Configuration.</b> An earlier note in the plan guessed otherwise, and
/// <c>B1</c>'s own "affects" line settles it: this is commercial policy keyed on a channel, like a
/// price list, and Configuration owns *shape* — custom fields, workflows, forms, weights, all
/// tenant-wide. A per-channel threshold is not that kind of thing, and putting it there would have
/// meant building the channel/outlet override machinery a second time in a module that has none.
/// </para>
/// <para>
/// <b>The minimum carries a currency, and comparing across two is refused rather than coerced.</b>
/// An order's currency comes from the price list that priced it (<c>BR-ORD-7</c>); a minimum
/// authored in EUR against an order in RON is a misconfiguration, and <see cref="Money"/> already
/// refuses arithmetic across currencies. Storing a bare number instead would make that comparison
/// silently succeed and refuse orders for the wrong reason — the sort of wrong that reads as a rule
/// working.
/// </para>
/// <para>
/// <b>Value only, and <c>BR-ORD-5</c> says "value/qty".</b> That is a genuine disagreement between the
/// rule and <c>B1</c>, which assumes value alone. Value is the narrower reading and the one the
/// decision ledger actually made, so it is what ships; a quantity minimum would need its own decision
/// about what it counts — units, cases, or lines — and none of those is written down.
/// </para>
/// </remarks>
public sealed class OrderMinimum : AggregateRoot, ITenantOwned, IAuditable, ISyncTracked
{
    /// <summary>Set by the row-version interceptor, never here (ADR-0013).</summary>
    public long RowVersion { get; set; }

    public Guid Id { get; private set; }

    /// <summary>Set when this applies to a whole channel. Null when <see cref="OutletId"/> is set.</summary>
    public Guid? ChannelId { get; private set; }

    /// <summary>Set when this applies to one outlet. Null when <see cref="ChannelId"/> is set.</summary>
    public Guid? OutletId { get; private set; }

    /// <summary>What an order must reach, in <see cref="CurrencyCode"/>.</summary>
    public decimal Amount { get; private set; }

    /// <summary>ISO-4217. The order's own currency has to match for the rule to mean anything.</summary>
    public string CurrencyCode { get; private set; } = null!;

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private OrderMinimum() { } // EF

    public static OrderMinimum ForChannel(Guid channelId, decimal amount, string currencyCode) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ChannelId = channelId,
            Amount = amount,
            CurrencyCode = currencyCode.ToUpperInvariant(),
        };

    public static OrderMinimum ForOutlet(Guid outletId, decimal amount, string currencyCode) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            OutletId = outletId,
            Amount = amount,
            CurrencyCode = currencyCode.ToUpperInvariant(),
        };
}
