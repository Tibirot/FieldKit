using System.Globalization;
using System.Text.Json;
using FieldKit.Modules.Configuration.Contracts;

namespace FieldKit.Modules.Outlets.Import;

/// <summary>
/// Earns back the types a CSV lost (<c>OUT-05</c>, <c>CFG-02</c>).
/// </summary>
/// <remarks>
/// <para>
/// A CSV has no types. Every cell is text, so <c>chiller_count</c> arrives as <c>"3"</c> and
/// <see cref="CustomFieldValidator"/> — correctly, by its own rules — refuses it as "must be a
/// number". That rejection would be nonsense to the person who typed 3 into a spreadsheet.
/// </para>
/// <para>
/// So the import does one thing the API does not: it reads the tenant's own field definitions and
/// turns text back into the type the tenant declared. That is only possible because Configuration
/// exists to be asked (<c>CFG-01</c>) — without the catalogue there is nothing to coerce
/// <i>towards</i>, and an importer would have to guess from the shape of the text.
/// </para>
/// <para>
/// <b>Coercion is parsing; validation is unchanged.</b> This produces a better-typed value and never
/// a verdict: text that will not convert is left exactly as it arrived, so the identical validator
/// produces the identical message. One source of error wording, and no second opinion about what an
/// outlet may hold — which is what keeps import from being a back door.
/// </para>
/// </remarks>
internal static class CustomFieldCoercion
{
    /// <summary>
    /// Returns <paramref name="values"/> with text converted to the declared types where it converts.
    /// </summary>
    public static Dictionary<string, JsonElement> Apply(
        IReadOnlyDictionary<string, JsonElement> values,
        IReadOnlyList<FieldDefinitionDescriptor> definitions)
    {
        var byKey = definitions.ToDictionary(definition => definition.Key, StringComparer.Ordinal);
        var coerced = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var (key, value) in values)
        {
            coerced[key] = byKey.TryGetValue(key, out var definition) ? Coerce(value, definition.Type) : value;
        }

        return coerced;
    }

    private static JsonElement Coerce(JsonElement value, CustomFieldType type)
    {
        // Only text needs earning back. A value that already has a type came from a format that kept
        // it, and re-deriving it from its own rendering could only lose information.
        if (value.ValueKind is not JsonValueKind.String) return value;

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text)) return value;

        return type switch
        {
            // Invariant, deliberately. A Romanian export writes 12,5 for twelve and a half, and a
            // culture-aware parse would read that as 125 on one machine and fail on another — a
            // number that is wrong rather than refused is the worse of the two outcomes.
            CustomFieldType.Number when double.TryParse(
                text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                => JsonSerializer.SerializeToElement(number),

            CustomFieldType.Boolean when bool.TryParse(text, out var flag)
                => JsonSerializer.SerializeToElement(flag),

            // Text, Choice and Date are text on the wire already — a date travels as yyyy-MM-dd by
            // the validator's own rule, so there is nothing to convert and everything to preserve.
            _ => value,
        };
    }
}
