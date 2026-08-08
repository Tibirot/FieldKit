using System.Globalization;

namespace FieldKit.SharedKernel;

/// <summary>
/// A money amount in a single ISO-4217 currency. Decimal, never float.
/// No implicit cross-currency arithmetic (BR-PRD-1); operations across currencies throw.
/// </summary>
public readonly record struct Money
{
    public decimal Amount { get; }

    /// <summary>Upper-cased ISO-4217 alphabetic code, e.g. "EUR".</summary>
    public string Currency { get; }

    public Money(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new ArgumentException("Currency must be a 3-letter ISO-4217 code.", nameof(currency));

        Amount = amount;
        Currency = currency.ToUpperInvariant();
    }

    public static Money Zero(string currency) => new(0m, currency);

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, Currency);
    }

    public Money Multiply(decimal factor) => new(Amount * factor, Currency);

    /// <summary>
    /// How many decimal places this currency's minor unit has (ISO 4217).
    /// </summary>
    /// <remarks>
    /// Two for almost everything, so the table is the exceptions rather than the world: the
    /// zero-decimal currencies, where a "minor unit" does not exist, and the three-decimal ones.
    /// A currency nobody has listed is treated as two, which is right far more often than it is
    /// wrong and is what the previous hard-coded 2 assumed for every currency including these.
    /// <para>
    /// Not read from <see cref="System.Globalization.RegionInfo"/>: that maps *regions* to
    /// currencies, so it answers a different question, needs a region to ask about, and moves with
    /// the host's ICU version — none of which a rule the TypeScript mirror has to reproduce exactly
    /// can afford.
    /// </para>
    /// </remarks>
    public int MinorUnits => Currency switch
    {
        // Zero-decimal: the amount is the whole unit and rounding to 2 would invent a fraction of a
        // yen that no invoice can express.
        "BIF" or "CLP" or "DJF" or "GNF" or "ISK" or "JPY" or "KMF" or "KRW" or "PYG" or "RWF"
            or "UGX" or "UYI" or "VND" or "VUV" or "XAF" or "XOF" or "XPF" => 0,

        // Three-decimal.
        "BHD" or "IQD" or "JOD" or "KWD" or "LYD" or "OMR" or "TND" => 3,

        _ => 2,
    };

    /// <summary>
    /// Round half-up (away from zero) to the currency's minor units — the documented rounding
    /// policy (BR-PRD-9). The TypeScript device mirror must apply the identical policy.
    /// </summary>
    /// <remarks>
    /// The parameter exists for a caller that genuinely means a different scale; leaving it out is
    /// the rule. It used to default to 2, which read as the rule and was one for EUR and RON and
    /// wrong for JPY and KWD — a disagreement between this summary and this method that only a
    /// tenant in the wrong currency would ever have found.
    /// </remarks>
    public Money Round(int? decimals = null) =>
        new(Math.Round(Amount, decimals ?? MinorUnits, MidpointRounding.AwayFromZero), Currency);

    public static Money operator +(Money left, Money right) => left.Add(right);
    public static Money operator -(Money left, Money right) => left.Subtract(right);
    public static Money operator *(Money money, decimal factor) => money.Multiply(factor);

    private void EnsureSameCurrency(Money other)
    {
        if (Currency != other.Currency)
            throw new InvalidOperationException(
                $"Cannot operate on different currencies: {Currency} vs {other.Currency}.");
    }

    public override string ToString() =>
        $"{Amount.ToString("0.####", CultureInfo.InvariantCulture)} {Currency}";
}
