using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FieldKit.Modules.Outlets.Import;

/// <summary>
/// Bulk import of the outlet base (<c>OUT-05</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The file is the body, and its content type chooses the reader.</b> Not multipart: there are no
/// other parts — the mode and the dry-run flag are query parameters, because they are how to run the
/// import rather than part of what is being imported. That leaves <c>Content-Type</c> free to do the
/// one job it is for, and makes the follow-up formats a routing decision instead of a new endpoint:
/// <c>application/json</c> and the Excel media type slot in beside <c>text/csv</c>.
/// </para>
/// <para>
/// <b>Permission is <c>outlet:write</c>.</b> Four thousand outlets is more volume than one, not a
/// different capability, and an <c>outlet:import</c> permission would mean touching every role
/// template and both realms to express something the existing one already says. Note what this
/// implies and is meant to: the importer cannot create channels, because creating a channel needs
/// <c>channel:write</c> and this path does not have it.
/// </para>
/// </remarks>
internal static class OutletImportEndpoints
{
    private const string Csv = "text/csv";

    public static void MapOutletImportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/outlets/import", async (
                HttpRequest http,
                OutletImportMode? mode,
                bool? dryRun,
                OutletsDbContext db,
                IFieldDefinitionCatalog fields,
                CancellationToken ct) =>
            {
                // AllOrNothing by omission: of two modes that are each right for some file, the one
                // to get by default is the one that cannot half-apply.
                var chosen = mode ?? OutletImportMode.AllOrNothing;

                if (!IsCsv(http.ContentType))
                {
                    // The one refusal that is neither 400 nor 409 — same envelope all the same,
                    // because a client should read every refusal the same way.
                    return Problems.Refuse(
                        StatusCodes.Status415UnsupportedMediaType,
                        $"Send the file as {Csv}. JSON and Excel are not read yet.");
                }

                // Buffered first, because the reader is synchronous and ASP.NET refuses synchronous
                // reads of the request stream (AllowSynchronousIO is false, and rightly — a blocked
                // thread per upload is how a server stops answering under load). Buffering is honest
                // here rather than a workaround: the row cap already bounds what may arrive.
                using var body = new MemoryStream();
                await http.Body.CopyToAsync(body, ct);
                body.Position = 0;

                if (!CsvOutletImportReader.TryRead(body, out var file, out var problem))
                {
                    // A whole-file failure, kept apart from row failures: a file that is not CSV has
                    // nothing to say per row, and handing back 4,000 identical errors would bury the
                    // one fact that matters.
                    return Problems.BadRequest(problem!);
                }

                if (file.Rows.Count == 0)
                {
                    return Problems.BadRequest("The file has a header but no rows.");
                }

                if (file.Rows.Count > OutletImportFormat.MaxRows)
                {
                    return Problems.BadRequest(
                        $"This import takes at most {OutletImportFormat.MaxRows:N0} rows at a time; "
                            + $"the file has {file.Rows.Count:N0}.");
                }

                var result = await OutletImporter.RunAsync(file, chosen, dryRun ?? false, db, fields, ct);

                // 200 even when every row was refused. The import ran and this is its result — a 400
                // would say the request was malformed, which is a different thing from a file full of
                // problems that the response describes row by row.
                return Results.Ok(result);
            })
            .RequirePermission(OutletsPermissions.OutletWrite)
            .Accepts<string>(Csv)
            .WithTags("Outlets");
    }

    /// <summary>
    /// Whether the body is CSV, allowing for the charset a browser appends.
    /// </summary>
    /// <remarks>
    /// A file input posted from a browser arrives as <c>text/csv; charset=UTF-8</c>, and an exact
    /// string comparison would refuse the exact request the import screen is going to make.
    /// </remarks>
    private static bool IsCsv(string? contentType) =>
        contentType is not null && contentType.Split(';')[0].Trim()
            .Equals(Csv, StringComparison.OrdinalIgnoreCase);
}
