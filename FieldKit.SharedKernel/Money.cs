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
    /// Round half-up (away from zero) to the currency's minor units — the documented rounding
    /// policy (BR-PRD-9). The TypeScript device mirror must apply the identical policy.
    /// </summary>
    public Money Round(int decimals = 2) =>
        new(Math.Round(Amount, decimals, MidpointRounding.AwayFromZero), Currency);

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
