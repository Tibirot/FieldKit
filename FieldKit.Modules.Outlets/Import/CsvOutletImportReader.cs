using System.Globalization;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;

namespace FieldKit.Modules.Outlets.Import;

/// <summary>
/// Reads an uploaded CSV into rows (<c>OUT-05</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The seam.</b> Everything past this class is format-agnostic: coercion, validation, the write,
/// and the rejected-rows file all work on <see cref="OutletImportRow"/>. Adding JSON or Excel means
/// adding a reader and nothing else, which is the claim this shape exists to make good on.
/// </para>
/// <para>
/// <b>Not hand-rolled.</b> A CSV field can contain the delimiter, a newline, and escaped quotes, and
/// the split-on-comma version of this parser works on every file until it meets a store called
/// <c>Smith, Jones &amp; Co</c>. The same library writes the rejected-rows file, so a value that
/// survived a round trip through the import comes back quoted the way it arrived.
/// </para>
/// <para>
/// <b>Encoding.</b> UTF-8 with BOM detection, because that is what Excel writes on Windows and a BOM
/// read as data turns the first column's name into <c>﻿code</c> — a header that matches nothing
/// and produces "the code column is missing" for a file that plainly has one.
/// </para>
/// </remarks>
internal static class CsvOutletImportReader
{
    /// <summary>
    /// Reads <paramref name="stream"/>, or explains why it could not be read at all.
    /// </summary>
    /// <remarks>
    /// A whole-file failure is separate from a row failure on purpose: a file with no header, or one
    /// that is not CSV, has nothing to report per row and an admin needs to be told about the file
    /// rather than handed 4,000 identical row errors.
    /// </remarks>
    public static bool TryRead(Stream stream, out OutletImportFile file, out string? problem)
    {
        file = new OutletImportFile([], []);
        problem = null;

        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            // A trailing blank line is what a text editor leaves behind, not a row an admin meant.
            IgnoreBlankLines = true,

            // Headers are matched by our own normalisation below, not the library's, so its own
            // validation would fire on differences we are about to forgive.
            HeaderValidated = null,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.Trim,
        };

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var csv = new CsvReader(reader, configuration);

        List<string> columns;

        try
        {
            if (!csv.Read() || !csv.ReadHeader() || csv.HeaderRecord is not { Length: > 0 })
            {
                problem = "The file has no header row.";
                return false;
            }

            columns = [.. csv.HeaderRecord.Select(header => header.Trim())];
        }
        catch (CsvHelperException)
        {
            problem = "The file could not be read as CSV.";
            return false;
        }

        if (columns.Any(string.IsNullOrWhiteSpace))
        {
            problem = "The header row has an unnamed column.";
            return false;
        }

        if (columns.Count != columns.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            // Left as a file-level failure rather than silently taking the last one: two columns
            // called `city` mean the admin does not know which of them their data is in either.
            problem = "The header row names the same column twice.";
            return false;
        }

        var rows = new List<OutletImportRow>();

        try
        {
            while (csv.Read())
            {
                var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

                foreach (var column in columns)
                {
                    // A blank cell is an absent value, not an empty string. Otherwise an optional
                    // choice field with nothing in it fails as "not one of the options", and every
                    // untouched column in a wide export becomes a validation error.
                    if (csv.TryGetField<string>(column, out var raw) && !string.IsNullOrWhiteSpace(raw))
                    {
                        values[column] = JsonSerializer.SerializeToElement(raw.Trim());
                    }
                }

                // csv.Parser.Row counts the header, which is also what a spreadsheet's row numbers do
                // — the number in a problem should be the one the admin can navigate to.
                rows.Add(new OutletImportRow(csv.Parser.Row, values));
            }
        }
        catch (CsvHelperException exception)
        {
            problem = $"The file could not be read as CSV at line {exception.Context?.Parser?.Row}.";
            return false;
        }

        file = new OutletImportFile(columns, rows);
        return true;
    }

    /// <summary>
    /// Writes the rejected rows back in the shape they arrived, plus why each was refused.
    /// </summary>
    /// <remarks>
    /// The same columns in the same order, so the result is a file the admin can edit and re-upload
    /// rather than a report they have to transcribe. The reason column is appended rather than
    /// inserted, and named so it cannot collide with a column the file already had — a re-upload
    /// carrying it back is harmless, because an unknown column is ignored.
    /// </remarks>
    public static string WriteRejected(
        OutletImportFile file,
        IReadOnlyList<(OutletImportRow Row, IReadOnlyList<string> Reasons)> rejected)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);

        foreach (var column in file.Columns) csv.WriteField(column);
        csv.WriteField(OutletImportFormat.ReasonColumn);
        csv.NextRecord();

        foreach (var (row, reasons) in rejected)
        {
            foreach (var column in file.Columns)
            {
                csv.WriteField(row.Values.TryGetValue(column, out var value) ? AsText(value) : string.Empty);
            }

            csv.WriteField(string.Join(" ", reasons));
            csv.NextRecord();
        }

        return writer.ToString();
    }

    /// <summary>
    /// The value as text again.
    /// </summary>
    /// <remarks>
    /// A CSV-read value is a JSON string, so this hands back exactly what the cell held. It is here
    /// for the formats that are not CSV: a real Excel number would otherwise be written back as
    /// <c>"3"</c> complete with quotes, because that is what <c>GetRawText</c> does to a string.
    /// </remarks>
    private static string AsText(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
}
