using System.Reflection;
using System.Text.RegularExpressions;

namespace FieldKit.Server.Tests;

/// <summary>
/// The threat model, made executable (<c>security §7</c>) — W13 slice 9.
/// </summary>
/// <remarks>
/// <para>
/// <b>A threat model whose mitigations are prose is a document; one whose mitigations are named
/// tests is a claim CI can fail.</b> §7 has listed seven rows since W2 and each named a control in a
/// sentence — every one of which happened to be true, and none of which anything checked. The W13
/// slice 0 audit found two claims in the neighbouring sections that were not: a dispatcher that did
/// not run, and CORS that did not exist. Both were prose, in a table, next to prose that was correct.
/// </para>
/// <para>
/// So the table gained a <b>Proven by</b> column and this reads it back. A row with no citation
/// fails; a citation naming a test that does not exist fails. What that buys is not proof the system
/// is secure — it is that a mitigation cannot be deleted or renamed while the security doc goes on
/// asserting it, which is the failure mode this week found twice.
/// </para>
/// <para>
/// <b>Parsed rather than duplicated.</b> A copy of the table in C# would be a second list to keep in
/// step — the mistake <c>ModuleRegistryTests</c> and the reachability gate both exist to prevent, and
/// the one W12½ slice 2 recorded when it *deleted* a duplicate assertion rather than keeping two.
/// </para>
/// </remarks>
public partial class ThreatModelTests
{
    /// <summary>A row of §7, as the table declares it.</summary>
    private sealed record ThreatRow(string Threat, IReadOnlyList<string> Citations);

    [Fact]
    public void Every_threat_names_a_test_that_exists()
    {
        /*
         * Both halves matter and they fail differently. A row with no citation is a mitigation
         * nobody has pinned — the state every row was in before this slice. A citation that does not
         * resolve is worse: it reads as proof and is a dead name, which is exactly what a rename
         * leaves behind.
         */
        var rows = Rows();

        Assert.NotEmpty(rows);

        var known = typeof(ThreatModelTests).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(method => method.GetCustomAttributes<FactAttribute>().Any()
                || method.GetCustomAttributes<TheoryAttribute>().Any())
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            Assert.True(
                row.Citations.Count > 0,
                $"The '{row.Threat}' row of security §7 names no test. A mitigation nobody has "
                + "pinned is the state this gate exists to end.");

            foreach (var citation in row.Citations)
            {
                Assert.True(
                    known.Contains(citation),
                    $"'{row.Threat}' cites {citation}, which is not a test in this assembly. "
                    + "A citation that does not resolve reads as proof and is a dead name.");
            }
        }
    }

    [Fact]
    public void The_bypass_ban_reaches_every_production_project()
    {
        /*
         * The one mitigation with no test, because there is nothing to run: `IgnoreQueryFilters` and
         * `ExecuteSqlRaw` are banned at compile time, so a violation is a build error rather than a
         * failure. What can be checked is the *wiring* — that the list is handed to every non-test
         * project by `Directory.Build.props` rather than referenced project by project.
         *
         * That distinction is the whole point. Per-project references were the arrangement before,
         * and they enforced the rule "in the two projects least likely to break it and nowhere
         * else"; a module added tomorrow inherits this one without anybody remembering.
         *
         * Read as text, in the shape `globals.test.ts` uses and for the same reason: what is being
         * asserted is a build-file decision, and nothing at runtime can see it.
         */
        var props = File.ReadAllText(Path.Combine(RepositoryRoot().FullName, "Directory.Build.props"));

        Assert.Contains("BannedSymbols.Isolation.txt", props, StringComparison.Ordinal);

        // Applied to C# projects that are not test projects — the exemption is deliberate and
        // documented, because proving isolation works means being able to look past the filter.
        Assert.Contains(
            "!$(MSBuildProjectName.EndsWith('.Tests'))",
            props,
            StringComparison.Ordinal);

        // And escalated to an error. A warning is a suggestion, and one of these rules is the
        // tenant-isolation bypass.
        Assert.Contains("<WarningsAsErrors>$(WarningsAsErrors);RS0030</WarningsAsErrors>", props, StringComparison.Ordinal);
    }

    /// <summary>The rows of §7's table, read out of the document.</summary>
    private static IReadOnlyList<ThreatRow> Rows()
    {
        var lines = File.ReadAllLines(
            Path.Combine(RepositoryRoot().FullName, "docs", "architecture", "16-security.md"));

        var rows = new List<ThreatRow>();
        var inTable = false;

        foreach (var line in lines)
        {
            // The table starts after §7's heading and ends at the first line that is not a row.
            if (line.StartsWith("## 7.", StringComparison.Ordinal)) { inTable = true; continue; }
            if (!inTable) continue;
            if (line.StartsWith("## ", StringComparison.Ordinal)) break;

            if (!line.StartsWith("| **", StringComparison.Ordinal)) continue;

            var cells = line.Split('|', StringSplitOptions.TrimEntries);

            // `| threat | vector | mitigation | proven by |` splits to six with the empty ends.
            if (cells.Length < 6) continue;

            rows.Add(new ThreatRow(
                cells[1].Trim('*', ' '),
                CitationPattern().Matches(cells[4]).Select(match => match.Value).ToList()));
        }

        return rows;
    }

    /// <summary>
    /// A citation: <c>SomeTests.Some_test_name</c> in backticks.
    /// </summary>
    /// <remarks>
    /// Anchored on the backticks rather than on the shape alone, so prose that happens to contain a
    /// dotted name — a type, a doc reference — is not mistaken for a claim about a test.
    /// </remarks>
    [GeneratedRegex(@"(?<=`)[A-Za-z]+Tests\.[A-Za-z0-9_]+(?=`)")]
    private static partial Regex CitationPattern();

    /// <summary>The repository root, found by walking up until the docs are underfoot.</summary>
    private static DirectoryInfo RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "docs", "architecture")))
                return directory;
        }

        throw new DirectoryNotFoundException(
            $"No docs/architecture above {AppContext.BaseDirectory}.");
    }
}
