using System.Text.Json;
using System.Text.Json.Serialization;
using FieldKit.Modules.Audit;
using FieldKit.Modules.Audit.Contracts;
using FieldKit.Modules.Configuration.Contracts;

namespace FieldKit.Server.Tests;

/// <summary>
/// The shared perfect-store vectors, run against the C# engine (<c>AUD-06</c>, <c>BR-AUD-5</c>,
/// <c>BR-AUD-12</c>) — W10 slice 5.
/// </summary>
/// <remarks>
/// <para>
/// The same files <c>frontend/lib/audits/score.test.ts</c> reads, from the same path — neither side
/// copies them. <see cref="PerfectStoreScoreTests"/> is where the rules are argued; this is where the
/// two languages are made to agree about them.
/// </para>
/// <para>
/// <b>Only the hand-written file runs here.</b> The generated one's expectations came out of this
/// engine, so running them back against it would confirm a bug rather than find one — it is an
/// oracle for the mirror, which is why the TypeScript side runs an order of magnitude more cases.
/// <see cref="VectorGenerator"/> makes the same point at length.
/// </para>
/// </remarks>
public class PerfectStoreScoreVectorTests
{
    /// <summary>
    /// Refuses a decimal that arrived as a JSON number.
    /// </summary>
    /// <remarks>
    /// The format rule <c>vectors/README.md</c> exists for. <c>JSON.parse</c> in the mirror would
    /// turn a bare <c>82.86</c> into a float <b>before the engine saw it</b>, and the suite would then
    /// be comparing two rounding errors rather than proving decimal behaviour. C# refuses the number
    /// token here; TypeScript refuses it in a test — same rule, both sides of the contract.
    /// </remarks>
    private sealed class StrictDecimalConverter : JsonConverter<decimal?>
    {
        public override decimal? Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;

            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException(
                    "Decimals in a vector file must be JSON strings — a bare number is parsed as a "
                        + "float by the TypeScript mirror before the engine ever sees it.");
            }

            return decimal.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        }

        public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options) =>
            throw new NotSupportedException("These files are read, never written by this converter.");
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new StrictDecimalConverter(), new JsonStringEnumConverter() },
    };

    private sealed record AvailabilityCase(AvailabilityStatus Status);

    private sealed record FacingsCase(int Facings);

    private sealed record PriceCase(long ObservedMinorUnits, long? ExpectedMinorUnits);

    private sealed record WeightCase(ScorePillar Pillar, decimal? Percentage);

    private sealed record PillarExpectation(ScorePillar Pillar, decimal? Percentage, decimal? Weight);

    private sealed record Expectation(decimal? Score, IReadOnlyList<PillarExpectation> Pillars);

    private sealed record ScoreCase(
        string Name,
        IReadOnlyList<AvailabilityCase> Availability,
        IReadOnlyList<FacingsCase> Facings,
        int? CategoryFacings,
        IReadOnlyList<PriceCase> Prices,
        IReadOnlyList<WeightCase> Weights,
        long PriceToleranceMinorUnits,
        Expectation Expected);

    private sealed record VectorFile(int Version, IReadOnlyList<ScoreCase> Cases);

    private static VectorFile Load(string file)
    {
        var path = Path.Combine(GeneratedVectorTests.VectorsDirectory(), "audits", file);

        return JsonSerializer.Deserialize<VectorFile>(File.ReadAllText(path), Options)!;
    }

    public static TheoryData<string> HandWritten()
    {
        var data = new TheoryData<string>();
        foreach (var vector in Load("perfect-store.v1.json").Cases) data.Add(vector.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(HandWritten))]
    public void The_engine_agrees_with_the_hand_written_vectors(string name)
    {
        var vector = Load("perfect-store.v1.json").Cases.Single(candidate => candidate.Name == name);

        var result = PerfectStoreScore.Compute(new ScoreInputs(
            [.. vector.Availability.Select(line => new AvailabilityLine(Guid.Empty, line.Status))],
            [.. vector.Facings.Select(line => new FacingsLine(Guid.Empty, line.Facings))],
            vector.CategoryFacings,
            [.. vector.Prices.Select(line =>
                new PriceLine(Guid.Empty, line.ObservedMinorUnits, line.ExpectedMinorUnits, "RON"))],
            [.. vector.Weights.Select(weight => new PillarWeight(weight.Pillar, weight.Percentage!.Value))],
            vector.PriceToleranceMinorUnits));

        Assert.Equal(vector.Expected.Score, result.Score);

        /*
         * The pillar breakdown is compared too, not only the total.
         *
         * Two engines can agree on a score while disagreeing about how they got there — a
         * compensating pair of errors, or a different rounding point that happens to cancel. The
         * breakdown is also what AUD-09 renders, so it is part of the contract rather than working
         * out.
         */
        Assert.Equal(
            vector.Expected.Pillars.Select(pillar => pillar.Pillar),
            result.Pillars.Select(pillar => pillar.Pillar));

        Assert.Equal(
            vector.Expected.Pillars.Select(pillar => pillar.Percentage),
            result.Pillars.Select(pillar => pillar.Percentage));

        Assert.Equal(
            vector.Expected.Pillars.Select(pillar => pillar.Weight),
            result.Pillars.Select(pillar => (decimal?)pillar.Weight));
    }
}
