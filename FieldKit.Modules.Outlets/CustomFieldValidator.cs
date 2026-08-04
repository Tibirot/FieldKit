using System.Text.Json;
using FieldKit.Modules.Configuration.Contracts;

namespace FieldKit.Modules.Outlets;

/// <summary>
/// Checks an outlet's custom-field values against the tenant's definitions (<c>CFG-02</c>).
/// </summary>
/// <remarks>
/// <para>
/// The validation lives here rather than in Configuration because the rule belongs where the data is
/// written: Outlets owns the values, so Outlets decides they are acceptable and can say so in terms
/// of an outlet. Configuration supplies the definitions and nothing else.
/// </para>
/// <para>
/// Pure and static — it takes values and definitions and returns problems. That keeps the one part
/// of this feature with real branching testable without a database, and stops it accreting the
/// request context.
/// </para>
/// </remarks>
internal static class CustomFieldValidator
{
    /// <summary>
    /// Returns every problem with <paramref name="values"/>, or an empty list.
    /// </summary>
    /// <remarks>
    /// All of them, not the first: an admin filling a form wants to fix everything in one pass, and
    /// returning one error at a time turns a six-field form into six round trips.
    /// </remarks>
    public static IReadOnlyList<string> Validate(
        IReadOnlyDictionary<string, JsonElement>? values,
        IReadOnlyList<FieldDefinitionDescriptor> definitions)
    {
        var problems = new List<string>();
        var supplied = values ?? new Dictionary<string, JsonElement>();
        var known = definitions.ToDictionary(definition => definition.Key, StringComparer.Ordinal);

        // Unknown keys are rejected rather than ignored. Silently dropping them means an import or a
        // typo loses data with no signal — and the catalogue exists precisely so that what is stored
        // is describable.
        foreach (var key in supplied.Keys.Where(key => !known.ContainsKey(key)))
        {
            problems.Add($"'{key}' is not a defined custom field for outlets.");
        }

        foreach (var definition in definitions)
        {
            if (!supplied.TryGetValue(definition.Key, out var value) || value.ValueKind is JsonValueKind.Null)
            {
                if (definition.Required) problems.Add($"'{definition.Key}' is required.");
                continue;
            }

            problems.AddRange(Check(definition, value));
        }

        return problems;
    }

    private static IEnumerable<string> Check(FieldDefinitionDescriptor definition, JsonElement value)
    {
        switch (definition.Type)
        {
            case CustomFieldType.Text or CustomFieldType.Choice when value.ValueKind is not JsonValueKind.String:
                yield return $"'{definition.Key}' must be text.";
                break;

            case CustomFieldType.Text:
                if (definition.MaxLength is { } max && value.GetString()!.Length > max)
                {
                    yield return $"'{definition.Key}' must be at most {max} characters.";
                }

                break;

            case CustomFieldType.Choice:
                var chosen = value.GetString()!;

                // Ordinal: these are stored identifiers, and accepting "Yes" for "yes" would make the
                // permitted set depend on how a caller happened to type it.
                if (!definition.Options.Contains(chosen, StringComparer.Ordinal))
                {
                    yield return
                        $"'{definition.Key}' must be one of: {string.Join(", ", definition.Options)}.";
                }

                break;

            case CustomFieldType.Number when value.ValueKind is not JsonValueKind.Number:
                yield return $"'{definition.Key}' must be a number.";
                break;

            case CustomFieldType.Number:
                var number = value.GetDouble();

                if (definition.Minimum is { } min && number < min)
                {
                    yield return $"'{definition.Key}' must be at least {min}.";
                }

                if (definition.Maximum is { } ceiling && number > ceiling)
                {
                    yield return $"'{definition.Key}' must be at most {ceiling}.";
                }

                break;

            case CustomFieldType.Boolean when value.ValueKind is not (JsonValueKind.True or JsonValueKind.False):
                yield return $"'{definition.Key}' must be true or false.";
                break;

            case CustomFieldType.Date:
                // A date, as text, in the one format that sorts and parses the same everywhere.
                // Accepting a timestamp here would store an instant for something the tenant means
                // as a day — the same distinction the outlet's own time zone exists to protect.
                if (value.ValueKind is not JsonValueKind.String
                    || !DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", out _))
                {
                    yield return $"'{definition.Key}' must be a date as yyyy-MM-dd.";
                }

                break;
        }
    }
}
