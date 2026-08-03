using FieldKit.Infrastructure;
using FieldKit.Modules.Iam.Contracts;
using FieldKit.Web;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Modules.Iam;

/// <summary>The IAM module: registers its context and its public contracts.</summary>
/// <remarks>
/// No endpoints yet — this slice establishes the module, its schema and the contracts other modules
/// will depend on. Users &amp; roles administration (IAM-03/04) is the next one.
/// </remarks>
public sealed class IamModule : IModule
{
    public string Name => "IAM";

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("fieldkitdb")
            ?? throw new InvalidOperationException("Connection string 'fieldkitdb' is not configured.");

        services.AddModuleDbContext<IamDbContext>(connectionString, IamDbContext.SchemaName);
        services.AddHostedService<ModuleMigrator<IamDbContext>>();

        // The public surface. Registered against the Contracts interfaces so consumers can only bind
        // to those — the implementations are internal to this module by convention (AT-2).
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<ITenantRegistry, TenantRegistry>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Intentionally empty for now — see the remarks above.
    }
}
