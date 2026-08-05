using System.Globalization;
using System.Text.Json;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Outlets.Import;

/// <summary>
/// Turns a read file into outlets, or into the reasons it could not (<c>OUT-05</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Import is not a back door.</b> Every rule an outlet is held to through <c>POST /api/outlets</c>
/// is applied here, through the same <see cref="Outlet.Create"/>, the same
/// <see cref="CustomFieldValidator"/>, the same <see cref="GeoPoint"/> bounds and the same time-zone
/// check. An importer with its own laxer path becomes the way bad data enters, and every feature
/// downstream inherits rows that could never have been created through the API.
/// </para>
/// <para>
/// The one thing it does that the API does not is <see cref="CustomFieldCoercion"/> — and that is
/// parsing, not judgement. See that class for why the distinction is load-bearing.
/// </para>
/// <para>
/// <b>Both modes are atomic.</b> Every row is validated before anything is written, and the write is
/// one <c>SaveChanges</c>. The mode chooses which set gets written, never whether the write can
/// half-apply — which is what makes a retry after a failure safe in either mode.
/// </para>
/// </remarks>
internal static class OutletImporter
{
    /// <summary>How <see cref="CustomFieldValidator"/> prefixes a custom field's path.</summary>
    private const string CustomFieldPrefix = "customFields.";

    /// <summary>A channel as this importer needs it — id to store, name to match on.</summary>
    private sealed record ChannelRef(Guid Id, string Name);

    /// <summary>Columns the file may carry that are not custom fields.</summary>
    private static class Column
    {
        public const string Code = "code";
        public const string Name = "name";
        public const string Channel = "channel";
        public const string Segment = "segment";
        public const string Banner = "banner";
        public const string TimeZone = "time_zone";
        public const string Street = "street";
        public const string City = "city";
        public const string PostalCode = "postal_code";
        public const string CountryCode = "country_code";
        public const string Latitude = "latitude";
        public const string Longitude = "longitude";

        /// <summary>Every built-in column, for telling custom fields and junk apart.</summary>
        public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Code, Name, Channel, Segment, Banner, TimeZone,
            Street, City, PostalCode, CountryCode, Latitude, Longitude,
        };
    }

    public static async Task<OutletImportResponse> RunAsync(
        OutletImportFile file,
        OutletImportMode mode,
        bool dryRun,
        OutletsDbContext db,
        IFieldDefinitionCatalog fields,
        CancellationToken ct)
    {
        var definitions = await fields.ForAsync(CustomFieldEntity.Outlet, ct);
        var definedKeys = definitions.Select(definition => definition.Key).ToHashSet(StringComparer.Ordinal);

        // Loaded once rather than per row. A 5,000-row file would otherwise be 15,000 round trips,
        // and the tenant filter means both sets are already scoped to the caller.
        var channels = await db.Channels
            .Select(channel => new ChannelRef(channel.Id, channel.Name))
            .ToListAsync(ct);

        var existingCodes = (await db.Outlets.Select(outlet => outlet.Code).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Both sets ignore case, matching the index that enforces it: a file holding OUT-1 and out-1
        // is one shop entered twice, and importing the pair is the accident rather than the service.
        var codesInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<OutletImportProblem>();
        var rejected = new List<(OutletImportRow Row, IReadOnlyList<string> Reasons)>();
        var accepted = new List<Outlet>();

        foreach (var row in file.Rows)
        {
            var issues = new List<(string? Column, string Message)>();
            var outlet = Build(row, issues, definitions, definedKeys, channels, existingCodes, codesInFile);

            if (issues.Count == 0 && outlet is not null)
            {
                accepted.Add(outlet);
                continue;
            }

            problems.AddRange(issues.Select(issue => new OutletImportProblem(row.Number, issue.Column, issue.Message)));
            rejected.Add((row, [.. issues.Select(issue => issue.Message)]));
        }

        // The mode decides what is written; it never decides whether a write can half-apply.
        var write = !dryRun && (mode == OutletImportMode.Partial || rejected.Count == 0);

        if (write && accepted.Count > 0)
        {
            db.Outlets.AddRange(accepted);

            // The same trail a single create writes (OUT-04). An outlet that arrived by import has
            // the same right to a history as one typed in, and "no history" reads the same as
            // "history was lost".
            db.OutletStatusChanges.AddRange(accepted.Select(outlet =>
                OutletStatusChange.Record(outlet.Id, from: null, outlet.Status, reason: null)));

            await db.SaveChangesAsync(ct);
        }

        return new OutletImportResponse(
            TotalRows: file.Rows.Count,
            Accepted: accepted.Count,
            Rejected: rejected.Count,
            Imported: write ? accepted.Count : 0,
            DryRun: dryRun,
            Mode: mode,
            Problems: problems,
            RejectedRowsCsv: rejected.Count == 0 ? null : CsvOutletImportReader.WriteRejected(file, rejected),
            IgnoredColumns: [.. file.Columns.Where(column =>
                !Column.All.Contains(column)
                && !definedKeys.Contains(column)
                && !column.Equals(OutletImportFormat.ReasonColumn, StringComparison.OrdinalIgnoreCase))],

            // Only on a dry run: a real run has nothing left to correct, and the caller is holding
            // these already. See OutletImportRowValues for why they are sent at all.
            Columns: dryRun ? file.Columns : [],
            Rows: dryRun ? [.. file.Rows.Select(row => Values(file.Columns, row))] : []);
    }

    /// <summary>
    /// One row's cells, in the file's own column order.
    /// </summary>
    /// <remarks>
    /// A blank cell is absent from <see cref="OutletImportRow.Values"/> — the reader drops it, so an
    /// optional field left empty is not judged as an empty string — and comes back as one here. The
    /// two say the same thing to the import, and a screen needs a cell to put in the grid.
    /// </remarks>
    private static OutletImportRowValues Values(IReadOnlyList<string> columns, OutletImportRow row) =>
        new(row.Number, [.. columns.Select(column => Cell(row, column))]);

    /// <summary>
    /// One cell as a screen should show it — the value, or empty where the file had nothing.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Text"/>, which answers null for an absent cell because the rules
    /// need "not supplied" and "supplied as empty" kept apart. A grid has only a box to fill.
    ///
    /// Not <c>GetRawText</c> unconditionally: a CSV-read value is a JSON string, and raw text would
    /// hand it back wrapped in quotes it never had. The other branch is for the formats after CSV,
    /// where a number cell really is a number.
    /// </remarks>
    private static string Cell(OutletImportRow row, string column) =>
        !row.Values.TryGetValue(column, out var value) ? string.Empty
            : value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty
            : value.GetRawText();

    /// <summary>
    /// Builds one outlet, adding to <paramref name="issues"/> everything wrong with the row.
    /// </summary>
    /// <remarks>
    /// Every problem, not the first. An admin fixing a spreadsheet wants one pass over it — the same
    /// reasoning <see cref="CustomFieldValidator"/> already follows, and it matters more here, where
    /// the alternative is re-uploading a 4,000-row file to discover the next single error.
    /// </remarks>
    private static Outlet? Build(
        OutletImportRow row,
        List<(string? Column, string Message)> issues,
        IReadOnlyList<FieldDefinitionDescriptor> definitions,
        IReadOnlySet<string> definedKeys,
        IReadOnlyList<ChannelRef> channels,
        IReadOnlySet<string> existingCodes,
        HashSet<string> codesInFile)
    {
        var code = Text(row, Column.Code);
        var name = Text(row, Column.Name);

        if (code is null) issues.Add((Column.Code, "A code is required."));
        if (name is null) issues.Add((Column.Name, "A name is required."));

        if (code is not null)
        {
            // Checked against the file as well as the database. Two rows with one code would
            // otherwise pass every per-row rule and fail as a unique-index violation mid-save —
            // an exception in place of the row number the admin needs.
            if (!codesInFile.Add(code))
            {
                issues.Add((Column.Code, $"'{code}' appears more than once in this file."));
            }
            else if (existingCodes.Contains(code))
            {
                issues.Add((Column.Code, $"An outlet with code '{code}' already exists."));
            }
        }

        var channelId = ResolveChannel(row, channels, issues);
        var timeZone = ResolveTimeZone(row, issues);
        var location = ResolveLocation(row, issues);
        var customFields = ResolveCustomFields(row, definitions, definedKeys, issues);

        if (issues.Count > 0 || code is null || name is null || channelId is null || timeZone is null) return null;

        return Outlet.Create(
            code,
            name,
            channelId.Value,
            Text(row, Column.Segment),
            Text(row, Column.Banner),
            timeZone,
            Address(row),
            location,
            contacts: null,
            customFields);
    }

    /// <summary>
    /// Resolves the channel by <b>name</b>, because that is what a spreadsheet holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A missing channel is refused rather than created. The channel vocabulary is what assortment
    /// and pricing rules key off, and a typo in one cell of an onboarding file would otherwise mint
    /// "Modren Trade" as a permanent classification nobody chose. It is also why <c>channel:write</c>
    /// is a separate permission from <c>outlet:write</c> — this path holds only the latter.
    /// </para>
    /// <para>
    /// Matched <b>case-insensitively</b>, plainly. A spreadsheet saying <c>modern trade</c> means the
    /// tenant's <c>Modern Trade</c> and there is nothing else it could mean, because a channel name
    /// is unique per tenant case-insensitively.
    ///
    /// That is a database index rather than an assumption, which is the only reason this can be one
    /// comparison. An earlier version of this method matched exactly, then loosely, then reported an
    /// ambiguity — three branches to cope with a tenant holding both <c>HoReCa</c> and <c>Horeca</c>.
    /// Fixing the index that let that pair exist deleted the problem instead of handling it.
    /// </para>
    /// </remarks>
    private static Guid? ResolveChannel(
        OutletImportRow row, IReadOnlyList<ChannelRef> channels, List<(string?, string)> issues)
    {
        var wanted = Text(row, Column.Channel);

        if (wanted is null)
        {
            issues.Add((Column.Channel, "A channel is required."));
            return null;
        }

        var match = channels.FirstOrDefault(channel =>
            string.Equals(channel.Name, wanted, StringComparison.OrdinalIgnoreCase));

        if (match is not null) return match.Id;

        issues.Add((Column.Channel, $"There is no channel called '{wanted}'."));
        return null;
    }

    private static string? ResolveTimeZone(OutletImportRow row, List<(string?, string)> issues)
    {
        var zone = Text(row, Column.TimeZone);

        if (zone is null)
        {
            // Required for the same reason it is required everywhere else: a visit's business day
            // and a promotion's validity resolve in it, so a missing zone is a wrong answer waiting.
            issues.Add((Column.TimeZone, "A time zone is required."));
            return null;
        }

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _))
        {
            issues.Add((Column.TimeZone, $"'{zone}' is not a known IANA time zone."));
            return null;
        }

        return zone;
    }

    /// <summary>
    /// Reads the coordinates, which are optional but not half-optional.
    /// </summary>
    /// <remarks>
    /// One of the pair without the other is a mistake rather than a partial answer — a latitude alone
    /// places nothing — and taking it silently would store a point on the Greenwich meridian for
    /// every outlet whose longitude column was blank.
    /// </remarks>
    private static GeoPoint? ResolveLocation(OutletImportRow row, List<(string?, string)> issues)
    {
        var latitude = Text(row, Column.Latitude);
        var longitude = Text(row, Column.Longitude);

        if (latitude is null && longitude is null) return null;

        if (latitude is null || longitude is null)
        {
            issues.Add((latitude is null ? Column.Latitude : Column.Longitude,
                "Coordinates need both a latitude and a longitude."));
            return null;
        }

        // Invariant on purpose — see CustomFieldCoercion for why a culture-aware parse of "44,43" is
        // the dangerous option rather than the helpful one.
        if (!double.TryParse(latitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            || !double.TryParse(longitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
        {
            issues.Add((Column.Latitude, "Coordinates must be numbers, with a dot for the decimal point."));
            return null;
        }

        if (!GeoPoint.TryCreate(lat, lon, out var point))
        {
            issues.Add((Column.Latitude,
                "Latitude must be between -90 and 90, and longitude between -180 and 180."));
            return null;
        }

        return point;
    }

    private static Dictionary<string, JsonElement> ResolveCustomFields(
        OutletImportRow row,
        IReadOnlyList<FieldDefinitionDescriptor> definitions,
        IReadOnlySet<string> definedKeys,
        List<(string?, string)> issues)
    {
        var supplied = row.Values
            .Where(value => definedKeys.Contains(value.Key))
            .ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);

        var coerced = CustomFieldCoercion.Apply(supplied, definitions);

        if (definitions.Count == 0 && coerced.Count == 0) return coerced;

        foreach (var problem in CustomFieldValidator.Validate(coerced, definitions))
        {
            // The validator names the field itself now, as `customFields.<key>` — and a custom
            // field's key *is* its column here, because the file's vocabulary and the catalogue's
            // are the same one. This used to recover the column by checking which definition the
            // message started with, which worked and would have stopped working the moment a message
            // was reworded.
            var column = problem.Field?.StartsWith(CustomFieldPrefix, StringComparison.Ordinal) == true
                ? problem.Field[CustomFieldPrefix.Length..]
                : null;

            issues.Add((column, problem.Message));
        }

        return coerced;
    }

    private static Address? Address(OutletImportRow row)
    {
        var street = Text(row, Column.Street);
        var city = Text(row, Column.City);
        var postalCode = Text(row, Column.PostalCode);
        var countryCode = Text(row, Column.CountryCode);

        return street is null && city is null && postalCode is null && countryCode is null
            ? null
            : new Address(street, city, postalCode, countryCode);
    }

    /// <summary>The cell as text, or null when the file did not have one.</summary>
    private static string? Text(OutletImportRow row, string column) =>
        row.Values.TryGetValue(column, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
