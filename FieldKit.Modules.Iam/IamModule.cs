using FieldKit.Infrastructure;
using FieldKit.Modules.Iam.Contracts;
using FieldKit.Web;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Modules.Iam;

/// <summary>The IAM module: registers its context, its public contracts and roles administration.</summary>
/// <remarks>
/// Roles (<c>IAM-04</c>) are here; users (<c>IAM-03</c>) are the next slice — roles come first
/// because a user without a role to hold is not a user the domain permits (BR-IAM-3).
/// </remarks>
public sealed class IamModule : IModule
{
    public string Name => "IAM";

    public IReadOnlyList<PermissionDefinition> Permissions =>
    [
        new(IamPermissions.RoleRead, "View roles and the permissions they grant."),
        new(IamPermissions.RoleWrite, "Create, edit and delete roles."),
        new(IamPermissions.UserRead, "View users and their assigned roles."),
        new(IamPermissions.UserWrite, "Invite users, edit their profile, and assign roles."),
    ];

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

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapRoleEndpoints();
}
