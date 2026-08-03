using FieldKit.Infrastructure;
using FieldKit.Web;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Modules.Org;

/// <summary>
/// The Organization module: the sales hierarchy, and later the territories hung off it.
/// </summary>
/// <remarks>
/// Org units (<c>ORG-01</c>) come first because everything else in this module attaches to them —
/// positions occupy a unit (<c>ORG-02</c>), and visibility scoping walks the tree they form
/// (<c>ORG-09</c>). It has no public contracts yet, deliberately: see the csproj.
/// </remarks>
public sealed class OrgModule : IModule
{
    public string Name => "Organization";

    public IReadOnlyList<PermissionDefinition> Permissions =>
    [
        new(OrgPermissions.OrgUnitRead, "View the sales hierarchy."),
        new(OrgPermissions.OrgUnitWrite, "Create, rename, move and delete org units."),
        new(OrgPermissions.PositionRead, "View who occupies which part of the sales hierarchy."),
        new(OrgPermissions.PositionWrite, "Assign people to org units and change their titles."),
    ];

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("fieldkitdb")
            ?? throw new InvalidOperationException("Connection string 'fieldkitdb' is not configured.");

        services.AddModuleDbContext<OrgDbContext>(connectionString, OrgDbContext.SchemaName);
        services.AddHostedService<ModuleMigrator<OrgDbContext>>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapOrgUnitEndpoints();
        endpoints.MapPositionEndpoints();
    }
}
