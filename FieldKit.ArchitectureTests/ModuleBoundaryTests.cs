using System.Reflection;
using System.Text.RegularExpressions;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Iam.Contracts;
using FieldKit.Modules.Journey;
using FieldKit.Modules.Journey.Contracts;
using FieldKit.Modules.Sync;
using FieldKit.Modules.Visit;
using FieldKit.Modules.Visit.Contracts;
using FieldKit.Modules.Org;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Org.Contracts;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Outlets.Contracts;
using FieldKit.Modules.Products;
using NetArchTest.Rules;

namespace FieldKit.ArchitectureTests;

/// <summary>
/// The module boundary (module-boundaries §5). These became testable the moment a second module
/// existed — with one module, AT-1 and AT-3 are statements about an empty set and pass whatever the
/// code does, which is why they were not written before IAM landed.
/// </summary>
/// <remarks>
/// Assembly references, not namespace rules, for the boundary itself. NetArchTest matches namespaces
/// by <b>prefix</b>, so a rule written against <c>FieldKit.Modules.Iam</c> also matches
/// <c>FieldKit.Modules.Iam.Contracts</c> — it would forbid the one dependency AT-1 explicitly
/// permits, and the first module to consume IAM's contracts would fail the test that exists to allow
/// it. Referenced-assembly names are exact.
/// </remarks>
public class ModuleBoundaryTests
{
    private static readonly Assembly Iam = typeof(IamModule).Assembly;
    private static readonly Assembly IamContracts = typeof(IUserDirectory).Assembly;
    private static readonly Assembly ProductsModuleAssembly = typeof(ProductsModule).Assembly;
    private static readonly Assembly OrgModuleAssembly = typeof(OrgModule).Assembly;
    private static readonly Assembly OutletsModuleAssembly = typeof(OutletsModule).Assembly;
    private static readonly Assembly OutletsContracts = typeof(IOutletCatalog).Assembly;
    private static readonly Assembly OrgContracts = typeof(RepAssignmentChanged).Assembly;
    private static readonly Assembly ConfigurationModuleAssembly = typeof(ConfigurationModule).Assembly;
    private static readonly Assembly JourneyModuleAssembly = typeof(JourneyModule).Assembly;
    private static readonly Assembly VisitModuleAssembly = typeof(VisitModule).Assembly;
    private static readonly Assembly SyncModuleAssembly = typeof(SyncModule).Assembly;
    private static readonly Assembly ConfigurationContracts = typeof(IFieldDefinitionCatalog).Assembly;
    private static readonly Assembly JourneyContracts = typeof(IJourneyQuery).Assembly;
    private static readonly Assembly VisitContracts = typeof(IVisitIngest).Assembly;

    /// <summary>
    /// Every module <b>implementation</b> assembly — the ones nothing outside may reference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one place a module is named.</b> Everything else in this file is derived from it, and
    /// <see cref="Every_module_in_the_solution_is_gated"/> checks it against the solution itself, so a
    /// module cannot be added and left ungated.
    /// </para>
    /// <para>
    /// It used to be two lists — this one and a parallel array of names — and they drifted: seven
    /// modules were gated for references while five were walked for cycles, because Journey and Visit
    /// were only ever added to one. It went unnoticed for three slices, since neither had a contract
    /// implementation for the cycle walk to find. A gate that silently covers less than it claims is
    /// the one failure this file cannot afford, so the duplication is gone.
    /// </para>
    /// <para>
    /// <b>Order matters here.</b> Static field initializers run in textual order, so this must be
    /// declared above anything derived from it — a derived list initialised first would be empty, and
    /// every check in this file would pass while examining nothing.
    /// </para>
    /// </remarks>
    private static readonly Assembly[] ModuleAssemblies =
    [
        Iam,
        ProductsModuleAssembly,
        OrgModuleAssembly,
        OutletsModuleAssembly,
        ConfigurationModuleAssembly,
        JourneyModuleAssembly,
        VisitModuleAssembly,
        SyncModuleAssembly,
    ];

    /// <summary>The same modules by assembly name, for the reference checks.</summary>
    private static readonly string[] ModuleImplementations =
        [.. ModuleAssemblies.Select(assembly => assembly.GetName().Name!)];

    /// <summary>
    /// Every module's <c>.Contracts</c> assembly — its only public surface.
    /// </summary>
    /// <remarks>
    /// Also checked against the solution, and for the same reason: a contracts assembly nobody added
    /// here is one AT-3 never looks at, which is exactly where an <c>IQueryable</c> or an entity would
    /// slip into a shared surface unnoticed.
    /// </remarks>
    private static readonly Assembly[] ContractsAssemblies =
    [
        IamContracts, OrgContracts, OutletsContracts, ConfigurationContracts, JourneyContracts,
        VisitContracts,
    ];

    [Fact] // AT-1 — the core boundary.
    public void A_module_never_references_another_modules_implementation()
    {
        Assert.NotEmpty(ModuleAssemblies);

        foreach (var module in ModuleAssemblies) AssertReferencesNoOtherModule(module);
    }

    [Fact] // AT-3 — entities cannot leak, because contracts cannot see them.
    public void Contracts_do_not_reference_any_module_implementation()
    {
        // The stronger form of "no domain type in a signature": if the contracts assembly cannot see
        // the implementation, no signature in it can name a domain type. Verifiable from the csproj.
        Assert.NotEmpty(ContractsAssemblies);

        foreach (var contracts in ContractsAssemblies)
        {
            AssertDoesNotReference(contracts, ModuleImplementations);
        }
    }

    [Fact] // AT-3, the half a reference check cannot cover.
    public void Contracts_expose_no_EF_Core_or_ASP_NET_types()
    {
        // A contracts assembly that could see EF or ASP.NET would let persistence and transport into
        // the shared surface — an `IQueryable<T>` return type, say, which hands the caller the
        // ability to compose queries against another module's tables.
        foreach (var contracts in ContractsAssemblies)
        {
            var result = Types.InAssembly(contracts)
                .Should().NotHaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore")
                .GetResult();

            Assert.True(
                result.IsSuccessful,
                $"{contracts.GetName().Name} — failing types: "
                    + string.Join(", ", result.FailingTypeNames ?? []));
        }
    }

    [Fact]
    public void A_modules_implementation_is_reachable_only_through_its_contracts()
    {
        // Guards what makes AT-1 worth having: consumers need something to bind to. If the contracts
        // assembly stopped carrying the public interfaces, AT-1 would still pass while the boundary
        // quietly became "nothing can talk to IAM at all".
        Assert.Contains(typeof(IUserDirectory), IamContracts.GetExportedTypes());
        Assert.Contains(typeof(ITenantRegistry), IamContracts.GetExportedTypes());

        // …and the implementations behind them stay inside the module (AT-2).
        var implementations = Iam.GetTypes()
            .Where(type => !type.IsInterface && typeof(IUserDirectory).IsAssignableFrom(type))
            .ToList();

        Assert.NotEmpty(implementations);
        Assert.All(implementations, type => Assert.False(type.IsPublic, $"{type.Name} should be internal"));

        // Same shape for Outlets, whose contracts assembly landed once Organization needed it.
        Assert.Contains(typeof(IOutletCatalog), OutletsContracts.GetExportedTypes());

        var catalogs = OutletsModuleAssembly.GetTypes()
            .Where(type => !type.IsInterface && typeof(IOutletCatalog).IsAssignableFrom(type))
            .ToList();

        Assert.NotEmpty(catalogs);
        Assert.All(catalogs, type => Assert.False(type.IsPublic, $"{type.Name} should be internal"));

        // And the same for the classification contract, which arrived when assortments needed it.
        // Asserted separately rather than folded into the loop above: a second contract on the same
        // assembly is exactly where "the interface is exported but the class is public too" slips
        // through, because the first one passing makes the file look already covered.
        Assert.Contains(typeof(IOutletClassification), OutletsContracts.GetExportedTypes());

        var classifiers = OutletsModuleAssembly.GetTypes()
            .Where(type => !type.IsInterface && typeof(IOutletClassification).IsAssignableFrom(type))
            .ToList();

        Assert.NotEmpty(classifiers);
        Assert.All(classifiers, type => Assert.False(type.IsPublic, $"{type.Name} should be internal"));

        // And Journey's, which landed in W7 slice 9b once check-in had a question for it.
        Assert.Contains(typeof(IJourneyQuery), JourneyContracts.GetExportedTypes());

        var journeys = JourneyModuleAssembly.GetTypes()
            .Where(type => !type.IsInterface && typeof(IJourneyQuery).IsAssignableFrom(type))
            .ToList();

        Assert.NotEmpty(journeys);
        Assert.All(journeys, type => Assert.False(type.IsPublic, $"{type.Name} should be internal"));

        // And Visit's, which landed in W8 slice 5 once Sync had work to hand it.
        Assert.Contains(typeof(IVisitIngest), VisitContracts.GetExportedTypes());

        var ingests = VisitModuleAssembly.GetTypes()
            .Where(type => !type.IsInterface && typeof(IVisitIngest).IsAssignableFrom(type))
            .ToList();

        Assert.NotEmpty(ingests);
        Assert.All(ingests, type => Assert.False(type.IsPublic, $"{type.Name} should be internal"));
    }


    [Fact] // The gate's own coverage: a module nothing names is a module nothing checks.
    public void Every_module_in_the_solution_is_gated()
    {
        // Every check in this file starts from a list somebody typed. That is fine as long as the
        // list is complete, and completeness is the one property none of them can observe — an
        // assembly nobody named simply never fails anything. Journey and Visit sat outside the cycle
        // check for three slices exactly this way.
        //
        // So the solution file is the authority. It is where a project has to be added to build at
        // all, which makes it the one list that cannot be forgotten.
        var projects = SolutionProjects();

        var modules = projects
            .Where(name => name.StartsWith("FieldKit.Modules.", StringComparison.Ordinal))
            .Where(name => !name.EndsWith(".Contracts", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var contracts = projects
            .Where(name => name.StartsWith("FieldKit.Modules.", StringComparison.Ordinal))
            .Where(name => name.EndsWith(".Contracts", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        // A sanity check on the parsing itself. Without it, a solution format this cannot read would
        // compare two empty sets and report the gates as complete — the same failure it exists to
        // prevent, one level up.
        Assert.True(modules.Count >= 7, $"Only found {modules.Count} modules in the solution.");

        Assert.Equal(
            modules.OrderBy(name => name, StringComparer.Ordinal),
            ModuleImplementations.OrderBy(name => name, StringComparer.Ordinal));

        Assert.Equal(
            contracts.OrderBy(name => name, StringComparer.Ordinal),
            ContractsAssemblies
                .Select(assembly => assembly.GetName().Name!)
                .OrderBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>Every project the solution builds, by name.</summary>
    /// <remarks>
    /// Read from <c>FieldKit.slnx</c> rather than from this assembly's own references, because the
    /// references are the thing being checked: a module the architecture-test project forgot to
    /// reference is precisely the module that would otherwise go ungated, and it would be invisible
    /// to a check that started from what this project can see.
    /// </remarks>
    private static IReadOnlyList<string> SolutionProjects()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FieldKit.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var solution = File.ReadAllText(Path.Combine(directory!.FullName, "FieldKit.slnx"));

        return
        [
            .. Regex.Matches(solution, "Path=\"([^\"]+)\\.csproj\"")
                .Select(match => Path.GetFileName(match.Groups[1].Value))
        ];
    }

    [Fact] // AT-10 — the cycle AT-1 cannot see.
    public void Contract_implementations_never_form_a_cycle()
    {
        // AT-1 forbids implementation-to-implementation references, which is what would break a
        // build. It permits two modules referencing each other's *contracts*, and that permission is
        // load-bearing: every .Contracts assembly is a leaf, so the assembly graph stays acyclic
        // however many modules point at each other, and insisting on a one-way arrow would invent a
        // hierarchy the domain does not have. Outlets owns what a shop is; Organization owns who
        // covers it.
        //
        // What it cannot see is a cycle at *runtime*. If the class behind ITerritoryDirectory took a
        // dependency on IOutletCatalog while the class behind IOutletCatalog took one on
        // ITerritoryDirectory, a single call would re-enter through the other module — mutual
        // recursion wearing two sets of perfectly legal references.
        //
        // So the rule is about contract *implementations* only. An endpoint may depend on any
        // module's contract, because nothing calls back into an endpoint; a contract implementation
        // is the re-enterable surface, and it is the one this constrains.
        var edges = new HashSet<(string From, string To)>();

        foreach (var module in ModuleAssemblies)
        {
            var moduleName = module.GetName().Name!;

            foreach (var implementation in module.GetTypes().Where(type => type is { IsClass: true, IsAbstract: false }))
            {
                if (!implementation.GetInterfaces().Any(IsContract)) continue;

                var dependencies = implementation.GetConstructors()
                    .SelectMany(constructor => constructor.GetParameters())
                    .Select(parameter => parameter.ParameterType)
                    .Where(IsContract)
                    .Select(contract => ModuleOf(contract.Assembly))
                    .Where(owner => owner is not null && owner != moduleName);

                foreach (var dependency in dependencies) edges.Add((moduleName, dependency!));
            }
        }

        var cycle = FindCycle(edges);

        Assert.True(
            cycle is null,
            "Contract implementations depend on each other in a cycle, so one module's call can "
            + "re-enter through another: " + cycle);
    }

    /// <summary>An interface published by some module's contracts assembly.</summary>
    private static bool IsContract(Type type) =>
        type.IsInterface && ModuleOf(type.Assembly) is not null;

    /// <summary>The module a contracts assembly belongs to, or null if it is not one.</summary>
    private static string? ModuleOf(Assembly assembly)
    {
        var name = assembly.GetName().Name;

        return name is not null && name.EndsWith(".Contracts", StringComparison.Ordinal)
            && ModuleImplementations.Contains(name[..^".Contracts".Length])
                ? name[..^".Contracts".Length]
                : null;
    }

    /// <summary>The first cycle in the edge set, rendered as a path, or null when there is none.</summary>
    private static string? FindCycle(IReadOnlySet<(string From, string To)> edges)
    {
        var visiting = new HashSet<string>();
        var done = new HashSet<string>();
        var path = new List<string>();

        string? Walk(string node)
        {
            if (done.Contains(node)) return null;

            if (!visiting.Add(node))
            {
                return string.Join(" -> ", path.Skip(path.IndexOf(node))) + " -> " + node;
            }

            path.Add(node);

            foreach (var edge in edges.Where(edge => edge.From == node))
            {
                if (Walk(edge.To) is { } found) return found;
            }

            path.RemoveAt(path.Count - 1);
            visiting.Remove(node);
            done.Add(node);
            return null;
        }

        return edges.Select(edge => edge.From).Distinct().Select(Walk).FirstOrDefault(found => found is not null);
    }

    private static void AssertReferencesNoOtherModule(Assembly module) =>
        AssertDoesNotReference(
            module,
            [.. ModuleImplementations.Where(name => name != module.GetName().Name)]);

    private static void AssertDoesNotReference(Assembly assembly, IReadOnlyCollection<string> forbidden)
    {
        if (forbidden.Count == 0) return;

        var violations = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null && forbidden.Contains(name, StringComparer.Ordinal))
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"{assembly.GetName().Name} references {string.Join(", ", violations)} — a module's "
                + "implementation is private to it; depend on its .Contracts assembly instead (AT-1).");
    }
}
