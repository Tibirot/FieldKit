using System.Globalization;
using FieldKit.Web;
using Microsoft.AspNetCore.Http;

namespace FieldKit.Modules.Products;

/// <summary>
/// Reads the <c>?on=</c> a resolution endpoint is asked about.
/// </summary>
/// <remarks>
/// <para>
/// <b>Always a parameter, never a clock read</b>, and all three resolution endpoints refuse a
/// request without one. Two reasons, and they are the same two for prices, promotions and tax:
/// </para>
/// <para>
/// A default would mean the <i>server's</i> today, and an outlet in Bucharest changes day six hours
/// before one in London — so a promotion running "1–30 June" would be live at the wrong moments for
/// most of a tenant's estate (<c>BR-PRD-6</c> evaluates a window in the outlet's timezone). The
/// business date has to be computed where the timezone is known, which is the device, or a caller
/// holding the outlet.
/// </para>
/// <para>
/// And resolution has to be reproducible: an order re-priced during sync must resolve to the price
/// and the promotion it was taken under, which cannot hold if the answer depends on when the question
/// is asked.
/// </para>
/// <para>
/// Shared by both endpoints rather than copied. The codes differ per resource — a form highlighting
/// the field wants to know which endpoint refused — so they are parameters, but the parse and the
/// reasoning are one thing in one place.
/// </para>
/// </remarks>
internal static class BusinessDate
{
    /// <summary>Parses <c>?on=</c>, or explains why it could not.</summary>
    /// <remarks>
    /// Hand-parsed rather than bound as a <c>DateOnly?</c> parameter, so a malformed date is a field
    /// problem naming <c>on</c> like every other refusal in this codebase, instead of the framework's
    /// bare 400 with no body.
    /// </remarks>
    public static IResult? Parse(
        string? on, string requiredCode, string malformedCode, out DateOnly date)
    {
        date = default;

        if (string.IsNullOrWhiteSpace(on))
        {
            return Problems.BadRequest(
                "on",
                "A date is required — resolution is never relative to the server's today.",
                requiredCode);
        }

        if (!DateOnly.TryParseExact(on, "yyyy-MM-dd", CultureInfo.InvariantCulture, default, out date))
        {
            return Problems.BadRequest("on", "Expected a date as yyyy-MM-dd.", malformedCode);
        }

        return null;
    }
}
