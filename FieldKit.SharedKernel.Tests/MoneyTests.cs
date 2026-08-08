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

    [Theory]
    [InlineData("EUR", 2)]
    [InlineData("RON", 2)]
    [InlineData("USD", 2)]
    [InlineData("JPY", 0)]
    [InlineData("KRW", 0)]
    [InlineData("KWD", 3)]
    [InlineData("BHD", 3)]
    [InlineData("ZZZ", 2)]
    public void Minor_units_come_from_the_currency(string currency, int expected)
    {
        Assert.Equal(expected, new Money(1m, currency).MinorUnits);
    }

    [Fact]
    public void Round_without_a_scale_uses_the_currency_rather_than_two()
    {
        // BR-PRD-9 says "the currency's minor units", and for most of the world that is two — but a
        // yen has none and a dinar has three. Rounding 1234.5 JPY to 1234.50 invents a fraction of a
        // unit no invoice can express, and truncating 1.2345 KWD to 1.23 loses a fils.
        Assert.Equal(1235m, new Money(1234.5m, "JPY").Round().Amount);
        Assert.Equal(1.235m, new Money(1.2345m, "KWD").Round().Amount);

        // Unchanged for the currencies this project actually ships with.
        Assert.Equal(2.13m, new Money(2.125m, "EUR").Round().Amount);
        Assert.Equal(2.13m, new Money(2.125m, "RON").Round().Amount);
    }

    [Fact]
    public void Round_still_takes_an_explicit_scale_when_a_caller_means_one()
    {
        Assert.Equal(1234.5m, new Money(1234.45m, "JPY").Round(1).Amount);
    }

    [Fact]
    public void Equality_is_by_value()
    {
        Assert.Equal(new Money(5m, "EUR"), new Money(5m, "EUR"));
        Assert.NotEqual(new Money(5m, "EUR"), new Money(5m, "USD"));
    }
}
