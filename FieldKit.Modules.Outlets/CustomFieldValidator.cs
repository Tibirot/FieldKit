using System.Text.Json;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Web;

namespace FieldKit.Modules.Outlets;

/// <summary>
/// Names an outlet's custom-field violations the way this API reports problems (<c>CFG-02</c>).
/// </summary>
/// <remarks>
/// <para>
/// The rules themselves moved to <see cref="CustomFieldRules"/> in Configuration's contracts when
/// Products became a second writer (<c>PRD-01</c>). Keeping them here would have meant a second
/// 143-line copy differing by one sentence, and two copies of real branching logic drift.
/// </para>
/// <para>
/// What stays is the part that is genuinely Outlets': the request path a problem is named by. The
/// shared rules cannot know that — they see a definition key, not the shape of the request it
/// arrived in.
/// </para>
/// <para>
/// <b>These carry <c>ADR-0012</c> codes; the rest of Outlets does not yet.</b> That is deliberate
/// rather than half-finished work left lying around. When Products became a second caller of
/// <see cref="CustomFieldRules"/>, one shared rule set had two callers answering differently — the
/// same violation coded in one module and bare prose in the other. Closing that is what this is;
/// migrating <c>OutletEndpoints</c>' other seventeen refusals is ADR-0012 stage 3 and its own change.
/// </para>
/// </remarks>
internal static class CustomFieldValidator
{
    /// <summary>Returns every problem with <paramref name="values"/>, or an empty list.</summary>
    public static IReadOnlyList<FieldProblem> Validate(
        IReadOnlyDictionary<string, JsonElement>? values,
        IReadOnlyList<FieldDefinitionDescriptor> definitions) =>
        [
            .. CustomFieldRules
                .Validate(values, definitions, CustomFieldEntity.Outlet)
                .Select(violation => new FieldProblem(
                    // `customFields.chiller_count`, not `chiller_count` — the request has a
                    // `customFields` object, so that is where a client looking for this problem
                    // expects to find it. Naming it by the bare key would collide with a fixed field
                    // the day a tenant defines one called `name`.
                    $"customFields.{violation.Key}",
                    violation.Message,
                    Code(violation.Kind),
                    violation.Args)),
        ];

    /// <summary>Maps a rule violation to this module's <c>ADR-0012</c> code.</summary>
    /// <remarks>
    /// <para>
    /// Literals in a switch, not interpolation over the enum name. Codes are API surface, and
    /// <c>grep outlet.customField</c> has to find the module that answers for them — which is the
    /// reason <see cref="CustomFieldRules"/> returns a <see cref="CustomFieldViolationKind"/> rather
    /// than a ready-made code.
    /// </para>
    /// <para>
    /// Deliberately a near-copy of the switch in Products' <c>ProductEndpoints</c>. The duplication
    /// is the point: these are two modules' independent naming of their own surface, and a shared
    /// helper deriving both would mean neither module's codes appear in its own source.
    /// </para>
    /// </remarks>
    private static string Code(CustomFieldViolationKind kind) => kind switch
    {
        CustomFieldViolationKind.Unknown => "outlet.customField.unknown",
        CustomFieldViolationKind.Required => "outlet.customField.required",
        CustomFieldViolationKind.WrongType => "outlet.customField.wrongType",
        CustomFieldViolationKind.TooLong => "outlet.customField.tooLong",
        CustomFieldViolationKind.NotAnOption => "outlet.customField.notAnOption",
        CustomFieldViolationKind.TooSmall => "outlet.customField.tooSmall",
        CustomFieldViolationKind.TooLarge => "outlet.customField.tooLarge",
        _ => "outlet.customField.invalid",
    };
}
