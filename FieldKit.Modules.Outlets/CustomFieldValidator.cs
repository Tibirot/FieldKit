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
/// These problems still carry prose only. Migrating them to <c>ADR-0012</c> codes is Outlets' own
/// change, and doing it here would have hidden a behavioural difference inside a refactor.
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
                .Select(violation => Problem(violation.Key, violation.Message)),
        ];

    /// <summary>
    /// Names the field by the path the caller sent it under.
    /// </summary>
    /// <remarks>
    /// <c>customFields.chiller_count</c>, not <c>chiller_count</c> — the request has a
    /// <c>customFields</c> object, so that is where a client looking for this problem will expect to
    /// find it. Naming it by the bare key would collide with a fixed field the day a tenant defines
    /// one called <c>name</c>.
    /// </remarks>
    private static FieldProblem Problem(string key, string message) =>
        new($"customFields.{key}", message);
}
