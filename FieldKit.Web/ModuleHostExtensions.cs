using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Web;

/// <summary>Composition helpers the host uses to wire up its modules.</summary>
public static class ModuleHostExtensions
{
    /// <summary>Registers each module's services. Call during service configuration.</summary>
    public static IServiceCollection AddModules(
        this IServiceCollection services, IConfiguration configuration, params IReadOnlyList<IModule> modules)
    {
        foreach (var module in modules)
            module.AddModule(services, configuration);
        return services;
    }

    /// <summary>Maps each module's endpoints. Call after the app is built.</summary>
    public static IEndpointRouteBuilder MapModules(
        this IEndpointRouteBuilder endpoints, params IReadOnlyList<IModule> modules)
    {
        foreach (var module in modules)
            module.MapEndpoints(endpoints);
        return endpoints;
    }
}
