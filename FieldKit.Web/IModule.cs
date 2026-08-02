using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Web;

/// <summary>
/// A module registers its own services and endpoints — the host only composes modules, it does not
/// know how they work (module boundaries §1). The host calls <see cref="AddModule"/> during service
/// registration and <see cref="MapEndpoints"/> after the app is built.
/// </summary>
public interface IModule
{
    /// <summary>The module's name (for diagnostics / logging).</summary>
    string Name { get; }

    /// <summary>Register the module's services (its DbContext, handlers, options, …).</summary>
    void AddModule(IServiceCollection services, IConfiguration configuration);

    /// <summary>Map the module's HTTP endpoints (its slice of <c>/api</c>).</summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
