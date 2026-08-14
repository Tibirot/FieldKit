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
    /// One, and it is deliberately narrower than <c>order:write</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Still no <c>order:write</c>, because nothing writes an order over HTTP.</b> An order arrives
    /// through <c>/sync/push</c> under the rep's own token, which <c>visit:write</c> already gates. A
    /// write permission nobody checks is worse than none — it appears in the role editor, gets
    /// granted, and means nothing.
    /// </para>
    /// <para>
    /// <b><see cref="OrderPermissions.Reject"/> is the exception, and it names the act rather than the
    /// table</b> (W11 slice 4a). Rejecting is not editing: it refuses an order back to the rep and
    /// changes no line. Calling it <c>order:write</c> would promise a holder they could alter what a
    /// shopkeeper agreed to, which is the one thing <c>BR-ORD-4</c> forbids anybody from doing.
    /// </para>
    /// <para>
    /// <b>Reading is still <c>visit:read</c>'s, and that is a compromise rather than a conclusion.</b>
    /// An audit genuinely <i>is</i> what happened during a visit. An order is commercial, and a finance
    /// reader who should see order values without seeing a rep's movements is an entirely reasonable
    /// person to exist — they just do not, yet.
    /// </para>
    /// <para>
    /// <b>A new realm role is not free, and not applied by deploying.</b> The Keycloak import is
    /// <c>IGNORE_EXISTING</c> (W10's finding, <see href="../docs/engineering/deploying.md">the
    /// runbook</see>), so an existing environment needs the role added by hand before this endpoint
    /// answers anything but <c>403</c>. That cost is why <c>order:read</c> is still not minted.
    /// </para>
    /// </remarks>
    public IReadOnlyList<PermissionDefinition> Permissions =>
    [
        new(OrderPermissions.Reject, "Refuse a submitted order back to the rep for correction."),
    ];

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

        // The one that goes back down: what the back office made of an order, on the pull feed
        // (`BR-ORD-9`, regression F5) — W12 F5a.
        services.AddScoped<IOrderVerdictFeed, OrderVerdictFeed>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapOrderEndpoints();
}
