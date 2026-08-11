namespace FieldKit.Server.Tests;

/// <summary>
/// Keeps the committed generated vectors in step with the generator (<c>PRD-08</c>) — W6 slice 14.
/// </summary>
/// <remarks>
/// <para>
/// The files under <c>vectors/</c> are committed artifacts, because the TypeScript mirror reads them
/// and cannot run a C# generator. Committed output has one failure mode: it goes stale. The engine
/// changes, C#'s own tests are updated with it, and the mirror keeps proving itself against a file
/// describing the engine as it was — which is exactly the drift this whole apparatus exists to catch,
/// arriving through the apparatus itself.
/// </para>
/// <para>
/// So the file is compared to what the generator produces now. Regenerate with:
/// </para>
/// <code>FIELDKIT_WRITE_VECTORS=1 dotnet test --filter GeneratedVectorTests</code>
/// <para>
/// which makes updating them a deliberate act that shows up in a diff, rather than something that
/// happens whenever someone runs the suite.
/// </para>
/// </remarks>
public class GeneratedVectorTests
{
    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (var path in VectorGenerator.Files().Keys) data.Add(path);
        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void The_committed_file_is_what_the_generator_produces(string relativePath)
    {
        var expected = VectorGenerator.Files()[relativePath];
        var path = Path.Combine(VectorsDirectory(), relativePath.Replace('/', Path.DirectorySeparatorChar));

        if (Environment.GetEnvironmentVariable("FIELDKIT_WRITE_VECTORS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Written as UTF-8 without a BOM and with the generator's own \n endings — File.WriteAllText
            // would otherwise be at the mercy of the platform.
            File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(expected));
            return;
        }

        Assert.True(File.Exists(path), $"{path} is missing — regenerate it (see this class's docs).");

        // Normalised, because git may check the file out with CRLF on Windows. What is being compared
        // is the content, not the checkout's line-ending policy.
        Assert.Equal(
            expected.ReplaceLineEndings("\n"),
            File.ReadAllText(path).ReplaceLineEndings("\n"));
    }

    /// <summary>Every field the format says is a decimal, and must therefore never be a JSON number.</summary>
    private static readonly HashSet<string> DecimalFields =
    [
        "amount", "amountOff", "getPercentOff", "gross", "net", "percentage", "percentOff",
        "score", "tax", "weight",
    ];

    [Theory]
    [MemberData(nameof(Files))]
    public void No_generated_value_is_a_bare_json_number(string relativePath)
    {
        // The hand-written files get this enforced by a converter that throws on a number token. The
        // generated ones cannot: this generator writes JSON by string interpolation, so a missing pair
        // of quotes would produce a file that parses perfectly and hands the mirror a float — which
        // is the exact failure vectors/README.md exists to prevent, arriving through the one path
        // that had no guard on it.
        using var document = System.Text.Json.JsonDocument.Parse(VectorGenerator.Files()[relativePath]);

        var offenders = new List<string>();
        Walk(document.RootElement, "$", offenders);

        Assert.Empty(offenders);
    }

    private static void Walk(
        System.Text.Json.JsonElement element, string path, List<string> offenders)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (DecimalFields.Contains(property.Name)
                        && property.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        offenders.Add($"{path}.{property.Name}");
                    }

                    Walk(property.Value, $"{path}.{property.Name}", offenders);
                }

                break;

            case System.Text.Json.JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray()) Walk(item, $"{path}[{index++}]", offenders);
                break;
        }
    }

    [Fact]
    public void The_generator_is_deterministic()
    {
        // Two runs in one process, which catches a generator reaching for a clock or an unseeded
        // random. It cannot catch one reaching for something that varies across *runtimes* — that is
        // what the hand-rolled PRNG is for, since Random's seeded sequence changed in .NET 6 and
        // would have made every regeneration on a new SDK look like a deliberate change.
        Assert.Equal(VectorGenerator.Files(), VectorGenerator.Files());
    }

    /// <summary>The repository's <c>vectors/</c> directory, found by walking up from the test binary.</summary>
    /// <remarks>
    /// The copies in the test output are read-only fixtures; writing there would produce a file that
    /// vanishes on the next clean and never reaches the mirror. So this finds the source tree
    /// instead, by looking for the directory that has the README in it.
    /// </remarks>
    internal static string VectorsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "vectors");
            if (File.Exists(Path.Combine(candidate, "README.md"))) return candidate;
        }

        throw new InvalidOperationException(
            $"No vectors/ directory above {AppContext.BaseDirectory}.");
    }
}
