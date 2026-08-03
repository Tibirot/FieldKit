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

    /// <summary>
    /// The permissions this module owns (IAM spec §8). Default empty — a module that guards nothing
    /// declares nothing.
    /// </summary>
    /// <remarks>
    /// Declared on the module rather than through a separate registration so ownership is
    /// structural: the code that checks a permission and the code that declares it are the same
    /// assembly, and the catalogue cannot list a permission whose module was never composed.
    /// </remarks>
    IReadOnlyList<PermissionDefinition> Permissions => [];

    /// <summary>Register the module's services (its DbContext, handlers, options, …).</summary>
    void AddModule(IServiceCollection services, IConfiguration configuration);

    /// <summary>Map the module's HTTP endpoints (its slice of <c>/api</c>).</summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
