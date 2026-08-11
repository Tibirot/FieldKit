using FieldKit.Modules.Audit.Contracts;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FieldKit.Modules.Audit;

public sealed record AvailabilityResponse(Guid ProductId, string Status);

public sealed record FacingsResponse(Guid ProductId, int Facings);

/// <summary>One price check, with the delta computed rather than stored.</summary>
/// <param name="DeltaMinorUnits">
/// Observed minus expected, or null when nothing was expected. Positive means the shop is charging
/// over. <b>Not a compliance verdict</b> — <c>BR-AUD-3</c>'s tolerance is tenant configuration, and
/// the score is W10 slice 4's.
/// </param>
public sealed record PriceCheckResponse(
    Guid ProductId,
    long ObservedMinorUnits,
    long? ExpectedMinorUnits,
    long? DeltaMinorUnits,
    string Currency);

/// <summary>An audit, as stored.</summary>
public sealed record AuditResponse(
    Guid Id,
    Guid VisitId,
    Guid OutletId,
    string UserId,
    DateTimeOffset CapturedAtUtc,
    int WeightSetVersion,
    int? CategoryFacings,
    IReadOnlyList<AvailabilityResponse> Availability,
    IReadOnlyList<FacingsResponse> Facings,
    IReadOnlyList<PriceCheckResponse> Prices);

/// <summary>
/// Reading audits (<c>AUD-09</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads only, and that is the whole surface.</b> An audit is created exactly one way — a device
/// pushes it through <see cref="IAuditIngest"/> — because an audit is worked at a shelf with no
/// signal (audits §7). A live REST capture endpoint would be an API no planned screen calls and a
/// second door into a record that is meant to be append-only.
/// </para>
/// <para>
/// <b>Routed under the thing being asked about</b> rather than under <c>/api/audits</c>: nobody asks
/// "show me audit 4f2c", they ask what happened during a visit or how a shop has been trending.
/// </para>
/// </remarks>
internal static class AuditEndpoints
{
    /// <summary>How many of an outlet's audits are returned when the caller does not say.</summary>
    private const int DefaultOutletAudits = 20;

    /// <summary>
    /// The permission that governs reading an audit — Visit's, not one of Audit's own.
    /// </summary>
    /// <remarks>
    /// An audit <i>is</i> what happened during a visit, and a supervisor who may see where a rep
    /// checked in from is not a different person from one who may see what they counted. See
    /// <see cref="AuditModule.Permissions"/> for the whole argument.
    /// <para>
    /// A literal string because <c>VisitPermissions</c> lives in Visit's implementation assembly,
    /// which this module may not reference (AT-1) — the same shape <c>SystemRoleTemplates</c> is
    /// forced into, and for the same reason.
    /// </para>
    /// </remarks>
    private const string VisitRead = "visit:read";

    public static void MapAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var audits = endpoints.MapGroup("/api").WithTags("Audit");

        audits.MapGet("/visits/{visitId:guid}/audit", async (
            Guid visitId, IAuditQuery query, CancellationToken ct) =>
        {
            var audit = await query.ForVisitAsync(visitId, ct);

            // A visit with no audit and a visit that does not exist answer the same 404. Audit
            // cannot tell them apart without reading Visit's schema, and the difference is not one
            // this reader can act on.
            return audit is null ? Results.NotFound() : Results.Ok(Respond(audit));
        }).RequirePermission(VisitRead);

        audits.MapGet("/outlets/{outletId:guid}/audits", async (
            Guid outletId, int? limit, IAuditQuery query, CancellationToken ct) =>
        {
            var found = await query.ForOutletAsync(outletId, limit ?? DefaultOutletAudits, ct);

            return found.Select(Respond).ToList();
        }).RequirePermission(VisitRead);
    }

    private static AuditResponse Respond(AuditRecord audit) => new(
        audit.AuditId,
        audit.VisitId,
        audit.OutletId,
        audit.UserId,
        audit.CapturedAtUtc,
        audit.WeightSetVersion,
        audit.CategoryFacings,
        [.. audit.Availability.Select(line =>
            new AvailabilityResponse(line.ProductId, line.Status.ToString()))],
        [.. audit.Facings.Select(line => new FacingsResponse(line.ProductId, line.Facings))],
        [.. audit.Prices.Select(line => new PriceCheckResponse(
            line.ProductId,
            line.ObservedMinorUnits,
            line.ExpectedMinorUnits,
            line.ExpectedMinorUnits is { } expected ? line.ObservedMinorUnits - expected : null,
            line.Currency))]);
}
