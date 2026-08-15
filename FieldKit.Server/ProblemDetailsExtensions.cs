using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;

namespace FieldKit.Server;

/// <summary>
/// How the API answers a request it could not read.
/// </summary>
/// <remarks>
/// <para>
/// ASP.NET already decides that a body it cannot bind is a <b>400</b>: minimal APIs raise a
/// <see cref="BadHttpRequestException"/>, which carries its own status code for exactly that reason.
/// What was missing is that <c>UseExceptionHandler</c> caught it first and reported 500, because the
/// handler's default is "any exception means the server broke" — and a status code the exception was
/// carrying is not a default that applies.
/// </para>
/// <para>
/// The difference is not cosmetic. A 500 tells a caller their payload was fine and this API is
/// unreliable, which is the opposite of true, and it sends them to file a bug instead of fixing a
/// typo. It is also the number that pages someone: a device syncing a body with one bad enum name
/// would have raised an incident for a client-side mistake.
/// </para>
/// </remarks>
public static class ProblemDetailsExtensions
{
    /// <summary>
    /// Registers problem details that say <i>where</i> an unreadable body went wrong.
    /// </summary>
    /// <remarks>
    /// The detail is built from the parser's own JSON path rather than its message. The message names
    /// the .NET type it failed to construct — <c>FieldKit.Modules.…CustomFieldType</c> — which tells a
    /// caller nothing they can act on and tells everyone else how the server is put together. The
    /// path (<c>$.type</c>) is the part that is actually about their request.
    /// </remarks>
    public static IHostApplicationBuilder AddRequestProblemDetails(this IHostApplicationBuilder builder)
    {
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            /*
             * The same trace id, spelled the same way, on the one response shape that is *not* the
             * `{ "errors": [...] }` envelope (W13 slice 2).
             *
             * Two shapes are two shapes; what must not also differ is the name and the format of the
             * id inside them. Set explicitly rather than left to the framework's default, which is
             * `Activity.Id` — the W3C `traceparent`, identifying this one span. Both would be called
             * `traceId` and only one of them finds the request in a trace viewer.
             */
            if (Activity.Current?.TraceId is { } trace && trace != default)
                context.ProblemDetails.Extensions["traceId"] = trace.ToString();

            // Only for a body we could not read. A 500's message is about our internals and stays in
            // the logs, where the trace id already points.
            if (context.Exception is BadHttpRequestException { InnerException: JsonException json })
            {
                // "$" is the body itself rather than a field in it — what the parser reports when the
                // whole thing is unreadable or the wrong shape. Naming it would tell a caller their
                // "$" was wrong, which is not a thing they wrote.
                context.ProblemDetails.Detail = json.Path is { Length: > 0 } path and not "$"
                    ? $"The request body could not be read: {path} is not a value this endpoint accepts."
                    : "The request body could not be read as JSON.";
            }
        });

        return builder;
    }

    /// <summary>
    /// Handles unhandled exceptions, letting one that carries a status code keep it.
    /// </summary>
    public static void UseRequestExceptionHandler(this WebApplication app) =>
        app.UseExceptionHandler(new ExceptionHandlerOptions { StatusCodeSelector = StatusCodeFor });

    /// <summary>
    /// The status an unhandled <paramref name="exception"/> is reported as.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow, and named rather than inlined so the narrowness can be asserted directly.
    /// <b>Only</b> <see cref="BadHttpRequestException"/> chooses its own status, and it can only ever
    /// choose a 4xx — 400 for a body that will not parse, 415 for a media type this API does not
    /// read. Everything else stays a 500.
    ///
    /// The temptation with a fix like this is to widen it until nothing pages anyone. That trades one
    /// silent failure for a worse one: a genuine server fault reported as the caller's problem is a
    /// fault nobody ever investigates.
    /// </remarks>
    public static int StatusCodeFor(Exception exception) =>
        exception is BadHttpRequestException badRequest
            ? badRequest.StatusCode
            : StatusCodes.Status500InternalServerError;
}
