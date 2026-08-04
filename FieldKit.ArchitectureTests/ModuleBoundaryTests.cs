using System.Reflection;
using FieldKit.Modules.Catalog;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Iam.Contracts;
using FieldKit.Modules.Org;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Org.Contracts;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Outlets.Contracts;
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
    private static readonly Assembly Catalog = typeof(CatalogModule).Assembly;
    private static readonly Assembly OrgModuleAssembly = typeof(OrgModule).Assembly;
    private static readonly Assembly OutletsModuleAssembly = typeof(OutletsModule).Assembly;
    private static readonly Assembly OutletsContracts = typeof(IOutletCatalog).Assembly;
    private static readonly Assembly OrgContracts = typeof(RepAssignmentChanged).Assembly;
    private static readonly Assembly ConfigurationModuleAssembly = typeof(ConfigurationModule).Assembly;
    private static readonly Assembly ConfigurationContracts = typeof(IFieldDefinitionCatalog).Assembly;

    /// <summary>Module <b>implementation</b> assemblies — the ones nothing outside may reference.</summary>
    private static readonly string[] ModuleImplementations =
    [
        "FieldKit.Modules.Iam",
        "FieldKit.Modules.Catalog",
        "FieldKit.Modules.Org",
        "FieldKit.Modules.Outlets",
        "FieldKit.Modules.Configuration",
    ];

    [Fact] // AT-1 — the core boundary.
    public void A_module_never_references_another_modules_implementation()
    {
        AssertReferencesNoOtherModule(Catalog);
        AssertReferencesNoOtherModule(Iam);
        AssertReferencesNoOtherModule(OrgModuleAssembly);
        AssertReferencesNoOtherModule(OutletsModuleAssembly);
        AssertReferencesNoOtherModule(ConfigurationModuleAssembly);
    }

    [Fact] // AT-3 — entities cannot leak, because contracts cannot see them.
    public void Contracts_do_not_reference_any_module_implementation()
    {
        // The stronger form of "no domain type in a signature": if the contracts assembly cannot see
        // the implementation, no signature in it can name a domain type. Verifiable from the csproj.
        AssertDoesNotReference(IamContracts, ModuleImplementations);
        AssertDoesNotReference(OutletsContracts, ModuleImplementations);
        AssertDoesNotReference(OrgContracts, ModuleImplementations);
        AssertDoesNotReference(ConfigurationContracts, ModuleImplementations);
    }

    [Fact] // AT-3, the half a reference check cannot cover.
    public void Contracts_expose_no_EF_Core_or_ASP_NET_types()
    {
        // A contracts assembly that could see EF or ASP.NET would let persistence and transport into
        // the shared surface — an `IQueryable<T>` return type, say, which hands the caller the
        // ability to compose queries against another module's tables.
        foreach (var contracts in new[] { IamContracts, OutletsContracts, OrgContracts, ConfigurationContracts })
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
