using System.Text.Json;
using System.Text.Json.Serialization;

namespace FieldKit.Modules.Outlets.Import;

/// <summary>
/// The facts about an import a caller has to know before sending one (<c>OUT-05</c>).
/// </summary>
/// <remarks>
/// Public because both are part of the contract, not implementation detail: an import screen has to
/// tell someone their file is too big <i>before</i> uploading twelve megabytes, and the reason column
/// is a header in a file the admin downloads, edits and sends back.
/// </remarks>
/// <seealso cref="OutletImportCapabilities"/>
public static class OutletImportFormat
{
    /// <summary>
    /// The most rows one request may carry.
    /// </summary>
    /// <remarks>
    /// A limit rather than a queue. Everything here is one transaction and one response, which is
    /// honest up to a point and dishonest past it: an import that takes four minutes needs a job, a
    /// progress endpoint and somewhere to keep its result, and that is a different feature than this
    /// one. Refused with a message rather than truncated — an import that silently drops row 5,001
    /// is worse than one that will not run.
    /// </remarks>
    public const int MaxRows = 5_000;

    /// <summary>
    /// The column appended to the rejected-rows file, and ignored on the way back in.
    /// </summary>
    /// <remarks>
    /// Appended rather than inserted, and named so it cannot collide with a column a real export
    /// would have — a re-upload carrying it back has to be harmless, or the round trip it exists for
    /// does not work.
    /// </remarks>
    public const string ReasonColumn = "import_error";
}

/// <summary>
/// What the import accepts, answered before a file is sent (<c>OUT-05</c>).
/// </summary>
/// <remarks>
/// The same facts as <see cref="OutletImportFormat"/>, over HTTP, because the client that needs them
/// is not a C# one. A screen that hard-coded the row cap would hold a second copy of a number only
/// the server enforces — and a drifted copy fails silently, since nothing breaks when the screen
/// merely starts lying about the limit.
/// </remarks>
/// <param name="MediaTypes">
/// The formats read today. CSV alone; JSON and Excel are follow-ups that add a reader, and this is
/// how a file picker learns about them without a second commit.
/// </param>
/// <param name="ReasonColumn">
/// The column appended to the rejected-rows file — the screen names it when explaining the download.
/// </param>
public sealed record OutletImportCapabilities(
    int MaxRows,
    IReadOnlyList<string> MediaTypes,
    string ReasonColumn);

/// <summary>What an import does when some rows are bad (<c>OUT-05</c>).</summary>
/// <remarks>
/// The admin's choice, because both answers are right for different files. A 40-row list an
/// onboarding consultant typed should be fixed and re-sent whole; a 4,000-row export from a system
/// that has been accumulating dirt for a decade will never be clean, and refusing all of it means
/// refusing the migration.
///
/// <see cref="AllOrNothing"/> is the default: the mode that cannot half-apply is the one to get by
/// omission. Both are atomic — the difference is <i>which</i> set is written, not whether the write
/// is all-or-nothing.
/// </remarks>
public enum OutletImportMode
{
    /// <summary>One bad row and nothing is written.</summary>
    AllOrNothing = 0,

    /// <summary>The good rows are written; the bad ones come back to be fixed and re-sent.</summary>
    Partial = 1,
}

/// <summary>Something wrong with one row, said in terms of the file the admin uploaded.</summary>
/// <param name="Row">The line number in the uploaded file, header included — what their editor shows.</param>
/// <param name="Column">The column at fault, where one column is at fault.</param>
public sealed record OutletImportProblem(int Row, string? Column, string Message);

/// <summary>
/// What happened to an import (<c>OUT-05</c>).
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="RejectedRowsCsv"/> is the part that makes <see cref="OutletImportMode.Partial"/>
/// usable rather than a trap. Without it, an admin who imports 3,988 of 4,000 rows has to hand-build
/// a 12-row file to retry, because re-sending the original would now collide with everything that
/// landed. With it, the failures come back in the shape they were sent, plus a reason column: fix
/// that file, send that file.
/// </para>
/// <para>
/// Returned inline rather than persisted behind a link. A synchronous import has no result to
/// out-live the response, and storing one would mean a table, a retention rule and a cleanup job for
/// a file the admin already has open.
/// </para>
/// </remarks>
/// <param name="Accepted">Rows that passed every rule.</param>
/// <param name="Imported">
/// Rows actually written — zero for a dry run, and zero for an <see cref="OutletImportMode.AllOrNothing"/>
/// run with any rejection. Separate from <paramref name="Accepted"/> because a screen has three
/// different sentences to say: what is valid, what is wrong, and what is now in the database.
/// </param>
/// <param name="IgnoredColumns">
/// Columns the file had that this import did not use — neither a known outlet column nor a custom
/// field this tenant has defined. Reported rather than dropped in silence: a real export is full of
/// <c>legacy_id</c> and <c>last_modified_by</c>, so refusing the file over them would be hostile,
/// but a mistyped custom-field header is exactly the same shape and must not pass unmentioned.
/// </param>
public sealed record OutletImportResponse(
    int TotalRows,
    int Accepted,
    int Rejected,
    int Imported,
    bool DryRun,
    [property: JsonConverter(typeof(JsonStringEnumConverter<OutletImportMode>))] OutletImportMode Mode,
    IReadOnlyList<OutletImportProblem> Problems,
    string? RejectedRowsCsv,
    IReadOnlyList<string> IgnoredColumns);

/// <summary>One row as the reader found it, and the line it was on.</summary>
/// <remarks>
/// Values are <see cref="JsonElement"/> rather than strings because that is what the formats
/// <i>after</i> CSV produce: an Excel date cell is a date and a JSON number is a number, and flattening
/// those to text so a CSV-shaped pipeline can re-parse them would lose the type the file already knew.
/// A CSV reader fills this with strings and <see cref="CustomFieldCoercion"/> earns the types back.
/// </remarks>
internal sealed record OutletImportRow(int Number, IReadOnlyDictionary<string, JsonElement> Values);

/// <summary>An uploaded file, read but not yet understood.</summary>
/// <param name="Columns">In the order the file had them — the rejected-rows file is written back the same way.</param>
internal sealed record OutletImportFile(
    IReadOnlyList<string> Columns,
    IReadOnlyList<OutletImportRow> Rows);
