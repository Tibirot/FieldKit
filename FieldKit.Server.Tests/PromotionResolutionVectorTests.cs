using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FieldKit.Modules.Products;

namespace FieldKit.Server.Tests;

/// <summary>
/// Runs the shared promotion vectors against the C# engine (<c>PRD-06</c>, <c>PRD-08</c>) — W6
/// slice 12.
/// </summary>
/// <remarks>
/// The second file in <c>vectors/pricing</c>, in the format
/// <see cref="PriceResolutionVectorTests"/> established — which is the format paying off: a new
/// engine got a new case file rather than a new convention, and W7's mirror learns one reader for
/// both.
/// </remarks>
public class PromotionResolutionVectorTests
{
    // Declared before File, and it has to stay that way — see PriceResolutionVectorTests for the
    // failure this ordering avoids (null options, case-sensitive matching, an empty file, and a
    // Theory that silently runs nothing).
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(), new DecimalStringConverter() },
    };

    private static readonly VectorFile File = Load();

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var vector in File.Cases) data.Add(vector.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void The_engine_agrees_with_the_shared_vectors(string name)
    {
        var vector = File.Cases.Single(c => c.Name == name);

        var actual = PromotionResolver.Resolve(
            vector.Candidates, vector.Quantity, vector.On);

        if (vector.Expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(vector.Expected.PromotionId, actual.PromotionId);
        Assert.Equal(vector.Expected.Type, actual.Type);
        Assert.Equal(vector.Expected.Priority, actual.Priority);
        Assert.Equal(vector.Expected.Currency, actual.Currency);

        // Value and scale both, as in the price vectors: 12.5000 and 12.50 are the same quantity but
        // not the same answer to hand a caller, and the file records the scale on purpose.
        AssertSame(vector.Expected.PercentOff, actual.PercentOff);
        AssertSame(vector.Expected.AmountOff, actual.AmountOff);

        Assert.Equal(vector.Expected.Bundle, actual.Bundle);
    }

    [Fact]
    public void The_vector_file_is_the_one_the_mirror_will_read()
    {
        Assert.Equal(1, File.Version);
        Assert.True(File.Cases.Count >= 18, $"only {File.Cases.Count} vectors loaded");
        Assert.Equal(File.Cases.Count, File.Cases.Select(c => c.Name).Distinct().Count());
    }

    private static void AssertSame(decimal? expected, decimal? actual)
    {
        Assert.Equal(expected, actual);
        Assert.Equal(
            expected?.ToString(CultureInfo.InvariantCulture),
            actual?.ToString(CultureInfo.InvariantCulture));
    }

    private static VectorFile Load()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "vectors", "pricing", "promotion-resolution.v1.json");

        return JsonSerializer.Deserialize<VectorFile>(System.IO.File.ReadAllText(path), Options)
               ?? throw new InvalidOperationException($"{path} deserialized to null");
    }

    private sealed record VectorFile(int Version, IReadOnlyList<Vector> Cases);

    private sealed record Vector(
        string Name,
        DateOnly On,
        int Quantity,
        List<PromotionCandidate> Candidates,
        ResolvedPromotion? Expected);

    /// <summary>
    /// Reads a decimal from a JSON string, and refuses a JSON number.
    /// </summary>
    /// <remarks>
    /// The same converter <see cref="PriceResolutionVectorTests"/> uses, for the same reason: the
    /// string rule in <c>vectors/README.md</c> exists so <c>JSON.parse</c> in the mirror cannot make
    /// a float of a value before the engine sees it, and a rule that lives only in a README is one
    /// somebody edits away while adding a case.
    /// <para>
    /// Duplicated rather than shared, because the two suites are the two independent readers this
    /// format is meant to survive. A helper they both call would be a third thing to keep honest, and
    /// the day it changed both suites would agree about something wrong.
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
