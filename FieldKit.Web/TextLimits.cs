namespace FieldKit.Web;

/// <summary>
/// Checks a caller's string against the column that has to hold it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Postgres errors on an overlong <c>varchar</c>; it does not truncate.</b> Without a check here
/// the failure surfaces as a <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> and a
/// <c>500</c> — the API telling the caller their request broke it, when what actually happened is
/// that a name was two characters too long. Every module had this on every create and update; a
/// pre-W7 sweep found it by posting a 300-character product name.
/// </para>
/// <para>
/// The rule is deliberately only about <i>length</i>. Whether a value is required, well-formed or
/// unique is a question each endpoint already answers for itself, and folding those together here
/// would make one helper that has to know every field's meaning.
/// </para>
/// <para>
/// <b>The maximum lives at the call site, not here.</b> It is the column's width, and the only
/// honest place to read that is beside the entity it belongs to — a table of limits in this file
/// would be a second copy of the schema, drifting quietly the first time a migration widens
/// something. <c>ContactValidator</c> already sets that precedent for outlet contacts.
/// </para>
/// </remarks>
public static class TextLimits
{
    /// <summary>
    /// A problem when <paramref name="value"/> is longer than <paramref name="max"/>, otherwise null.
    /// </summary>
    /// <remarks>
    /// Null is the "fine" answer so a caller can collect several at once —
    /// <c>problems.AddRange(new[] { … }.OfType&lt;FieldProblem&gt;())</c> — rather than returning on the
    /// first. A form showing eight boxes should hear about all eight, which is the same reason
    /// <see cref="Problems.BadRequest(IReadOnlyList{FieldProblem})"/> takes a list.
    /// </remarks>
    /// <param name="field">The JSON path the caller sent it under, e.g. <c>name</c>.</param>
    /// <param name="value">What arrived. Null and empty are never too long.</param>
    /// <param name="max">The column's width.</param>
    /// <param name="code">The ADR-0012 code, e.g. <c>product.name.tooLong</c>.</param>
    public static FieldProblem? TooLong(string field, string? value, int max, string code) =>
        value is not null && value.Length > max
            ? new FieldProblem(
                field,
                $"'{field}' is at most {max} characters.",
                code,
                new Dictionary<string, string>
                {
                    ["max"] = max.ToString(),
                    ["length"] = value.Length.ToString(),
                })
            : null;
}
