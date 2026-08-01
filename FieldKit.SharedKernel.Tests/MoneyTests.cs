using FieldKit.SharedKernel;

namespace FieldKit.SharedKernel.Tests;

public class MoneyTests
{
    [Fact]
    public void Add_same_currency_sums_the_amounts()
    {
        var result = new Money(10.50m, "EUR") + new Money(4.25m, "EUR");

        Assert.Equal(14.75m, result.Amount);
        Assert.Equal("EUR", result.Currency);
    }

    [Fact]
    public void Operating_across_currencies_throws()
    {
        var eur = new Money(10m, "EUR");
        var usd = new Money(10m, "USD");

        Assert.Throws<InvalidOperationException>(() => eur + usd);
    }

    [Fact]
    public void Currency_is_normalised_to_upper_case()
    {
        Assert.Equal("EUR", new Money(1m, "eur").Currency);
    }

    [Theory]
    [InlineData("")]
    [InlineData("EU")]
    [InlineData("EURO")]
    public void Invalid_currency_code_throws(string currency)
    {
        Assert.Throws<ArgumentException>(() => new Money(1m, currency));
    }

    [Fact]
    public void Round_uses_half_up_away_from_zero()
    {
        // 2.125 -> 2.13 (half-up), not 2.12 (banker's rounding).
        Assert.Equal(2.13m, new Money(2.125m, "EUR").Round(2).Amount);
    }

    [Fact]
    public void Equality_is_by_value()
    {
        Assert.Equal(new Money(5m, "EUR"), new Money(5m, "EUR"));
        Assert.NotEqual(new Money(5m, "EUR"), new Money(5m, "USD"));
    }
}
