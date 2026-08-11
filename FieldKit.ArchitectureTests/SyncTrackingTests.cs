using System.Reflection;
using System.Runtime.CompilerServices;
using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using FieldKit.Modules.Audit;
using FieldKit.Modules.Order;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Journey;
using FieldKit.Modules.Org;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Products;
using FieldKit.Modules.Sync;
using FieldKit.Modules.Visit;

namespace FieldKit.ArchitectureTests;

/// <summary>
/// AT-12 — a module that has something to sync owns the tables that make it syncable.
/// </summary>
/// <remarks>
/// <para>
/// The row-version counter and the tombstone table are opt-in per module
/// (<c>ModuleDbContext.TracksSyncChanges</c>), because mapping them everywhere gives every module a
/// table it never reads and a pending model change that stops <c>Migrate()</c> (ADR-0013).
/// </para>
/// <para>
/// Opt-in means somebody has to remember, and forgetting is not caught by a compiler: an entity
/// marked <see cref="ISyncTracked"/> in a module that has not flipped the flag saves fine until the
/// first write, then fails on a missing <c>change_sequence</c> relation. W8 slices 0 and 1 each cost
/// an afternoon to two different versions of this mistake, which is why the pairing is a test.
/// </para>
/// <para>
/// It checks <b>both directions</b>. A module that syncs must own the tables; a module that owns
/// them must have something to number, or it is carrying two tables and a migration for nothing.
/// </para>
/// </remarks>
public class SyncTrackingTests
{
    private static readonly Assembly[] ModuleAssemblies =
    [
        typeof(IamModule).Assembly,
        typeof(ProductsModule).Assembly,
        typeof(OrgModule).Assembly,
        typeof(OutletsModule).Assembly,
        typeof(ConfigurationModule).Assembly,
        typeof(JourneyModule).Assembly,
        typeof(VisitModule).Assembly,

        // Audit is the first module here with nothing tracked and nothing to track: audits travel
        // up, never down, so its context opts *out* — which is the second half of this gate, and the
        // first module to exercise it deliberately.
        typeof(AuditModule).Assembly,

        // Order opts out too, and unlike Audit it will not stay opted out: a rejected order is the
        // one transactional record that flows back *down* to the device (order spec F4), which is
        // exactly the question a change sequence answers. W11 slice 4 is where it gains one, with
        // the pull feed that reads it — a counter no feed reads is the same waste as a store with no
        // writer (W8 slice 6).
        typeof(OrderModule).Assembly,
        typeof(SyncModule).Assembly,
    ];

    [Fact]
    public void A_module_with_a_sync_tracked_entity_owns_the_sync_tables()
    {
        var missing = ModuleAssemblies
            .Where(module => SyncTrackedEntitiesIn(module).Count > 0 && !TracksSyncChanges(module))
            .Select(module => $"{module.GetName().Name} " +
                $"({string.Join(", ", SyncTrackedEntitiesIn(module))})")
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"These modules have ISyncTracked entities but their ModuleDbContext does not override "
                + $"TracksSyncChanges => true, so they have no change_sequence or tombstone table and "
                + $"the first save of those entities fails at runtime: {string.Join("; ", missing)} (AT-12).");
    }

    [Fact]
    public void A_module_that_owns_the_sync_tables_has_something_to_number()
    {
        var pointless = ModuleAssemblies
            .Where(module => TracksSyncChanges(module) && SyncTrackedEntitiesIn(module).Count == 0)
            .Select(module => module.GetName().Name)
            .ToList();

        Assert.True(
            pointless.Count == 0,
            $"These modules opt into sync tracking but have no ISyncTracked entity, so they carry a "
                + $"change_sequence and tombstone table — and a migration for them — that nothing "
                + $"writes: {string.Join(", ", pointless)} (AT-12).");
    }

    private static List<string> SyncTrackedEntitiesIn(Assembly module) =>
        [.. module.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && typeof(ISyncTracked).IsAssignableFrom(type))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Reads the module's <c>TracksSyncChanges</c> override without building a DbContext.
    /// </summary>
    /// <remarks>
    /// A real instance would need <c>DbContextOptions</c> and a provider, which drags the whole EF
    /// stack into an architecture test to read one boolean. Both overrides are constant expressions
    /// (<c>=> true</c>), so an uninitialised instance answers correctly — and if one ever stops being
    /// constant, this throws rather than lying, because a getter reading uninitialised state fails
    /// loudly on a null field.
    /// </remarks>
    private static bool TracksSyncChanges(Assembly module)
    {
        var contextType = module.GetTypes()
            .SingleOrDefault(type => type is { IsClass: true, IsAbstract: false }
                && typeof(ModuleDbContext).IsAssignableFrom(type));

        if (contextType is null) return false;

        var property = typeof(ModuleDbContext).GetProperty(
            "TracksSyncChanges",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "ModuleDbContext.TracksSyncChanges is gone or renamed — AT-12 is checking nothing.");

        var uninitialised = RuntimeHelpers.GetUninitializedObject(contextType);
        return (bool)property.GetGetMethod(nonPublic: true)!.Invoke(uninitialised, null)!;
    }
}
