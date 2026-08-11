using FieldKit.Infrastructure;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Web;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Modules.Configuration;

/// <summary>
/// The permissions the Configuration module owns.
/// </summary>
/// <remarks>
/// One pair for the whole module rather than a pair per definition kind. Authoring custom fields,
/// visit workflows, survey forms and score weights is one job done by one person (Configuration spec
/// §2), and four pairs would be four ways to say the same thing. If a tenant ever wants the survey
/// author kept away from the field catalogue, splitting then is additive.
/// </remarks>
public static class ConfigurationPermissions
{
    public const string Read = "config:read";
    public const string Write = "config:write";
}

/// <summary>
/// The Configuration module: every tenant definition, owned in one place (ADR-0009 §0).
/// </summary>
/// <remarks>
/// Custom fields (<c>CFG-01</c>/<c>CFG-02</c>), visit workflows (<c>CFG-03</c>), survey forms
/// (<c>CFG-04</c>) and the perfect-store weighting (<c>CFG-05</c>) are here today. The versioned
/// configuration set arrives with the features that interpret it.
/// </remarks>
public sealed class ConfigurationModule : IModule
{
    public string Name => "Configuration";

    public IReadOnlyList<PermissionDefinition> Permissions =>
    [
        new(ConfigurationPermissions.Read, "View the tenant's definitions: custom fields, visit workflows, score weights."),
        new(ConfigurationPermissions.Write, "Define and edit them."),
    ];

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("fieldkitdb")
            ?? throw new InvalidOperationException("Connection string 'fieldkitdb' is not configured.");

        services.AddModuleDbContext<ConfigurationDbContext>(connectionString, ConfigurationDbContext.SchemaName);
        services.AddHostedService<ModuleMigrator<ConfigurationDbContext>>();

        // The public surface. Registered against the Contracts interface so consumers can only bind
        // to that — the implementation is internal to this module by convention (AT-2).
        services.AddScoped<IFieldDefinitionCatalog, FieldDefinitionCatalog>();

        // …and how a visit is worked in a channel, which is how Visit will ask (VIS-03).
        services.AddScoped<IVisitWorkflow, VisitWorkflowCatalog>();

        // Sync pages workflows to devices through this rather than reading the config schema
        // (W8 slice 8b). Separate from the catalog above: one answers "how is this channel worked",
        // the other "what changed since", and only the second needs a cursor.
        services.AddScoped<IVisitWorkflowFeed, VisitWorkflowFeed>();

        // …and what a tenant's questionnaires ask, which is how Audit will read them (AUD-04). Bound
        // one slice ahead of that module, exactly as IVisitWorkflow was bound ahead of check-in.
        services.AddScoped<ISurveyForms, SurveyFormCatalog>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapFieldDefinitionEndpoints();
        endpoints.MapVisitWorkflowEndpoints();
        endpoints.MapScoreWeightEndpoints();
        endpoints.MapSurveyFormEndpoints();
    }
}
