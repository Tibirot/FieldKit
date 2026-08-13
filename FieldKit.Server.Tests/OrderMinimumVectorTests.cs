using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FieldKit.Modules.Products;
using FieldKit.SharedKernel;

namespace FieldKit.Server.Tests;

/// <summary>
/// Runs the shared order-minimum vectors against the C# engine (<c>ORD-06</c>, <c>BR-ORD-5</c>,
/// <c>PRD-08</c>'s regime) — W11½ R7.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule with the most to lose from disagreement, and it was the one with no shared file.</b>
/// Every other mirrored rule is checked again somewhere downstream — a price is recomputed when the
/// order is ingested, a score is recomputed against its weight version. <c>BR-ORD-5</c> is explicit
/// that this one has <i>no server-side gate</i>: the device refuses the submission because that is
/// where a rep can still add a line. So a divergence is not caught late, it is not caught at all.
/// </para>
/// <para>
/// <b>Resolution and the check are separate sections because they are separate questions.</b> Only
/// the first has a precedence story; the second is a comparison plus two ways of declining to make
/// one. Keeping them apart in the file is what lets the device show a rep the threshold before they
/// have added anything to the order.
/// </para>
/// <para>
/// <b>The <c>Unreadable</c> cases are the point of the file rather than its edges.</b> The two
/// engines parse the stored amount with different machinery — .NET with
/// <c>NumberStyles.AllowDecimalPoint | AllowLeadingSign</c>, the device with <c>decimal.js</c> — and
/// those two accept different strings. Writing this file found that <c>"1e2"</c> and <c>"0x10"</c>
/// were read as numbers on the device and as broken rows here, which would have had a phone call an
/// order <i>Met</i> against a minimum the server cannot read.
/// </para>
/// </remarks>
public class OrderMinimumVectorTests
{
    private static readonly VectorFile File = Load();

    public static TheoryData<string> ResolutionCases()
    {
        var data = new TheoryData<string>();
        foreach (var vector in File.Resolution) data.Add(vector.Name);
        return data;
    }

    public static TheoryData<string> CheckCases()
    {
        var data = new TheoryData<string>();
        foreach (var vector in File.Check) data.Add(vector.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ResolutionCases))]
    public void The_resolver_agrees_with_the_shared_vector(string name)
    {
        var vector = File.Resolution.Single(candidate => candidate.Name == name);

        var resolved = OrderMinimumResolver.Resolve(
            [.. vector.Candidates.Select(candidate => new OrderMinimumCandidate(
                Guid.Parse(candidate.OrderMinimumId),
                candidate.Scope,
                candidate.CurrencyCode,
                candidate.Amount))]);

        if (vector.Expected is null)
        {
            // "No minimum" is an answer, not an absence — a file of positive cases only would let a
            // resolver that always returns something pass.
            Assert.Null(resolved);
            return;
        }

        Assert.NotNull(resolved);
        Assert.Equal(Guid.Parse(vector.Expected.OrderMinimumId), resolved.OrderMinimumId);
        Assert.Equal(vector.Expected.Scope, resolved.Scope);
        Assert.Equal(vector.Expected.CurrencyCode, resolved.CurrencyCode);

        // Compared as the string it arrived as. Resolution *selects*; parsing here would mean this
        // suite could not tell "500.00" from "500", which is a difference the check section owns.
        Assert.Equal(vector.Expected.Amount, resolved.Amount);
    }

    [Theory]
    [MemberData(nameof(CheckCases))]
    public void The_check_agrees_with_the_shared_vector(string name)
    {
        var vector = File.Check.Single(candidate => candidate.Name == name);

        var minimum = vector.Minimum is null
            ? null
            // The id and scope play no part in the check, so the file does not carry them here —
            // a case that named them would imply they mattered.
            : new ResolvedOrderMinimum(
                Guid.Empty, OrderMinimumScope.Outlet, vector.Minimum.CurrencyCode, vector.Minimum.Amount);

        // The *order* total is always well formed — it is arithmetic this engine did, not a stored
        // string somebody typed — so it parses here rather than through the module's guarded parse.
        // The minimum's amount is the one under test, and it stays a string all the way in.
        var total = new Money(
            decimal.Parse(vector.Total.Amount, CultureInfo.InvariantCulture), vector.Total.Currency);

        Assert.Equal(vector.Expected, OrderMinimumResolver.Check(minimum, total));
    }

    [Fact]
    public void The_file_carries_the_version_this_suite_was_written_against()
    {
        // A file whose cases changed meaning bumps its version, so a suite running an older one
        // fails loudly rather than quietly proving yesterday's rule (vectors/README.md).
        Assert.Equal(1, File.Version);
    }

    [Fact]
    public void Every_amount_in_the_file_is_a_string()
    {
        /*
         * The format rule this file depends on, asserted rather than assumed — the same check the
         * TypeScript mirror makes, from the other side.
         *
         * A bare JSON number would be a float before either engine saw it, and the suite would be
         * comparing two rounding errors. Here it matters twice over: several cases carry amounts
         * that are *deliberately* not numbers, and a serializer that helpfully normalised them
         * would delete the divergence this file exists to pin.
         */
        using var document = JsonDocument.Parse(System.IO.File.ReadAllText(Path()));

        foreach (var section in new[] { "resolution", "check" })
        {
            foreach (var element in document.RootElement.GetProperty(section).EnumerateArray())
            {
                foreach (var amount in Amounts(element))
                {
                    Assert.Equal(JsonValueKind.String, amount.ValueKind);
                }
            }
        }
    }

    private static IEnumerable<JsonElement> Amounts(JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var nested in Amounts(property.Value)) yield return nested;
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    foreach (var nested in Amounts(item)) yield return nested;
                }
            }
            else if (property.NameEquals("amount"))
            {
                yield return property.Value;
            }
        }
    }

    private static string Path() =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "vectors", "pricing", "order-minimum.v1.json");

    private static VectorFile Load() =>
        JsonSerializer.Deserialize<VectorFile>(
            System.IO.File.ReadAllText(Path()),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                // Names, not ordinals — the file says "Outlet" and a member inserted into the middle
                // of the enum must not silently change what a committed case means.
                Converters = { new JsonStringEnumConverter() },
            })
        ?? throw new InvalidOperationException("order-minimum.v1.json deserialized to null");

    private sealed record VectorFile(
        int Version,
        IReadOnlyList<ResolutionVector> Resolution,
        IReadOnlyList<CheckVector> Check);

    private sealed record ResolutionVector(
        string Name, IReadOnlyList<CandidateVector> Candidates, ResolvedVector? Expected);

    private sealed record CandidateVector(
        string OrderMinimumId, OrderMinimumScope Scope, string CurrencyCode, string Amount);

    private sealed record ResolvedVector(
        string OrderMinimumId, OrderMinimumScope Scope, string CurrencyCode, string Amount);

    private sealed record CheckVector(
        string Name, MinimumVector? Minimum, TotalVector Total, OrderMinimumVerdict Expected);

    private sealed record MinimumVector(string CurrencyCode, string Amount);

    private sealed record TotalVector(string Amount, string Currency);
}
