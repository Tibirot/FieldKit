using FieldKit.Infrastructure;
using FieldKit.Modules.Visit.Contracts;
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
/// <b>Two public contracts, and each only because a consumer asked.</b> Sync needs to apply visits a
/// device captured offline, so <c>IVisitIngest</c> exists (W8 slice 5); Audit needs to know whether a
/// visit is open before attaching work to it, so <c>IVisitContext</c> exists (W10 slice 3a).
/// <c>IVisitQuery</c> still does not — Order is W11.
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

        // Sync applies pushed work through this rather than writing the visit schema (W8 slice 5).
        services.AddScoped<IVisitIngest, VisitIngestService>();

        // …and Audit asks whether a visit exists, whose it is, and whether it is sealed, which is
        // every input `BR-AUD-6` needs (W10 slice 3a).
        services.AddScoped<IVisitContext, VisitContextService>();
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
