using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace FieldKit.Web;

/// <summary>
/// One thing wrong with a request, and which part of it.
/// </summary>
/// <param name="Field">
/// The JSON path in the request the caller sent — <c>code</c>, <c>channelId</c>,
/// <c>customFields.chiller_count</c>. Null when the problem is about the request as a whole rather
/// than a field in it, which a form should show at the top rather than beside a control.
/// </param>
/// <param name="Message">
/// <b>English, always — a fallback, not the localized string</b> (<c>ADR-0012</c>). It is what a
/// client that does not recognise <paramref name="Code"/> shows, what a <c>.http</c> response reads
/// as, and what a test asserts on. A client rendering this to someone who chose Romanian is using it
/// wrong; it should resolve <paramref name="Code"/> through the message catalogs instead.
/// </param>
/// <param name="Code">
/// A stable identifier for <i>which rule</i> was broken — <c>channel.name.taken</c>,
/// <c>outlet.customField.tooLong</c>. Resource-first and dotted, mirroring the
/// <c>resource:action</c> shape of permission strings.
/// <para>
/// <b>Part of the API contract.</b> Renaming one is a breaking change, the same as renaming a field.
/// Null on endpoints not yet migrated — Organization, Outlets, IAM and Configuration, at the time of
/// writing — where the client falls back to <paramref name="Message"/> and reads English. Products
/// emits a code on every refusal; it was written that way rather than migrated, since W6 is the week
/// ADR-0012 was decided for.
/// </para>
/// </param>
/// <param name="Args">
/// The values <paramref name="Code"/>'s message interpolates, named. Codes alone are not enough:
/// more than half of this API's refusals interpolate something, and <c>"at most {max} characters"</c>
/// without <c>max</c> is not a message.
/// <para>
/// Values are <b>strings</b>, deliberately. A number here would be JSON-coerced to float64 in the
/// browser, which is the same class of bug <c>Money</c> crosses the wire as a string to avoid
/// (<c>BR-PRD-8</c>). Formatting a number for display is the catalog's job, and it has the locale.
/// </para>
/// </param>
public sealed record FieldProblem(
    string? Field,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Code = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string>? Args = null);

/// <summary>
/// How a write endpoint refuses.
/// </summary>
/// <remarks>
/// <para>
/// <b>The field is the point.</b> Endpoints used to answer with prose — <c>{ "error": "A territory
/// needs a name." }</c> — which reads perfectly and tells a form nothing about *where* to put it. A
/// screen could only list sentences above a page of inputs and leave someone to work out which of
/// eleven fields each was about, or re-declare the rules client-side to produce its own field-keyed
/// errors, which is a second copy of what the server owns.
/// </para>
/// <para>
/// The bulk import already answered this way — <c>{ row, column, message }</c> — and this is the same
/// idea for a request with no rows. Naming it after the request's own JSON path, rather than a
/// column or a form control, keeps it something the API can promise: the caller sent
/// <c>channelId</c>, so <c>channelId</c> is what it is told about.
/// </para>
/// <para>
/// The envelope stays <c>{ "errors": [...] }</c> for every refusal, including the single-problem
/// case. One shape means a client writes one branch, and an endpoint that grows a second rule later
/// does not change how callers read it.
/// </para>
/// </remarks>
public static class Problems
{
    /// <summary>
    /// The refusal envelope, with the trace this response belongs to (W13 slice 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The correlation id, on the responses that actually carry one.</b>
    /// <c>observability §4</c> said a <c>traceId</c> "is returned in every <c>ProblemDetails</c>" —
    /// and this API does not answer with <c>ProblemDetails</c>. Every refusal it raises deliberately
    /// uses the <c>{ "errors": [...] }</c> envelope above (<c>api-contracts §3</c>), which had no
    /// trace id in it; the W13 slice 0 audit found the word did not occur anywhere in the repository.
    /// So the claim is made true where the refusals are, rather than where the doc guessed they were.
    /// </para>
    /// <para>
    /// <b>The 32-hex trace id, not the W3C <c>traceparent</c>.</b> <c>Activity.Id</c> is
    /// <c>00-{trace}-{span}-01</c> — it identifies this one span, and pasting it into a trace viewer
    /// asks the wrong question. What a person reads down a phone line, and what finds every span of
    /// the request, is the trace id alone.
    /// </para>
    /// <para>
    /// <b>Absent rather than empty when nothing is tracing.</b> With no listener there is no activity,
    /// which is the ordinary state of a unit test — and <c>"traceId": ""</c> is a value somebody will
    /// eventually paste into a search box and get nothing back from. A missing property says "this
    /// response was not traced"; an empty one says "it was, and here is the id", falsely.
    /// </para>
    /// </remarks>
    private static object Envelope(params FieldProblem[] problems) => Envelope((IReadOnlyList<FieldProblem>)problems);

    private static object Envelope(IReadOnlyList<FieldProblem> problems) =>
        Activity.Current?.TraceId is { } trace && trace != default
            ? new { errors = problems, traceId = trace.ToString() }
            : new { errors = problems };

    /// <summary>Refuses a request because of one field.</summary>
    public static IResult BadRequest(string field, string message) =>
        Results.BadRequest(Envelope(new FieldProblem(field, message)));

    /// <summary>Refuses a request for a reason that is not about any one field.</summary>
    public static IResult BadRequest(string message) =>
        Results.BadRequest(Envelope(new FieldProblem(null, message)));

    /// <summary>
    /// Refuses a request, naming the rule that was broken (<c>ADR-0012</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="code"/> is required rather than optional, and that is what keeps this
    /// unambiguous with the two overloads above: they top out at two arguments, so a three-argument
    /// call can only mean this one. An optional <c>code</c> on <c>BadRequest(string, string)</c>
    /// would collide with <c>BadRequest(string)</c> — <c>("a", "b")</c> could be
    /// <c>(field, message)</c> or <c>(message, code)</c>, and the compiler cannot choose.
    /// </para>
    /// <para>
    /// Pass <c>null</c> for <paramref name="field"/> when the problem is about the request as a
    /// whole. The prose overloads stay because migration is module by module — an endpoint that has
    /// not been given a code keeps working exactly as it does now.
    /// </para>
    /// </remarks>
    public static IResult BadRequest(
        string? field, string message, string code, IReadOnlyDictionary<string, string>? args = null) =>
        Results.BadRequest(Envelope(new FieldProblem(field, message, code, args)));

    /// <summary>
    /// Refuses a request with everything wrong with it.
    /// </summary>
    /// <remarks>
    /// All of them, not the first: someone filling a form wants to fix everything in one pass, and
    /// returning one problem at a time turns a six-field form into six round trips.
    /// </remarks>
    public static IResult BadRequest(IReadOnlyList<FieldProblem> problems) =>
        Results.BadRequest(Envelope(problems));

    /// <summary>
    /// Refuses a write that collides with something already stored.
    /// </summary>
    /// <remarks>
    /// A 409 rather than a 400 because the request was well-formed and the world disagreed — but it
    /// still names a field, since the thing a form wants to do with "that code is taken" is put it
    /// under the code box.
    /// </remarks>
    public static IResult Conflict(string field, string message) =>
        Results.Conflict(Envelope(new FieldProblem(field, message)));

    /// <summary>Refuses a write that collides with something already stored, with no one field at fault.</summary>
    public static IResult Conflict(string message) =>
        Results.Conflict(Envelope(new FieldProblem(null, message)));

    /// <summary>Refuses a colliding write, naming the rule (<c>ADR-0012</c>).</summary>
    public static IResult Conflict(
        string? field, string message, string code, IReadOnlyDictionary<string, string>? args = null) =>
        Results.Conflict(Envelope(new FieldProblem(field, message, code, args)));

    /// <summary>
    /// Refuses a request with a status this class has no name for.
    /// </summary>
    /// <remarks>
    /// The envelope belongs here even for the one-off statuses — a 415 hand-rolling its own
    /// <c>{ "errors": [...] }</c> at the call site is how a second shape gets in, and one shape for
    /// every refusal is the whole point.
    /// </remarks>
    public static IResult Refuse(int statusCode, string message) =>
        Results.Json(Envelope(new FieldProblem(null, message)), statusCode: statusCode);

    /// <summary>Refuses with an unnamed status, naming the rule (<c>ADR-0012</c>).</summary>
    public static IResult Refuse(
        int statusCode, string message, string code, IReadOnlyDictionary<string, string>? args = null) =>
        Results.Json(Envelope(new FieldProblem(null, message, code, args)), statusCode: statusCode);
}
