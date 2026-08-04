using FieldKit.Infrastructure;
using FieldKit.Modules.Outlets.Contracts;
using FieldKit.Modules.Outlets.Import;
using FieldKit.Web;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Modules.Outlets;

/// <summary>
/// The Outlets module: the retail locations the field app is organized around.
/// </summary>
/// <remarks>
/// Classification comes with the outlet rather than after it, because BR-OUT-1 makes a channel part
/// of what an outlet <i>is</i> — an outlet without one cannot be given an assortment, a price list or
/// a visit workflow, so there is no useful intermediate state where outlets exist unclassified.
/// </remarks>
public sealed class OutletsModule : IModule
{
    public string Name => "Outlets";

    public IReadOnlyList<PermissionDefinition> Permissions =>
    [
        new(OutletsPermissions.OutletRead, "View outlets and their classification."),
        new(OutletsPermissions.OutletWrite, "Create and edit outlets, and change their status."),
        new(OutletsPermissions.ChannelRead, "View the trade classifications outlets are grouped by."),
        new(OutletsPermissions.ChannelWrite, "Create, rename and delete trade channels."),
    ];

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("fieldkitdb")
            ?? throw new InvalidOperationException("Connection string 'fieldkitdb' is not configured.");

        services.AddModuleDbContext<OutletsDbContext>(connectionString, OutletsDbContext.SchemaName);
        services.AddHostedService<ModuleMigrator<OutletsDbContext>>();

        // The public surface. Registered against the Contracts interface so consumers can only bind
        // to that — the implementation is internal to this module by convention (AT-2).
        services.AddScoped<IOutletCatalog, OutletCatalog>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapChannelEndpoints();
        endpoints.MapOutletEndpoints();
        endpoints.MapOutletImportEndpoints();
    }
}
