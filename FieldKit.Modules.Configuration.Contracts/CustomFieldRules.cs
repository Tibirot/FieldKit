using System.Text.Json.Serialization;
using System.Text.Json;

namespace FieldKit.Modules.Configuration.Contracts;

/// <summary>What is wrong with one custom-field value.</summary>
/// <remarks>
/// A <see cref="Kind"/> rather than a ready-made <c>ADR-0012</c> code, deliberately. Codes are API
/// surface owned by the module that emits them — <c>outlet.customField.tooLong</c> belongs to
/// Outlets — and deriving them here would mean <c>grep product.customField</c> returns nothing in
/// the Products module that answers for them. The mapping is a ten-line switch at each call site;
/// the branching logic, which is the part that can be wrong, is not duplicated.
/// </remarks>
/// <param name="Key">The definition key, bare. Callers prefix it with their own request path.</param>
/// <param name="Message">
/// English, and a fallback — same contract as <c>FieldProblem.Message</c>. Phrased without naming a
/// module so it reads correctly whichever entity carried the value.
/// </param>
/// <param name="Args">The values <see cref="Message"/> interpolates, named, for the catalogs.</param>
public sealed record CustomFieldViolation(
    string Key,
    CustomFieldViolationKind Kind,
    string Message,
    IReadOnlyDictionary<string, string>? Args = null);

/// <summary>The ways a custom-field value can fail its definition.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CustomFieldViolationKind>))]
public enum CustomFieldViolationKind
{
    /// <summary>A key no definition describes.</summary>
    Unknown = 0,

    /// <summary>A required field with no value.</summary>
    Required = 1,

    /// <summary>A value of the wrong JSON kind for its type.</summary>
    WrongType = 2,

    /// <summary>Text longer than the definition permits.</summary>
    TooLong = 3,

    /// <summary>A choice outside the permitted set.</summary>
    NotAnOption = 4,

    /// <summary>A number below the minimum.</summary>
    TooSmall = 5,

    /// <summary>A number above the maximum.</summary>
    TooLarge = 6,
}

/// <summary>
/// Checks custom-field values against the tenant's definitions (<c>CFG-02</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Here rather than in the modules that store the values.</b> Outlets originally owned this, on
/// the argument that the rule belongs where the data is written. That held while there was one
/// writer; with a second (<c>PRD-01</c>) it would have meant a second 143-line copy differing by a
/// single sentence, and two copies of real branching logic drift — a bug fixed in one is not fixed
/// in the other.
/// </para>
/// <para>
/// Configuration is the right home because it owns the vocabulary being checked against:
/// <see cref="CustomFieldType"/>, what <c>MaxLength</c> and <c>Minimum</c> mean, and which entities
/// can carry fields at all. What each module keeps is the part that is genuinely its own — how the
/// problem is named to a caller, and which <c>ADR-0012</c> code it carries.
/// </para>
/// <para>
/// Pure and static: values and definitions in, violations out. No database, no request context, and
/// no dependency beyond the BCL — which is what lets it live in a <c>Contracts</c> assembly at all,
/// since those may reference only <c>SharedKernel</c> and <c>BuildingBlocks</c>.
/// </para>
/// </remarks>
public static class CustomFieldRules
{
    /// <summary>
    /// Returns every violation in <paramref name="values"/>, or an empty list.
    /// </summary>
    /// <remarks>
    /// All of them, not the first: an admin filling a form wants to fix everything in one pass, and
    /// returning one error at a time turns a six-field form into six round trips.
    /// </remarks>
    /// <param name="entity">Only used to phrase the unknown-key message in the caller's own terms.</param>
    public static IReadOnlyList<CustomFieldViolation> Validate(
        IReadOnlyDictionary<string, JsonElement>? values,
        IReadOnlyList<FieldDefinitionDescriptor> definitions,
        CustomFieldEntity entity)
    {
        var violations = new List<CustomFieldViolation>();
        var supplied = values ?? new Dictionary<string, JsonElement>();
        var known = definitions.ToDictionary(definition => definition.Key, StringComparer.Ordinal);

        // Unknown keys are rejected rather than ignored. Silently dropping them means an import or a
        // typo loses data with no signal — and the catalogue exists precisely so that what is stored
        // is describable.
        foreach (var key in supplied.Keys.Where(key => !known.ContainsKey(key)))
        {
            violations.Add(new CustomFieldViolation(
                key,
                CustomFieldViolationKind.Unknown,
                $"'{key}' is not a defined custom field for {Plural(entity)}.",
                new Dictionary<string, string> { ["key"] = key, ["entity"] = entity.ToString() }));
        }

        foreach (var definition in definitions)
        {
            if (!supplied.TryGetValue(definition.Key, out var value) || value.ValueKind is JsonValueKind.Null)
            {
                if (definition.Required)
                {
                    violations.Add(new CustomFieldViolation(
                        definition.Key,
                        CustomFieldViolationKind.Required,
                        $"'{definition.Key}' is required.",
                        new Dictionary<string, string> { ["key"] = definition.Key }));
                }

                continue;
            }

            violations.AddRange(Check(definition, value));
        }

        return violations;
    }

    /// <summary>The entity as it appears mid-sentence — "outlets", "products".</summary>
    /// <remarks>
    /// A switch rather than string concatenation, so adding a member to
    /// <see cref="CustomFieldEntity"/> is a compile-time prompt to decide how it reads rather than a
    /// silent "Orders" appearing in a lower-case sentence.
    /// </remarks>
    private static string Plural(CustomFieldEntity entity) => entity switch
    {
        CustomFieldEntity.Outlet => "outlets",
        CustomFieldEntity.Product => "products",
        CustomFieldEntity.Order => "orders",
        CustomFieldEntity.Visit => "visits",
        _ => entity.ToString().ToLowerInvariant(),
    };

    private static IEnumerable<CustomFieldViolation> Check(
        FieldDefinitionDescriptor definition, JsonElement value)
    {
        var key = definition.Key;

        switch (definition.Type)
        {
            case CustomFieldType.Text or CustomFieldType.Choice when value.ValueKind is not JsonValueKind.String:
                yield return Violation(key, CustomFieldViolationKind.WrongType, $"'{key}' must be text.");
                break;

            case CustomFieldType.Text:
                if (definition.MaxLength is { } max && value.GetString()!.Length > max)
                {
                    yield return Violation(
                        key,
                        CustomFieldViolationKind.TooLong,
                        $"'{key}' must be at most {max} characters.",
                        ("max", max.ToString()));
                }

                break;

            case CustomFieldType.Choice:
                var chosen = value.GetString()!;

                // Ordinal: these are stored identifiers, and accepting "Yes" for "yes" would make the
                // permitted set depend on how a caller happened to type it.
                if (!definition.Options.Contains(chosen, StringComparer.Ordinal))
                {
                    yield return Violation(
                        key,
                        CustomFieldViolationKind.NotAnOption,
                        $"'{key}' must be one of: {string.Join(", ", definition.Options)}.",
                        ("options", string.Join(", ", definition.Options)));
                }

                break;

            case CustomFieldType.Number when value.ValueKind is not JsonValueKind.Number:
                yield return Violation(key, CustomFieldViolationKind.WrongType, $"'{key}' must be a number.");
                break;

            case CustomFieldType.Number:
                var number = value.GetDouble();

                if (definition.Minimum is { } min && number < min)
                {
                    yield return Violation(
                        key,
                        CustomFieldViolationKind.TooSmall,
                        $"'{key}' must be at least {min}.",
                        ("min", min.ToString()));
                }

                if (definition.Maximum is { } ceiling && number > ceiling)
                {
                    yield return Violation(
                        key,
                        CustomFieldViolationKind.TooLarge,
                        $"'{key}' must be at most {ceiling}.",
                        ("max", ceiling.ToString()));
                }

                break;

            case CustomFieldType.Boolean when value.ValueKind is not (JsonValueKind.True or JsonValueKind.False):
                yield return Violation(
                    key, CustomFieldViolationKind.WrongType, $"'{key}' must be true or false.");
                break;

            case CustomFieldType.Date:
                // A date, as text, in the one format that sorts and parses the same everywhere.
                // Accepting a timestamp here would store an instant for something the tenant means
                // as a day — the same distinction an outlet's own time zone exists to protect.
                if (value.ValueKind is not JsonValueKind.String
                    || !DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", out _))
                {
                    yield return Violation(
                        key, CustomFieldViolationKind.WrongType, $"'{key}' must be a date as yyyy-MM-dd.");
                }

                break;
        }
    }

    private static CustomFieldViolation Violation(
        string key, CustomFieldViolationKind kind, string message, params (string Key, string Value)[] args)
    {
        var named = new Dictionary<string, string> { ["key"] = key };

        foreach (var (name, value) in args)
        {
            named[name] = value;
        }

        return new CustomFieldViolation(key, kind, message, named);
    }
}
