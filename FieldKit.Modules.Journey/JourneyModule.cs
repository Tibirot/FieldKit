using FieldKit.Infrastructure;
using FieldKit.Web;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Modules.Journey;

/// <summary>
/// The Journey module: where a rep goes and when (<c>JRN-01…06</c>).
/// </summary>
/// <remarks>
/// <para>
/// Call frequency comes first because everything else in this module is derived from it — the
/// generator turns frequency × territory × calendar into planned visits (<c>JRN-03</c>), and the
/// compliance metric asks whether an outlet got the visits its frequency said it should
/// (<c>BR-JRN-6</c>). It is also the only part a supervisor configures by hand, so it is the part
/// that exists before there is anything to generate.
/// </para>
/// <para>
/// <b>No public contracts yet</b>, deliberately — see the csproj. <c>IJourneyQuery</c> is specified
/// and has no consumer until Visit exists.
/// </para>
/// </remarks>
public sealed class JourneyModule : IModule
{
    public string Name => "Journey";

    public IReadOnlyList<PermissionDefinition> Permissions =>
    [
        new(JourneyPermissions.Read, "View call frequencies and journey plans."),
        new(JourneyPermissions.Write, "Set call frequencies and generate journey plans."),
    ];

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("fieldkitdb")
            ?? throw new InvalidOperationException("Connection string 'fieldkitdb' is not configured.");

        services.AddModuleDbContext<JourneyDbContext>(connectionString, JourneyDbContext.SchemaName);
        services.AddHostedService<ModuleMigrator<JourneyDbContext>>();

        // Internal to the module: its only caller is generation, which lives here too.
        services.AddScoped<FrequencyResolver>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapFrequencyEndpoints();
}

/// <summary>
/// The permissions this module owns, as <c>resource:action</c> strings.
/// </summary>
/// <remarks>
/// Constants rather than literals so a rename is a compile error rather than a silently open
/// endpoint. Named after the resource — a journey — rather than after any one screen: the same pair
/// will guard generation and the published plan.
/// </remarks>
public static class JourneyPermissions
{
    public const string Read = "journey:read";
    public const string Write = "journey:write";
}
