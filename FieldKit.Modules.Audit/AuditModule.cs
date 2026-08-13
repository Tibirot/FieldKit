using FieldKit.Infrastructure;
using FieldKit.Modules.Audit.Contracts;
using FieldKit.Web;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Modules.Audit;

/// <summary>
/// The Audit module: what a rep measured at a shelf (<c>AUD-01…12</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The first module whose only write path belongs to another module.</b> An audit is worked
/// in-store, offline, inside a visit, and reaches this server through <c>/sync/push</c> — so
/// <see cref="IAuditIngest"/> ships with the module rather than a slice later, unlike Visit's own
/// contracts. A module nothing can write to is not a module yet.
/// </para>
/// <para>
/// <b>It stores no score.</b> <see cref="PerfectStoreScore"/> derives one from an audit's
/// measurements plus the weight version it recorded (<c>BR-AUD-8</c>), and it is a pure function
/// rather than a column: a stored score would be a second answer that could disagree with the
/// recomputation that rule promises. Slice 6 is what calls it on the way in.
/// </para>
/// <para>
/// Survey answers (<c>AUD-04</c>) and photo references (<c>AUD-05</c>) arrived in W10 slice 3b, on
/// the same aggregate. An audit names its questionnaire by id and this module confirms the tenant has
/// it — the only question it asks Configuration. It deliberately does not ask whether the answers
/// <i>satisfy</i> the form: <c>BR-AUD-7</c> is enforced on the device, where the rep is looking at it.
/// </para>
/// </remarks>
public sealed class AuditModule : IModule
{
    public string Name => "Audit";

    /// <summary>
    /// None, and that is the decision rather than an omission.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing writes an audit over HTTP</b>, so there is no <c>audit:write</c> to define: an
    /// audit arrives through <c>/sync/push</c> under the rep's own token, which <c>visit:write</c>
    /// already gates. A write permission nobody checks is worse than none — it appears in the role
    /// editor, gets granted, and means nothing.
    /// </para>
    /// <para>
    /// <b>And reading is <c>visit:read</c>'s</b>, not a permission of its own. An audit <i>is</i>
    /// what happened during a visit; a supervisor who may see where a rep checked in from and how
    /// long they stayed is not a different person from one who may see what they counted on the
    /// shelf. Splitting them would define a capability every role that could use it already implies,
    /// and — because permissions are realm roles — would need a Keycloak change before any existing
    /// tenant could grant it.
    /// </para>
    /// <para>
    /// If a tenant ever wants merchandising data kept from a supervisor who can see visits, that is a
    /// real requirement and this is where it lands. No requirement asks for it today.
    /// </para>
    /// </remarks>
    public IReadOnlyList<PermissionDefinition> Permissions => [];

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("fieldkitdb")
            ?? throw new InvalidOperationException("Connection string 'fieldkitdb' is not configured.");

        services.AddModuleDbContext<AuditDbContext>(connectionString, AuditDbContext.SchemaName);
        services.AddHostedService<ModuleMigrator<AuditDbContext>>();

        // Sync applies pushed audits through this rather than writing the audit schema (W10 slice 6).
        services.AddScoped<IAuditIngest, AuditIngestService>();

        // …and reporting reads them through this (AUD-09).
        services.AddScoped<IAuditQuery, AuditQueryService>();

        // The upload's other end: Sync tells an audit that a photograph it references has arrived
        // (`OFF-08`, W11 slice 13a). Separate from ingest so a caller that only wants to say "the
        // bytes are there" does not acquire the ability to file audits.
        services.AddScoped<IPhotoEvidence, PhotoEvidenceService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapAuditEndpoints();
}
