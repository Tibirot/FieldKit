using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FieldKit.Modules.Products;
using FieldKit.SharedKernel;

namespace FieldKit.Server.Tests;

/// <summary>
/// Runs the shared line vectors against the C# calculator (<c>ORD-02</c>, <c>ORD-03</c>,
/// <c>PRD-08</c>) — W11 slice 2a.
/// </summary>
/// <remarks>
/// <para>
/// The fifth file in <c>vectors/</c> and the first that is not a <i>resolver</i>. The others pin
/// which price, which promotion, which rate; this pins the arithmetic that turns those into money —
/// which is where a device and a server most easily disagree by a cent, and a cent on an order is a
/// reconciliation someone chases through a ledger.
/// </para>
/// <para>
/// The TypeScript mirror reads this same file in slice 2b. Until it does, this is a corpus with one
/// reader — which is worth saying out loud, because a vector file's value is entirely in being read
/// twice. <c>scripts/check-vector-readers.mjs</c> is what will refuse the imbalance.
/// </para>
/// </remarks>
public class LinePricingVectorTests
{
    // Declared before File — see PriceResolutionVectorTests for the ordering failure this avoids.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(), new DecimalStringConverter() },
    };

    private static readonly VectorFile File = Load();

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var vector in File.Cases) data.Add(vector.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_line_agrees_with_the_shared_vectors(string name)
    {
        var vector = File.Cases.Single(c => c.Name == name);

        var actual = LinePricing.Price(
            new Money(vector.UnitPrice, vector.Currency),
            vector.Quantity,
            Promotion(vector.Promotion),
            vector.TaxPercentage);

        AssertSame(vector.Expected.Subtotal, actual.Subtotal.Amount);
        AssertSame(vector.Expected.Discount, actual.Discount.Amount);
        AssertSame(vector.Expected.Net, actual.Net.Amount);
        AssertSame(vector.Expected.Tax, actual.Tax.Amount);
        AssertSame(vector.Expected.Total, actual.Total.Amount);

        Assert.Equal(vector.Currency, actual.Total.Currency);
    }

    [Fact]
    public void The_four_numbers_always_add_up()
    {
        /*
         * A property over every case rather than a case of its own, and the one an order document
         * depends on: a reader adding the printed net and the printed tax must reach the printed
         * total, and subtracting the printed discount from the printed subtotal must reach the net.
         *
         * Any scheme that carries full precision between steps and rounds once at the end can break
         * this on a case nobody wrote down.
         */
        foreach (var vector in File.Cases)
        {
            var actual = LinePricing.Price(
                new Money(vector.UnitPrice, vector.Currency),
                vector.Quantity,
                Promotion(vector.Promotion),
                vector.TaxPercentage);

            Assert.Equal(actual.Net.Amount, actual.Subtotal.Amount - actual.Discount.Amount);
            Assert.Equal(actual.Total.Amount, actual.Net.Amount + actual.Tax.Amount);
        }
    }

    [Fact]
    public void No_case_ever_drives_a_line_negative()
    {
        // The clamp, as a property. A promotion authored larger than the line it targets is an
        // ordinary authoring accident, and the answer is a free line rather than a refund.
        foreach (var vector in File.Cases)
        {
            var actual = LinePricing.Price(
                new Money(vector.UnitPrice, vector.Currency),
                vector.Quantity,
                Promotion(vector.Promotion),
                vector.TaxPercentage);

            Assert.True(actual.Net.Amount >= 0m, $"{vector.Name} drove the net negative");
            Assert.True(actual.Discount.Amount <= actual.Subtotal.Amount, vector.Name);
        }
    }

    [Fact]
    public void The_vector_file_is_the_one_the_mirror_will_read()
    {
        Assert.Equal(1, File.Version);
        Assert.True(File.Cases.Count >= 10, $"only {File.Cases.Count} line vectors");
        Assert.Equal(File.Cases.Count, File.Cases.Select(c => c.Name).Distinct().Count());

        // At least one case in a currency with no minor unit. Without it the whole corpus can be
        // satisfied by arithmetic hard-coded to two decimals, which is exactly the bug the
        // Money.Round summary records having shipped once already.
        Assert.Contains(File.Cases, c => c.Currency == "JPY");

        // …and at least one of each promotion shape, so a mirror cannot pass by implementing two.
        foreach (var kind in new[] { "percentOff", "amountOff", "bundle" })
        {
            Assert.Contains(File.Cases, c => c.Promotion?.Kind == kind);
        }
    }

    private static ResolvedPromotion? Promotion(PromotionVector? vector) => vector?.Kind switch
    {
        null => null,

        "percentOff" => new ResolvedPromotion(
            Guid.Empty, PromotionType.PercentOff, 0, vector!.PercentOff, null, null, null),

        "amountOff" => new ResolvedPromotion(
            Guid.Empty, PromotionType.FixedAmountOff, 0, null, vector!.AmountOff, vector.Currency, null),

        "bundle" => new ResolvedPromotion(
            Guid.Empty,
            PromotionType.BuyXGetY,
            0,
            null,
            null,
            null,
            new BundleCandidate(
                vector!.BuyQuantity!.Value,
                vector.GetQuantity!.Value,
                vector.GetPercentOff!.Value,
                vector.GetProductId)),

        _ => throw new InvalidOperationException($"unknown promotion kind '{vector.Kind}'"),
    };

    private static void AssertSame(decimal expected, decimal actual)
    {
        Assert.Equal(expected, actual);
        Assert.Equal(
            expected.ToString(CultureInfo.InvariantCulture),
            actual.ToString(CultureInfo.InvariantCulture));
    }

    private static VectorFile Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vectors", "pricing", "line.v1.json");

        return JsonSerializer.Deserialize<VectorFile>(System.IO.File.ReadAllText(path), Options)
               ?? throw new InvalidOperationException($"{path} deserialized to null");
    }

    private sealed record VectorFile(int Version, IReadOnlyList<LineVector> Cases);

    private sealed record LineVector(
        string Name,
        decimal UnitPrice,
        string Currency,
        decimal Quantity,
        PromotionVector? Promotion,
        decimal? TaxPercentage,
        ExpectedLine Expected);

    private sealed record PromotionVector(
        string Kind,
        decimal? PercentOff,
        decimal? AmountOff,
        string? Currency,
        int? BuyQuantity,
        int? GetQuantity,
        decimal? GetPercentOff,
        Guid? GetProductId);

    private sealed record ExpectedLine(
        decimal Subtotal, decimal Discount, decimal Net, decimal Tax, decimal Total);

    /// <summary>
    /// Money and percentages are strings in a vector file; a JSON number is refused.
    /// </summary>
    /// <remarks>
    /// A number would be parsed by the mirror's <c>JSON.parse</c> into a float before either engine
    /// saw it, so the suite would be checking that both languages make the same rounding error.
    /// <para>
    /// <b>Third copy</b>, after <c>TaxVectorTests</c> and <c>PromotionResolutionVectorTests</c> —
    /// each is a private nested class in the file that reads its own corpus. Worth extracting at the
    /// next one; three identical fifteen-line converters is the point where a shared
    /// <c>VectorJson</c> helper stops being premature.
    /// </para>
    /// </remarks>
    private sealed class DecimalStringConverter : JsonConverter<decimal>
    {
        public override decimal Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.String => decimal.Parse(
                    reader.GetString()!,
                    NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture),
                JsonTokenType.Number => throw new JsonException(
                    "A value in a vector file must be a string — see vectors/README.md."),
                _ => throw new JsonException($"unexpected {reader.TokenType} for a decimal"),
            };

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
