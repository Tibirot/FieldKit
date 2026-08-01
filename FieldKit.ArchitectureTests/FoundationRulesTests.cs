using System.Reflection;
using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;
using NetArchTest.Rules;

namespace FieldKit.ArchitectureTests;

/// <summary>
/// The architecture is self-enforcing: these run in CI and fail the build on a boundary violation
/// (module-boundaries §5). This is the foundation subset; module-boundary rules (AT-1..AT-6) arrive
/// with the modules. AT-7 ("no static time; IClock only") is enforced at *compile* time by the
/// banned-API analyzer, not here.
/// </summary>
public class FoundationRulesTests
{
    private static readonly Assembly SharedKernel = typeof(Money).Assembly;
    private static readonly Assembly BuildingBlocks = typeof(ITenantContext).Assembly;

    [Fact] // Dependencies point inward: the kernel knows nothing of the building blocks.
    public void SharedKernel_does_not_depend_on_BuildingBlocks()
    {
        var result = Types.InAssembly(SharedKernel)
            .Should().NotHaveDependencyOn("FieldKit.BuildingBlocks")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact] // AT-8 (foundation): keep the kernel and building blocks free of web/ORM infrastructure.
    public void Foundation_has_no_infrastructure_dependencies()
    {
        var result = Types.InAssemblies([SharedKernel, BuildingBlocks])
            .Should().NotHaveDependencyOnAny(
                "Microsoft.AspNetCore",
                "Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static string Describe(TestResult result) =>
        result.IsSuccessful
            ? "OK"
            : "Failing types: " + string.Join(", ", result.FailingTypeNames ?? []);
}
