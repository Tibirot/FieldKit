using System.Text.Json;
using System.Text.Json.Serialization;
using FieldKit.Modules.Products;

namespace FieldKit.Server.Tests;

/// <summary>
/// Runs the shared pricing vectors against the C# engine (<c>PRD-04</c>, <c>PRD-08</c>) — W6 slice 7.
/// </summary>
/// <remarks>
/// <para>
/// The cases live in <c>vectors/pricing/price-resolution.v1.json</c>, outside this project, because
/// W7's TypeScript mirror runs the same file. This class is one of two readers, not the owner. See
/// <c>vectors/README.md</c> for the format and why money is a string in it.
/// </para>
/// <para>
/// No fixture, no collection, no database: <see cref="PriceResolver"/> is pure (<c>BR-PRD-7</c>) and
/// so is its test. A pricing suite that needed Postgres to run would not be runnable in the place the
/// rules also have to hold — a phone with no signal.
/// </para>
/// </remarks>
public class PriceResolutionVectorTests
{
    // Declared before File, and it has to stay that way: static initializers run in source order, and
    // File = Load() reading a null Options deserializes case-sensitively against camelCase JSON —
    // which yields an empty VectorFile rather than an error, and a Theory with no cases is a green
    // test that ran nothing. The_vector_file_is_the_one_the_mirror_will_read is the backstop.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(), new MoneyStringConverter() },
    };

    private static readonly VectorFile File = Load();

    /// <summary>
    /// Case names, so xUnit reports "a tie on scope and date is broken by…" rather than an index.
    /// </summary>
    /// <remarks>
    /// Names rather than whole cases because xUnit has to serialize member data to identify a test,
    /// and a name is both serializable and the thing worth reading in a failure.
    /// </remarks>
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

        var actual = PriceResolver.Resolve(vector.Candidates, vector.On);

        if (vector.Expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(vector.Expected.PriceListId, actual.PriceListId);
        Assert.Equal(vector.Expected.Scope, actual.Scope);
        Assert.Equal(vector.Expected.Currency, actual.Currency);

        // Both the value and its scale: 12.5000 and 12.50 are equal as decimals but are not the same
        // answer to give a caller, and the vector file records the scale on purpose (BR-PRD-8).
        Assert.Equal(vector.Expected.Amount, actual.Amount);
        Assert.Equal(
            vector.Expected.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            actual.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void The_vector_file_is_the_one_the_mirror_will_read()
    {
        // Guards the wiring rather than the engine. If the Content glob stops copying the file, or the
        // file is emptied, every Theory above silently becomes zero tests — a green suite that checks
        // nothing. This is the assertion that goes red instead.
        Assert.Equal(1, File.Version);
        Assert.True(File.Cases.Count >= 15, $"only {File.Cases.Count} vectors loaded");
        Assert.Equal(File.Cases.Count, File.Cases.Select(c => c.Name).Distinct().Count());
    }

    private static VectorFile Load()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "vectors", "pricing", "price-resolution.v1.json");

        return JsonSerializer.Deserialize<VectorFile>(System.IO.File.ReadAllText(path), Options)
               ?? throw new InvalidOperationException($"{path} deserialized to null");
    }

    private sealed record VectorFile(int Version, IReadOnlyList<Vector> Cases);

    private sealed record Vector(
        string Name, DateOnly On, List<PriceCandidate> Candidates, ResolvedPrice? Expected);

    /// <summary>Reads a decimal from a JSON string, and refuses a JSON number.</summary>
    /// <remarks>
    /// The refusal is the point. <c>vectors/README.md</c> says money is a string so that
    /// <c>JSON.parse</c> in the mirror cannot turn it into a float before the engine sees it — but a
    /// rule that only lives in a README is a rule someone edits away while adding a case. Rejecting
    /// the number token makes the format enforce itself, in the language that is strictest about it.
    /// </remarks>
    private sealed class MoneyStringConverter : JsonConverter<decimal>
    {
        public override decimal Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.String => decimal.Parse(
                    reader.GetString()!,
                    System.Globalization.NumberStyles.AllowDecimalPoint
                    | System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture),
                JsonTokenType.Number => throw new JsonException(
                    "Money in a vector file must be a string — see vectors/README.md."),
                _ => throw new JsonException($"unexpected {reader.TokenType} for an amount"),
            };

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
