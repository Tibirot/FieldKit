using FieldKit.Infrastructure;
using FieldKit.Modules.Order.Contracts;
using FieldKit.Web;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Modules.Order;

/// <summary>
/// The Order module: what a rep sold at a counter (<c>ORD-01…15</c>) — the eleventh module.
/// </summary>
/// <remarks>
/// <para>
/// <b>The second module whose only write path belongs to another module</b>, after Audit and for the
/// same reason: an order is taken in-store, offline, inside a visit, and reaches this server through
/// <c>/sync/push</c>. <see cref="IOrderIngest"/> therefore ships with the module rather than a slice
/// later — a module nothing can write to is not a module yet.
/// </para>
/// <para>
/// <b>What slice 1 is not.</b> There is no pricing here: every amount stored is the device's, because
/// <c>BR-ORD-6</c> makes the device's number the record and the server's a note beside it (W11
/// slice 0). Slice 2 adds the recomputation. There is no assortment gate either — <c>BR-ORD-1</c>'s
/// answer is a <i>rejection the rep can fix</i>, which needs the re-open path, and both land in
/// slice 4. Nothing here submits, accepts or rejects: the aggregate stores what arrived already
/// sealed.
/// </para>
/// </remarks>
public sealed class OrderModule : IModule
{
    public string Name => "Order";

    /// <summary>
    /// None yet, and for a narrower reason than Audit's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing writes an order over HTTP</b>, so there is no <c>order:write</c> to define: an order
    /// arrives through <c>/sync/push</c> under the rep's own token, which <c>visit:write</c> already
    /// gates. A write permission nobody checks is worse than none — it appears in the role editor,
    /// gets granted, and means nothing.
    /// </para>
    /// <para>
    /// <b>Reading is <c>visit:read</c>'s for now, and unlike Audit that is a compromise rather than a
    /// conclusion.</b> An audit genuinely <i>is</i> what happened during a visit. An order is
    /// commercial, and a finance reader who should see order values without seeing a rep's movements
    /// is an entirely reasonable person to exist. They do not exist yet, and a realm role minted
    /// before its holder is a role nobody was granted — with the added cost, recorded in W10, that a
    /// realm change is not applied by deploying. See <c>OrderEndpoints</c>.
    /// </para>
    /// <para>
    /// The back office's Accept/Reject (<c>ORD-09</c>) is where a real <c>order:write</c> would be
    /// argued, and W11 slice 4 ships its API.
    /// </para>
    /// </remarks>
    public IReadOnlyList<PermissionDefinition> Permissions => [];

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("fieldkitdb")
            ?? throw new InvalidOperationException("Connection string 'fieldkitdb' is not configured.");

        services.AddModuleDbContext<OrderDbContext>(connectionString, OrderDbContext.SchemaName);
        services.AddHostedService<ModuleMigrator<OrderDbContext>>();

        // Sync applies pushed orders through this rather than writing the `ordering` schema (slice 5).
        services.AddScoped<IOrderIngest, OrderIngestService>();

        // …and reporting reads them through this.
        services.AddScoped<IOrderQuery, OrderQueryService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapOrderEndpoints();
}
