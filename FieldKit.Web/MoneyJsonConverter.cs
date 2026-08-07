using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FieldKit.SharedKernel;

namespace FieldKit.Web;

/// <summary>
/// Puts <see cref="Money"/> on the wire as <c>{ "amount": "12.50", "currency": "EUR" }</c>
/// (<c>BR-PRD-8</c>, <see href="../../docs/architecture/13-api-contracts.md">api-contracts §1</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The amount is a string, and that is the whole point.</b> Serialized by default,
/// <c>decimal 12.50</c> becomes the JSON number <c>12.50</c> — and JavaScript has no decimal type, so
/// <c>JSON.parse</c> hands the browser an IEEE-754 float. The value survives that trip for small
/// amounts and stops surviving it exactly where pricing lives: percentage discounts, tiered
/// quantities, tax. A string arrives as a string, and the device engine feeds it to
/// <c>decimal.js</c> without ever having been a float.
/// </para>
/// <para>
/// <b>Registered globally rather than applied per property.</b> The enums in this codebase use
/// <c>[JsonConverter]</c> attributes at each site, which is fine because forgetting one produces a
/// number a client can still read. Forgetting one here produces a float, silently, in the one place
/// the project has a business rule against floats — and it would look correct in every test that
/// round-trips through a typed client. A converter nobody has to remember cannot be forgotten.
/// </para>
/// <para>
/// <b>Invariant culture, both directions.</b> Parsing <c>"12.50"</c> under a comma-decimal culture
/// yields 1250, which is the kind of bug that only appears on someone else's machine.
/// </para>
/// </remarks>
public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.StartObject)
        {
            throw new JsonException("A money value is an object with an amount and a currency.");
        }

        string? amount = null;
        string? currency = null;

        while (reader.Read() && reader.TokenType is not JsonTokenType.EndObject)
        {
            if (reader.TokenType is not JsonTokenType.PropertyName) continue;

            var property = reader.GetString();
            reader.Read();

            if (string.Equals(property, "amount", StringComparison.OrdinalIgnoreCase))
            {
                // A number is accepted on the way *in* — a client sending `12.50` unquoted has
                // already lost nothing, because it has not been through a JavaScript float yet if it
                // came from a decimal-aware caller. Refusing it would break `.http` files and curl
                // for no gain; what matters is that this API never *emits* one.
                amount = reader.TokenType switch
                {
                    JsonTokenType.String => reader.GetString(),
                    JsonTokenType.Number => reader.GetDecimal().ToString(CultureInfo.InvariantCulture),
                    _ => throw new JsonException("A money amount is a string, e.g. \"12.50\"."),
                };
            }
            else if (string.Equals(property, "currency", StringComparison.OrdinalIgnoreCase))
            {
                currency = reader.GetString();
            }
        }

        if (amount is null || currency is null)
        {
            throw new JsonException("A money value needs both an amount and a currency.");
        }

        // No thousands separators: NumberStyles.Number would accept "12,50" and hand back 1250
        // under invariant culture — a hundredfold error wearing a plausible price's clothes.
        if (!decimal.TryParse(
                amount,
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new JsonException($"'{amount}' is not a decimal amount.");
        }

        // Money's constructor enforces ISO-4217 shape; a bad currency surfaces as a JsonException
        // here rather than as an unhandled ArgumentException from deep inside model binding.
        try
        {
            return new Money(parsed, currency);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException(exception.Message);
        }
    }

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        // "12.50", not "12.5". A price list shows what a price list shows, and a client should not
        // have to know a currency's minor units to render what the server already knows.
        writer.WriteString("amount", value.Amount.ToString("0.00##", CultureInfo.InvariantCulture));
        writer.WriteString("currency", value.Currency);
        writer.WriteEndObject();
    }
}
