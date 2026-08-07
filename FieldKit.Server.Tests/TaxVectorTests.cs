using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FieldKit.Modules.Products;
using FieldKit.SharedKernel;

namespace FieldKit.Server.Tests;

/// <summary>
/// Runs the shared tax vectors against the C# engine (<c>PRD-07</c>, <c>PRD-08</c>) — W6 slice 13.
/// </summary>
/// <remarks>
/// The third file in <c>vectors/pricing</c>, and the one the format was really built for.
/// <c>BR-PRD-9</c>'s rounding policy is where a JavaScript mirror and <c>System.Decimal</c> diverge
/// most easily, and a cent of disagreement on a VAT line is a reconciliation problem someone chases
/// through a ledger.
/// <para>
/// Two engines in one file, because they are two halves of one rule: which rate, and what it does.
/// Splitting them would have meant two files whose cases nobody reads together.
/// </para>
/// </remarks>
public class TaxVectorTests
{
    // Declared before File — see PriceResolutionVectorTests for the failure this ordering avoids.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(), new DecimalStringConverter() },
    };

    private static readonly VectorFile File = Load();

    public static TheoryData<string> ResolutionCases()
    {
        var data = new TheoryData<string>();
        foreach (var vector in File.Resolution) data.Add(vector.Name);
        return data;
    }

    public static TheoryData<string> ApplicationCases()
    {
        var data = new TheoryData<string>();
        foreach (var vector in File.Application) data.Add(vector.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ResolutionCases))]
    public void Rate_selection_agrees_with_the_shared_vectors(string name)
    {
        var vector = File.Resolution.Single(c => c.Name == name);

        var actual = TaxEngine.Resolve(vector.Candidates, vector.On);

        if (vector.Expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(vector.Expected.TaxRateId, actual.TaxRateId);
        AssertSame(vector.Expected.Percentage, actual.Percentage);
    }

    [Theory]
    [MemberData(nameof(ApplicationCases))]
    public void Rounding_agrees_with_the_shared_vectors(string name)
    {
        var vector = File.Application.Single(c => c.Name == name);

        var actual = TaxEngine.Apply(new Money(vector.Net, vector.Currency), vector.Percentage);

        AssertSame(vector.Expected.Net, actual.Net.Amount);
        AssertSame(vector.Expected.Tax, actual.Tax.Amount);
        AssertSame(vector.Expected.Gross, actual.Gross.Amount);

        Assert.Equal(vector.Currency, actual.Net.Currency);
        Assert.Equal(vector.Currency, actual.Tax.Currency);
        Assert.Equal(vector.Currency, actual.Gross.Currency);
    }

    [Fact]
    public void The_three_numbers_always_add_up()
    {
        // A property over every application case rather than a case of its own: an invoice shows net,
        // tax and gross, and a customer adds the first two. Any rounding scheme that computes gross
        // independently — net * 1.19, say — can break this on a case nobody wrote down.
        foreach (var vector in File.Application)
        {
            var actual = TaxEngine.Apply(new Money(vector.Net, vector.Currency), vector.Percentage);

            Assert.Equal(actual.Gross.Amount, actual.Net.Amount + actual.Tax.Amount);
        }
    }

    [Fact]
    public void The_vector_file_is_the_one_the_mirror_will_read()
    {
        Assert.Equal(1, File.Version);
        Assert.True(File.Resolution.Count >= 9, $"only {File.Resolution.Count} resolution vectors");
        Assert.True(File.Application.Count >= 10, $"only {File.Application.Count} application vectors");

        Assert.Equal(
            File.Resolution.Count, File.Resolution.Select(c => c.Name).Distinct().Count());

        Assert.Equal(
            File.Application.Count, File.Application.Select(c => c.Name).Distinct().Count());
    }

    private static void AssertSame(decimal expected, decimal actual)
    {
        Assert.Equal(expected, actual);
        Assert.Equal(
            expected.ToString(CultureInfo.InvariantCulture),
            actual.ToString(CultureInfo.InvariantCulture));
    }

    private static VectorFile Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vectors", "pricing", "tax.v1.json");

        return JsonSerializer.Deserialize<VectorFile>(System.IO.File.ReadAllText(path), Options)
               ?? throw new InvalidOperationException($"{path} deserialized to null");
    }

    private sealed record VectorFile(
        int Version,
        IReadOnlyList<ResolutionVector> Resolution,
        IReadOnlyList<ApplicationVector> Application);

    private sealed record ResolutionVector(
        string Name, DateOnly On, List<TaxRateCandidate> Candidates, ExpectedRate? Expected);

    private sealed record ExpectedRate(Guid TaxRateId, decimal Percentage);

    private sealed record ApplicationVector(
        string Name, decimal Net, string Currency, decimal Percentage, ExpectedAmount Expected);

    private sealed record ExpectedAmount(decimal Net, decimal Tax, decimal Gross);

    /// <summary>
    /// Reads a decimal from a JSON string, and refuses a JSON number.
    /// </summary>
    /// <remarks>
    /// The third copy, and deliberately so — see <c>PromotionResolutionVectorTests</c> for the
    /// argument. Here it matters most: this file exists to prove decimal behaviour, and a bare
    /// <c>12.99</c> would become a float in the mirror's <c>JSON.parse</c> before its engine ever saw
    /// it, so the suite would be checking that both languages make the same rounding error.
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
