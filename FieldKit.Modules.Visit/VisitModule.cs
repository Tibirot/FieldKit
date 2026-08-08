using FieldKit.Infrastructure;
using FieldKit.Web;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Modules.Visit;

/// <summary>
/// The Visit module: what happens when a rep is at a shop (<c>VIS-01…07</c>).
/// </summary>
/// <remarks>
/// <para>
/// Check-in comes first because everything else in this module hangs off it — steps belong to a
/// visit (<c>VIS-03</c>), check-out closes one (<c>VIS-05</c>), and both need something to attach to.
/// It is also where the module's one genuinely contested rule lives: <c>BR-VIS-2</c>'s "never block
/// the rep, always record".
/// </para>
/// <para>
/// <b>No public contracts yet</b>, deliberately — see the csproj. <c>IVisitContext</c> and
/// <c>IVisitQuery</c> are consumed by Audit and Order, which are Phase 3.
/// </para>
/// </remarks>
public sealed class VisitModule : IModule
{
    public string Name => "Visit";

    public IReadOnlyList<PermissionDefinition> Permissions =>
    [
        new(VisitPermissions.Read, "View visits and where they were checked in."),
        new(VisitPermissions.Write, "Check in at an outlet and work a visit."),
    ];

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("fieldkitdb")
            ?? throw new InvalidOperationException("Connection string 'fieldkitdb' is not configured.");

        services.AddModuleDbContext<VisitDbContext>(connectionString, VisitDbContext.SchemaName);
        services.AddHostedService<ModuleMigrator<VisitDbContext>>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapVisitEndpoints();
}

/// <summary>
/// The permissions this module owns, as <c>resource:action</c> strings.
/// </summary>
/// <remarks>
/// <b><see cref="Write"/> is a rep's permission, not an administrator's</b> — unlike every other
/// write in this system. Checking in *is* the field job, so the role that holds it is Field Rep;
/// what sales ops and supervisors get is <see cref="Read"/>, because reviewing where somebody
/// checked in from is oversight and performing a visit is not something you do on their behalf.
/// </remarks>
public static class VisitPermissions
{
    public const string Read = "visit:read";
    public const string Write = "visit:write";
}
