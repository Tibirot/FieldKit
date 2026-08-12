using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Configuration;

/// <summary>One question, as an admin sets it. No order — position in the list is the order.</summary>
/// <remarks>
/// <para>
/// The type travels as its name, and the converter is opt-in per property because nothing registers
/// a global one. Without it this API would accept only the ordinal — which is exactly what the visit
/// workflow's step type did until somebody posted <c>"Audit"</c> and got a 400.
/// </para>
/// <para>
/// <b><see cref="Mandatory"/> and <see cref="Options"/> carry defaults, and the rest deliberately do
/// not.</b> The server runs with <c>RespectRequiredConstructorParameters</c>, so a positional
/// parameter without one is <i>required on the wire</i> — a nullable type is not enough. Most of a
/// text question's JSON would otherwise be <c>"options": null</c>. Key, text and type stay required
/// because a question missing any of them is not a question, and that is precisely the mistake the
/// setting exists to catch.
/// </para>
/// <para>
/// Defaulting <see cref="Mandatory"/> to <c>false</c> is the safe direction: a question that is
/// optional by omission costs an unanswered box, and one that is mandatory by omission blocks a rep's
/// check-out over a flag nobody typed.
/// </para>
/// </remarks>
public sealed record SurveyQuestionRequest(
    string Key,
    string Text,
    [property: JsonConverter(typeof(JsonStringEnumConverter<SurveyQuestionType>))] SurveyQuestionType Type,
    bool Mandatory = false,
    IReadOnlyList<string>? Options = null);

/// <summary>A survey form, as an admin sets it.</summary>
public sealed record SurveyFormRequest(string Name, IReadOnlyList<SurveyQuestionRequest> Questions);

public sealed record SurveyQuestionResponse(
    int Order, string Key, string Text, string Type, bool Mandatory, IReadOnlyList<string> Options);

/// <summary>A survey form, as stored.</summary>
public sealed record SurveyFormResponse(
    Guid Id, string Name, IReadOnlyList<SurveyQuestionResponse> Questions);

/// <summary>
/// The tenant's survey forms (<c>AUD-04</c>, <c>CFG-04</c>, <c>BR-AUD-7</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>POST to create, PUT by id to replace</b> — not PUT by a natural key, which is how the visit
/// workflow works. A workflow's natural key is the channel; a form's is nothing, because a tenant
/// has several and renaming one must not create a second.
/// </para>
/// <para>
/// <b>Questions are replaced wholesale</b> — see <see cref="SurveyForm"/> for why an ordered thing
/// cannot sensibly be patched, and why an answer is therefore filed under a key rather than an id.
/// </para>
/// </remarks>
internal static class SurveyFormEndpoints
{
    /// <summary>
    /// What a question key may look like.
    /// </summary>
    /// <remarks>
    /// The same pattern custom-field keys use, and for the same reason: this goes into the JSON an
    /// answer is stored in and into whatever <c>AUD-09</c> groups by, so it is an identifier rather
    /// than prose. An admin never types it — slice 9's screen derives it from the question text, the
    /// way the custom-field screen already does.
    /// </remarks>
    private const string KeyPattern = "^[a-z][a-z0-9_]{0,59}$";

    public static void MapSurveyFormEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var forms = endpoints.MapGroup("/api/config/surveys").WithTags("Configuration");

        forms.MapGet("/", async (ISurveyForms catalog, CancellationToken ct) =>
        {
            var all = await catalog.AllAsync(ct);

            return all.Select(Respond).ToList();
        }).RequirePermission(ConfigurationPermissions.Read);

        forms.MapGet("/{formId:guid}", async (
            Guid formId, ISurveyForms catalog, CancellationToken ct) =>
        {
            var form = await catalog.ByIdAsync(formId, ct);

            return form is null ? Results.NotFound() : Results.Ok(Respond(form));
        }).RequirePermission(ConfigurationPermissions.Read);

        forms.MapPost("/", async (
            SurveyFormRequest request, ConfigurationDbContext db, CancellationToken ct) =>
        {
            if (Problem(request) is { } problem) return problem;

            if (await NameTakenAsync(db, request.Name, null, ct)) return NameTaken(request.Name);

            var (form, refusal) = SurveyForm.Create(request.Name, Questions(request));
            if (refusal is not SurveyFormRefusal.None) return Refuse(refusal);

            db.SurveyForms.Add(form!);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/config/surveys/{form!.Id}", Respond(form.Describe()));
        }).RequirePermission(ConfigurationPermissions.Write);

        forms.MapPut("/{formId:guid}", async (
            Guid formId, SurveyFormRequest request, ConfigurationDbContext db, IClock clock,
            CancellationToken ct) =>
        {
            if (Problem(request) is { } problem) return problem;

            var form = await db.SurveyForms
                .Include(candidate => candidate.Questions)
                .SingleOrDefaultAsync(candidate => candidate.Id == formId, ct);

            if (form is null) return Results.NotFound();

            if (await NameTakenAsync(db, request.Name, formId, ct)) return NameTaken(request.Name);

            var refusal = form.Set(request.Name, Questions(request), clock);
            if (refusal is not SurveyFormRefusal.None) return Refuse(refusal);

            await db.SaveChangesAsync(ct);

            return Results.Ok(Respond(form.Describe()));
        }).RequirePermission(ConfigurationPermissions.Write);

        forms.MapDelete("/{formId:guid}", async (
            Guid formId, ConfigurationDbContext db, CancellationToken ct) =>
        {
            var form = await db.SurveyForms
                .SingleOrDefaultAsync(candidate => candidate.Id == formId, ct);

            if (form is null) return Results.NotFound();

            /*
             * Deleted outright, and the answers already given under it stay where they are.
             *
             * The same call the custom-field catalogue makes: the answers live in another module's
             * rows and Configuration may not read them (ADR-0005), so this cannot be refused on their
             * behalf even if it wanted to be. It stops the form being asked; it is not a redaction.
             *
             * This comment used to say "refusing to delete a form in use is slice 3's rule to add".
             * That was wrong, and the boundary is why: W10 slice 3b now has audits pointing at a form
             * by id, and Configuration still cannot see them. A synchronous "is this in use" check
             * would mean reading Audit's schema; the honest alternative is an integration event, and
             * no requirement asks for one. What an audit does instead is carry each question's text
             * as it was asked, so its answers stay readable after the form is gone.
             */
            db.SurveyForms.Remove(form);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }).RequirePermission(ConfigurationPermissions.Write);
    }

    private static IReadOnlyList<SurveyQuestionDraft> Questions(SurveyFormRequest request) =>
        [.. request.Questions.Select(question => new SurveyQuestionDraft(
            question.Key, question.Text, question.Type, question.Mandatory, question.Options))];

    /// <summary>
    /// Whether another form already has this name.
    /// </summary>
    /// <remarks>
    /// Checked here rather than left to the unique index so the answer is a 409 naming the form
    /// rather than a 500 from a constraint. The index is still what holds under a race; this is what
    /// makes the ordinary case legible.
    /// </remarks>
    private static Task<bool> NameTakenAsync(
        ConfigurationDbContext db, string name, Guid? excluding, CancellationToken ct)
    {
        var trimmed = name.Trim();

        return db.SurveyForms.AnyAsync(
            form => form.Name == trimmed && (excluding == null || form.Id != excluding), ct);
    }

    /// <summary>
    /// Refuses a name another form already has, <b>naming it</b>.
    /// </summary>
    /// <remarks>
    /// The name travels as an <c>args</c> entry as well as inside the sentence, and that is the part
    /// that matters: a translated catalogue cannot dig a value out of the English message, so
    /// <c>config.survey.nameTaken</c> without this can only ever render as "another survey already
    /// has that name" — and an entry that named a placeholder the server does not send would throw
    /// at render (ADR-0012's stated cost). <c>product.priceList.nameTaken</c> is the same refusal
    /// with the same argument.
    /// </remarks>
    private static IResult NameTaken(string name) => Problems.Conflict(
        "name",
        $"'{name.Trim()}' is already the name of a survey.",
        "config.survey.nameTaken",
        new Dictionary<string, string> { ["name"] = name.Trim() });

    /// <summary>
    /// Refuses what the aggregate cannot say precisely enough.
    /// </summary>
    /// <remarks>
    /// Per-question problems are indexed, because an admin looking at twelve questions cannot work
    /// out which one "a question needs text" is about — the lesson the visit workflow's step
    /// validation already learned.
    /// </remarks>
    private static IResult? Problem(SurveyFormRequest request)
    {
        var problems = new List<FieldProblem>();

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            problems.Add(new FieldProblem(
                "name", "A survey needs a name — it is how you pick it.", "config.survey.nameRequired"));
        }
        else if (TextLimits.TooLong(
                     "name", request.Name.Trim(), SurveyForm.MaximumNameLength,
                     "config.survey.nameTooLong") is { } nameTooLong)
        {
            problems.Add(nameTooLong);
        }

        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];

            if (!Enum.IsDefined(question.Type))
            {
                problems.Add(new FieldProblem(
                    $"questions[{index}].type", "Unknown question type.",
                    "config.survey.unknownQuestionType"));
            }

            if (!Regex.IsMatch(question.Key ?? "", KeyPattern))
            {
                problems.Add(new FieldProblem(
                    $"questions[{index}].key",
                    "A key must be lowercase letters, digits and underscores, starting with a letter.",
                    "config.survey.keyMalformed"));
            }

            if (string.IsNullOrWhiteSpace(question.Text))
            {
                problems.Add(new FieldProblem(
                    $"questions[{index}].text",
                    "A question needs text — it is what the rep reads.",
                    "config.survey.textRequired"));

                continue;
            }

            if (TextLimits.TooLong(
                    $"questions[{index}].text", question.Text.Trim(), SurveyQuestion.MaximumTextLength,
                    "config.survey.textTooLong") is { } tooLong)
            {
                problems.Add(tooLong);
            }
        }

        return problems.Count == 0 ? null : Problems.BadRequest(problems);
    }

    /// <summary>Maps the aggregate's refusal onto an <c>ADR-0012</c> code a screen can branch on.</summary>
    private static IResult Refuse(SurveyFormRefusal refusal) => refusal switch
    {
        SurveyFormRefusal.Empty => Problems.BadRequest(
            "questions", "A survey needs at least one question.", "config.survey.empty"),

        SurveyFormRefusal.TooManyQuestions => Problems.BadRequest(
            "questions",
            $"A survey asks at most {SurveyForm.MaximumQuestions} questions.",
            "config.survey.tooManyQuestions"),

        SurveyFormRefusal.DuplicateKey => Problems.BadRequest(
            "questions",
            "Two questions share a key, and an answer is filed under it.",
            "config.survey.duplicateKey"),

        SurveyFormRefusal.ChoiceWithoutOptions => Problems.BadRequest(
            "questions",
            "A choice question needs something to choose from.",
            "config.survey.choiceWithoutOptions"),

        _ => Problems.BadRequest("questions", "That survey was refused.", "config.survey.refused"),
    };

    private static SurveyFormResponse Respond(SurveyFormDescriptor form) => new(
        form.Id,
        form.Name,
        [.. form.Questions.Select(question => new SurveyQuestionResponse(
            question.Order,
            question.Key,
            question.Text,
            question.Type.ToString(),
            question.Mandatory,
            question.Options))]);
}
