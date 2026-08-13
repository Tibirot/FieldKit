using System.Reflection;
using System.Text.RegularExpressions;

namespace FieldKit.ArchitectureTests;

/// <summary>
/// The module registry describes the contracts that exist (W11½ slice R1).
/// </summary>
/// <remarks>
/// <para>
/// <b>A gate on a document, which is unusual here and earns it.</b> The registry in
/// <c>docs/architecture/10-module-boundaries.md</c> §7 is what a module author reads to decide what
/// they may depend on, and <c>CLAUDE.md</c> makes it a deliverable that moves with the code. It has
/// now drifted twice: the pre-Phase-2 audit found four entries with no interface behind them, and the
/// post-W11 regression found the opposite — a built contract missing altogether and three more still
/// marked as planned, two of which Order already consumes.
/// </para>
/// <para>
/// <b>Both directions, because the two failures mislead different readers.</b> Overstating tells
/// someone a seam exists when it does not; understating tells them to build a workaround for
/// something that shipped weeks ago. A check that only caught one would have missed the drift this
/// slice was opened for.
/// </para>
/// <para>
/// <b>It reads the table rather than a hand-kept list.</b> A list would be a third place to forget,
/// which is the failure being fixed rather than a fix for it — the same reasoning that made the other
/// architecture gates self-checking.
/// </para>
/// </remarks>
public partial class ModuleRegistryTests
{
    /// <summary>Every interface a `*.Contracts` assembly publishes, by simple name.</summary>
    /// <remarks>
    /// <para>
    /// Reflection rather than a scan of the source: it is the same view a consuming module gets, so
    /// an interface that is <c>public</c> in a file but not reachable at runtime — a wrong assembly,
    /// a missed project reference — does not pass by looking right in the text.
    /// </para>
    /// <para>
    /// <b>Loaded from the output directory, not from <c>AppDomain.GetAssemblies()</c>.</b> That
    /// returns only what has already been touched, and the first draft of this class used it: the
    /// set came back empty, so the "every built contract is listed" test passed against nothing at
    /// all. Its sibling caught it by reporting every entry in the table as unbuilt. A gate whose
    /// green means "I found no work to do" is the failure this codebase keeps writing down.
    /// </para>
    /// </remarks>
    private static IReadOnlySet<string> BuiltContracts()
    {
        var names = Directory
            .GetFiles(AppContext.BaseDirectory, "FieldKit.*.Contracts.dll")
            .Select(Assembly.LoadFrom)
            .SelectMany(SafeTypes)
            .Where(type => type.IsInterface && type.IsPublic)
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        // The one assertion that makes every other assertion here mean something.
        Assert.True(
            names.Count > 0,
            $"No contracts assemblies found beside the tests in {AppContext.BaseDirectory} — "
                + "this gate would pass vacuously.");

        return names;
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        // A contracts assembly should never fail to load its own types, but a partially-loaded
        // dependency must not take the whole gate down with a stack trace that hides the real answer.
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException loaded)
        {
            return loaded.Types.Where(type => type is not null)!;
        }
    }

    /// <summary>The registry's "Key contracts" column, as (name, whether it is bold).</summary>
    private static IReadOnlyList<(string Name, bool Bold)> RegistryEntries()
    {
        var table = File.ReadAllText(RegistryPath());

        // Only the rows of §7's table: a line starting with `|` whose fourth cell holds the contracts.
        // Anything else in the document that happens to name an interface — the prose above the
        // table, the layering rules — is deliberately not part of the claim being checked.
        return
        [
            .. table
                .Split('\n')
                .Where(line => line.StartsWith("| ", StringComparison.Ordinal))
                .Select(line => line.Split('|'))
                .Where(cells => cells.Length > 4)
                .SelectMany(cells => Entry.Matches(cells[4]))
                .Select(match => (match.Groups["name"].Value, match.Value.StartsWith("**", StringComparison.Ordinal))),
        ];
    }

    /// <summary>A cell entry: <c>**`IThing`**</c> when built, <c>`IThing`</c> when planned.</summary>
    [GeneratedRegex(@"(\*\*)?`(?<name>I[A-Za-z]+)`(\*\*)?")]
    private static partial Regex Entry { get; }

    private static string RegistryPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName, "docs", "architecture", "10-module-boundaries.md");

            if (File.Exists(candidate)) return candidate;
        }

        throw new InvalidOperationException(
            $"No docs/architecture/10-module-boundaries.md above {AppContext.BaseDirectory}.");
    }

    [Fact]
    public void Every_built_contract_is_in_the_registry_and_marked_as_built()
    {
        /*
         * The direction the post-W11 regression found broken. `IOrderMinimumChangeFeed` was absent
         * entirely and three others were shown as planned — including `IPricingService`, which W11
         * slice 14 had been consuming for a week.
         */
        var entries = RegistryEntries();
        var built = entries.Where(entry => entry.Bold).Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        var mentioned = entries.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);

        var missing = BuiltContracts().Where(name => !mentioned.Contains(name)).Order().ToList();
        var understated = BuiltContracts().Where(name => mentioned.Contains(name) && !built.Contains(name)).Order().ToList();

        Assert.True(
            missing.Count == 0,
            $"Built contracts absent from the module registry: {string.Join(", ", missing)}. "
                + "Add a row entry in docs/architecture/10-module-boundaries.md §7, in bold.");

        Assert.True(
            understated.Count == 0,
            $"Built contracts the registry still shows as planned: {string.Join(", ", understated)}. "
                + "Bold means built — a reader takes plain as 'you may not depend on this yet'.");
    }

    [Fact]
    public void Nothing_the_registry_calls_built_is_missing_from_the_assemblies()
    {
        /*
         * The direction the pre-Phase-2 audit found broken, and the reason the bold convention exists
         * at all. A seam somebody is told they can use, and cannot.
         *
         * Planned entries are deliberately not checked: naming the shape before building it is what
         * the plain style is *for*.
         */
        var overstated = RegistryEntries()
            .Where(entry => entry.Bold)
            .Select(entry => entry.Name)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !BuiltContracts().Contains(name))
            .Order()
            .ToList();

        Assert.True(
            overstated.Count == 0,
            $"The registry marks these as built and no contracts assembly publishes them: "
                + $"{string.Join(", ", overstated)}. Either build it, or set it back to plain — "
                + "plain is how the table says 'planned'.");
    }
}
