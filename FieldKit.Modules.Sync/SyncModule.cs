using FieldKit.Infrastructure;
using FieldKit.Web;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Modules.Sync;

/// <summary>
/// The Sync module: the device registry now (<c>OFF-12</c>), pull and push next (<c>OFF-03/04</c>).
/// </summary>
/// <remarks>
/// <para>
/// The registry comes first because every other question this module will answer is asked *after*
/// it. A pull is scoped to a rep's territory, and resolving that scope is expensive; a device that
/// is no longer the rep's must be refused before the expensive part, not after.
/// </para>
/// <para>
/// <b>No contracts yet</b> — the registry lists in the module registry (<c>ISyncEndpoints</c>) are
/// consumed by nothing outside this module, and an interface designed before its caller is a guess
/// that caller has to live with.
/// </para>
/// </remarks>
public sealed class SyncModule : IModule
{
    public string Name => "Sync";

    public IReadOnlyList<PermissionDefinition> Permissions =>
    [
        new(SyncPermissions.DeviceRead, "See which devices are bound, and which is active."),
        new(SyncPermissions.DeviceRevoke, "Deactivate someone else's device — lost, stolen or replaced."),
    ];

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("fieldkitdb")
            ?? throw new InvalidOperationException("Connection string 'fieldkitdb' is not configured.");

        services.AddModuleDbContext<SyncDbContext>(connectionString, SyncDbContext.SchemaName);
        services.AddHostedService<ModuleMigrator<SyncDbContext>>();

        // The ledger is scoped: it hands out tracked entities on the request context, so the
        // caller can commit them with the work they describe (W8 slice 4).
        services.AddScoped<IMutationLedger, MutationLedger>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDeviceEndpoints();
        endpoints.MapPullEndpoints();
        endpoints.MapPushEndpoints();
    }
}

/// <summary>
/// The permissions this module owns, as <c>resource:action</c> strings.
/// </summary>
/// <remarks>
/// <b>Binding a device needs no permission, and that is deliberate.</b> A rep binds their own phone
/// as part of signing in on it; requiring a grant would mean a new starter cannot work until an
/// administrator notices. The authorisation that matters is the token: a device is bound to the
/// subject in it, and a rep can only ever bind or list their own.
/// <para>
/// <see cref="DeviceRevoke"/> is the administrator's side — revoking a device the rep no longer
/// holds, which by definition they cannot do from it.
/// </para>
/// </remarks>
public static class SyncPermissions
{
    public const string DeviceRead = "device:read";
    public const string DeviceRevoke = "device:revoke";
}
