using System.Net;
using System.Net.Http.Json;
using System.Text;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Outlets;
using FieldKit.Web;
using FieldKit.Modules.Outlets.Import;

namespace FieldKit.Server.Tests;

/// <summary>
/// Bulk import of the outlet base (<c>OUT-05</c>).
/// </summary>
/// <remarks>
/// The tests that carry the design are the ones about what happens when a file is <i>wrong</i>: an
/// importer that only works on clean data is a demo, and nobody's outlet export has ever been clean.
/// </remarks>
[Collection(ServerCollection.Name)]
public class OutletImportTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private static StringContent Csv(string csv) => new(csv, Encoding.UTF8, "text/csv");

    private async Task<string> ChannelAsync(HttpClient client)
    {
        var name = Unique("Channel");
        await client.PostAsJsonAsync("/api/outlets/channels", new ChannelRequest(name));
        return name;
    }

    private static async Task<OutletImportResponse> ImportAsync(
        HttpClient client, string csv, OutletImportMode? mode = null, bool? dryRun = null)
    {
        var query = new List<string>();
        if (mode is not null) query.Add($"mode={mode}");
        if (dryRun is not null) query.Add($"dryRun={dryRun.Value.ToString().ToLowerInvariant()}");

        var url = "/api/outlets/import" + (query.Count == 0 ? "" : "?" + string.Join("&", query));
        var response = await client.PostAsync(url, Csv(csv));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<OutletImportResponse>())!;
    }

    private static async Task<bool> ExistsAsync(HttpClient client, string code)
    {
        var outlets = (await client.GetFromJsonAsync<PagedList<OutletResponse>>($"/api/outlets?pageSize={Paging.MaxSize}"))!.Items;
        return outlets!.Any(outlet => outlet.Code == code);
    }

    [Fact]
    public async Task The_import_says_what_it_accepts_before_a_file_is_sent()
    {
        // So the screen can refuse an oversized file without uploading twelve megabytes of it, and
        // so the row cap lives in one place. A front end holding its own copy of 5,000 would drift
        // silently: nothing breaks when the two disagree, the screen simply starts lying.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var capabilities =
            await client.GetFromJsonAsync<OutletImportCapabilities>("/api/outlets/import");

        Assert.Equal(OutletImportFormat.MaxRows, capabilities!.MaxRows);
        Assert.Equal(OutletImportFormat.ReasonColumn, capabilities.ReasonColumn);
        Assert.Equal("text/csv", Assert.Single(capabilities.MediaTypes));
    }

    [Fact]
    public async Task Reading_what_the_import_accepts_needs_the_permission_to_import()
    {
        // A capability document is only about a capability. Someone who may read outlets but not
        // write them has no import to configure, so this is not a fact they are owed.
        using var client = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        var response = await client.GetAsync("/api/outlets/import");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_dry_run_hands_back_the_file_as_it_read_it()
    {
        // So a screen can correct a row without re-reading the upload. A second CSV reader only has
        // to disagree about which row is row 7 for every flagged cell to land on the wrong shop —
        // and nothing would say so. The reader that numbered the problems is the one that should say
        // what is in that row.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channel = await ChannelAsync(client);

        // The two cases a hand-rolled reader gets wrong: a quoted delimiter, and a newline inside a
        // field. The second is why rows are counted as records — read as lines, every number after
        // it is off by one.
        var result = await ImportAsync(
            client,
            "code,name,channel,time_zone\n"
                + $"{Unique("A")},\"Smith, Jones & Co\",{channel},Europe/Bucharest\n"
                + $"{Unique("B")},\"Two\nLines Shop\",{channel},Europe/Bucharest\n",
            dryRun: true);

        Assert.Equal(["code", "name", "channel", "time_zone"], result.Columns);
        Assert.Equal(2, result.Rows.Count);

        Assert.Equal([2, 3], [.. result.Rows.Select(row => row.Row)]);
        Assert.Equal("Smith, Jones & Co", result.Rows[0].Values[1]);
        Assert.Equal("Two\nLines Shop", result.Rows[1].Values[1]);
    }

    [Fact]
    public async Task A_row_number_in_a_problem_indexes_the_rows_it_sent()
    {
        // The point of sending them. A client should be able to find the row a problem is about by
        // its number, without counting anything itself.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channel = await ChannelAsync(client);
        var code = Unique("C");

        var result = await ImportAsync(
            client,
            "code,name,channel,time_zone\n"
                + $"{Unique("D")},\"Multi\nline\",{channel},Europe/Bucharest\n"
                + $"{code},Bad Zone,{channel},Europe/Bucuresti\n",
            dryRun: true);

        var problem = Assert.Single(result.Problems);
        var row = Assert.Single(result.Rows, candidate => candidate.Row == problem.Row);

        Assert.Equal(code, row.Values[0]);
    }

    [Fact]
    public async Task A_real_run_sends_no_rows_back()
    {
        // Nothing left to correct, and the caller is holding them already — 5,000 rows of JSON for
        // a screen that has moved on is payload nobody reads.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channel = await ChannelAsync(client);

        var result = await ImportAsync(
            client,
            $"code,name,channel,time_zone\n{Unique("E")},Corner Shop,{channel},Europe/Bucharest\n");

        Assert.Equal(1, result.Imported);
        Assert.Empty(result.Rows);
        Assert.Empty(result.Columns);
    }

    [Fact]
    public async Task A_clean_file_becomes_outlets()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channel = await ChannelAsync(client);
        var code = Unique("OUT");

        var result = await ImportAsync(client, $"""
            code,name,channel,time_zone,city,latitude,longitude
            {code},Alimentara Central,{channel},{Zone},Bucharest,44.4268,26.1025
            """);

        Assert.Equal(1, result.TotalRows);
        Assert.Equal(1, result.Imported);
        Assert.Empty(result.Problems);
        Assert.Null(result.RejectedRowsCsv);

        var outlets = (await client.GetFromJsonAsync<PagedList<OutletResponse>>($"/api/outlets?pageSize={Paging.MaxSize}"))!.Items;
        var imported = outlets!.Single(outlet => outlet.Code == code);

        Assert.Equal("Alimentara Central", imported.Name);
        Assert.Equal(Zone, imported.TimeZoneId);
        Assert.Equal("Bucharest", imported.Address?.City);
        Assert.Equal(44.4268, imported.Location!.Latitude, 4);
    }

    [Fact]
    public async Task A_csv_has_no_types_so_the_catalogue_supplies_them()
    {
        // The one thing this path does that POST /api/outlets does not. Every cell is text, so a
        // number field arrives as "3" and the validator — correctly, by its own rules — would refuse
        // it. Coercion reads the tenant's own definitions and earns the type back.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channel = await ChannelAsync(client);
        var code = Unique("OUT");
        var number = $"n{Guid.NewGuid():N}"[..12];
        var flag = $"b{Guid.NewGuid():N}"[..12];

        await client.PostAsJsonAsync("/api/config/field-definitions", new CreateFieldDefinitionRequest(
            CustomFieldEntity.Outlet, number, "Chillers", CustomFieldType.Number, Minimum: 0, Maximum: 50));
        await client.PostAsJsonAsync("/api/config/field-definitions", new CreateFieldDefinitionRequest(
            CustomFieldEntity.Outlet, flag, "Has parking", CustomFieldType.Boolean));

        var result = await ImportAsync(client, $"""
            code,name,channel,time_zone,{number},{flag}
            {code},Corner Shop,{channel},{Zone},3,true
            """);

        Assert.Equal(1, result.Imported);

        var outlets = (await client.GetFromJsonAsync<PagedList<OutletResponse>>($"/api/outlets?pageSize={Paging.MaxSize}"))!.Items;
        var imported = outlets!.Single(outlet => outlet.Code == code);

        // Stored as a number and a boolean, not as the strings the file held.
        Assert.Equal(3, imported.CustomFields[number].GetInt32());
        Assert.True(imported.CustomFields[flag].GetBoolean());
    }

    [Fact]
    public async Task Coercion_does_not_soften_the_rules_it_only_types_the_values()
    {
        // The line that keeps import from being a back door: a value that converts is still held to
        // the same bounds, and the message is the validator's own.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channel = await ChannelAsync(client);
        var number = $"n{Guid.NewGuid():N}"[..12];

        await client.PostAsJsonAsync("/api/config/field-definitions", new CreateFieldDefinitionRequest(
            CustomFieldEntity.Outlet, number, "Chillers", CustomFieldType.Number, Minimum: 0, Maximum: 50));

        var result = await ImportAsync(client, $"""
            code,name,channel,time_zone,{number}
            {Unique("OUT")},Corner Shop,{channel},{Zone},900
            """);

        Assert.Equal(0, result.Imported);

        // And the problem names its column, so the screen can point at the cell rather than the row.
        var problem = Assert.Single(result.Problems);
        Assert.Contains("at most 50", problem.Message);
        Assert.Equal(number, problem.Column);
    }

    [Fact]
    public async Task All_or_nothing_writes_nothing_when_one_row_is_wrong()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channel = await ChannelAsync(client);
        var good = Unique("OUT");

        var result = await ImportAsync(client, $"""
            code,name,channel,time_zone
            {good},Good Row,{channel},{Zone}
            {Unique("OUT")},Bad Row,{channel},Mars/Olympus_Mons
            """);

        Assert.Equal(1, result.Accepted);
        Assert.Equal(1, result.Rejected);
        Assert.Equal(0, result.Imported);
        Assert.False(await ExistsAsync(client, good));
    }

    [Fact]
    public async Task Partial_writes_the_good_rows_and_hands_back_the_rest()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channel = await ChannelAsync(client);
        var good = Unique("OUT");
        var bad = Unique("OUT");

        var result = await ImportAsync(client, $"""
            code,name,channel,time_zone
            {good},Good Row,{channel},{Zone}
            {bad},Bad Row,{channel},Mars/Olympus_Mons
            """, OutletImportMode.Partial);

        Assert.Equal(1, result.Imported);
        Assert.True(await ExistsAsync(client, good));
        Assert.False(await ExistsAsync(client, bad));

        Assert.NotNull(result.RejectedRowsCsv);
        Assert.Contains(bad, result.RejectedRowsCsv);
        Assert.DoesNotContain(good, result.RejectedRowsCsv);
    }

    [Fact]
    public async Task The_rejected_rows_are_a_file_you_can_fix_and_send_back()
    {
        // The whole reason partial mode is usable rather than a trap. Re-sending the original file
        // would now collide with everything that landed, so the failures come back in the shape they
        // arrived — fix that file, send that file.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channel = await ChannelAsync(client);
        var good = Unique("OUT");
        var bad = Unique("OUT");

        var first = await ImportAsync(client, $"""
            code,name,channel,time_zone
            {good},Good Row,{channel},{Zone}
            {bad},Bad Row,{channel},Mars/Olympus_Mons
            """, OutletImportMode.Partial);

        // The fix an admin would make in their editor: correct the cell, leave everything else alone.
        // The reason column rides back along with it and has to be harmless.
        var corrected = first.RejectedRowsCsv!.Replace("Mars/Olympus_Mons", Zone);
        var second = await ImportAsync(client, corrected);

        Assert.Equal(1, second.Imported);
        Assert.Empty(second.Problems);
        Assert.True(await ExistsAsync(client, bad));

        // And the round trip is silent about the one column the admin did not add. Reporting our own
        // reason column back as "ignored" would be noise they can do nothing about — every other
        // unused column is worth naming precisely because it came from them.
        Assert.DoesNotContain(OutletImportFormat.ReasonColumn, second.IgnoredColumns);
    }

    [Fact]
    public async Task A_dry_run_answers_the_question_without_doing_anything()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channel = await ChannelAsync(client);
        var code = Unique("OUT");

        var result = await ImportAsync(client, $"""
            code,name,channel,time_zone
            {code},Corner Shop,{channel},{Zone}
            """, dryRun: true);

        Assert.True(result.DryRun);
        Assert.Equal(1, result.Accepted);
        Assert.Equal(0, result.Imported);
        Assert.False(await ExistsAsync(client, code));
    }

    /// <summary>
    /// The import is the outlet's second door, and it has to refuse what the API refuses.
    /// </summary>
    /// <remarks>
    /// A spreadsheet is the likelier place to find a country spelled out — nothing in a CSV header
    /// says the column wants a code. Before this the cell went through unchecked and, once
    /// upper-cased, reached a <c>varchar(2)</c> column as "ROMANIA": a <c>DbUpdateException</c> that
    /// failed the whole import with a stack trace where the admin needed a row number.
    /// </remarks>
    [Fact]
    public async Task A_country_that_is_not_a_code_is_a_row_problem_not_a_failed_import()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channel = await ChannelAsync(client);
        var good = Unique("OUT");
        var bad = Unique("OUT");

        var result = await ImportAsync(client, $"""
            code,name,channel,time_zone,country_code
            {good},Corner Shop,{channel},{Zone},ro
            {bad},Spelled Out,{channel},{Zone},Romania
            """, OutletImportMode.Partial);

        Assert.Equal(1, result.Imported);

        Assert.Contains(
            result.Problems,
            problem => problem.Column == "country_code" && problem.Message.Contains("ISO-3166-1"));

        // And the row that was fine is stored upper-cased, by the same rule the API applies.
        var outlets = (await client.GetFromJsonAsync<PagedList<OutletResponse>>(
            $"/api/outlets?pageSize={Paging.MaxSize}"))!.Items;

        Assert.Equal("RO", outlets!.Single(outlet => outlet.Code == good).Address!.CountryCode);
        Assert.False(await ExistsAsync(client, bad));
    }

    [Fact]
    public async Task A_code_twice_in_one_file_is_caught_before_the_database_sees_it()
    {
        // Otherwise both rows pass every per-row rule and the unique index throws mid-save — an
        // exception where the admin needed a row number.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channel = await ChannelAsync(client);
        var code = Unique("OUT");

        var result = await ImportAsync(client, $"""
            code,name,channel,time_zone
            {code},First,{channel},{Zone}
            {code},Second,{channel},{Zone}
            """, OutletImportMode.Partial);

        Assert.Equal(1, result.Imported);
        Assert.Contains(result.Problems, problem => problem.Message.Contains("more than once in this file"));
    }

    [Fact]
    public async Task An_outlet_that_already_exists_is_not_overwritten()
    {
        // Insert-only, deliberately. An import that updates would let a stale spreadsheet silently
        // revert back-office corrections across the whole base.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channel = await ChannelAsync(client);
        var code = Unique("OUT");

        await ImportAsync(client, $"""
            code,name,channel,time_zone
            {code},Original Name,{channel},{Zone}
            """);

        var second = await ImportAsync(client, $"""
            code,name,channel,time_zone
            {code},Overwritten Name,{channel},{Zone}
            """, OutletImportMode.Partial);

        Assert.Equal(0, second.Imported);
        Assert.Contains(second.Problems, problem => problem.Message.Contains("already exists"));

        var outlets = (await client.GetFromJsonAsync<PagedList<OutletResponse>>($"/api/outlets?pageSize={Paging.MaxSize}"))!.Items;
        Assert.Equal("Original Name", outlets!.Single(outlet => outlet.Code == code).Name);
    }

    [Fact]
    public async Task A_channel_is_matched_however_the_spreadsheet_capitalised_it()
    {
        // A file saying "modern trade" means the tenant's "Modern Trade" and there is nothing else it
        // could mean — because a channel name is unique per tenant ignoring case. Reading forgives
        // the capitalisation; the channel itself keeps the one it was created with.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channel = await ChannelAsync(client);
        var code = Unique("OUT");

        var result = await ImportAsync(client, $"""
            code,name,channel,time_zone
            {code},Corner Shop,{channel.ToUpperInvariant()},{Zone}
            """);

        Assert.Equal(1, result.Imported);

        var outlets = (await client.GetFromJsonAsync<PagedList<OutletResponse>>($"/api/outlets?pageSize={Paging.MaxSize}"))!.Items;
        Assert.Equal(channel, outlets!.Single(outlet => outlet.Code == code).ChannelName);
    }

    [Fact]
    public async Task A_code_that_differs_only_in_capitalisation_is_the_same_code()
    {
        // One shop entered twice, in the file and against what is already stored. Importing the pair
        // would be the accident rather than the service.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channel = await ChannelAsync(client);
        var code = Unique("OUT").ToUpperInvariant();

        var withinFile = await ImportAsync(client, $"""
            code,name,channel,time_zone
            {code},First,{channel},{Zone}
            {code.ToLowerInvariant()},Second,{channel},{Zone}
            """, OutletImportMode.Partial);

        Assert.Equal(1, withinFile.Imported);
        Assert.Contains(withinFile.Problems, problem => problem.Message.Contains("more than once in this file"));

        var againstStored = await ImportAsync(client, $"""
            code,name,channel,time_zone
            {code.ToLowerInvariant()},Third,{channel},{Zone}
            """, OutletImportMode.Partial);

        Assert.Equal(0, againstStored.Imported);
        Assert.Contains(againstStored.Problems, problem => problem.Message.Contains("already exists"));

        // And the stored code kept the capitalisation it arrived with.
        var outlets = (await client.GetFromJsonAsync<PagedList<OutletResponse>>($"/api/outlets?pageSize={Paging.MaxSize}"))!.Items;
        Assert.Contains(outlets!, outlet => outlet.Code == code);
    }

    [Fact]
    public async Task An_unknown_channel_is_refused_rather_than_created()
    {
        // A typo in one cell would otherwise mint "Modren Trade" as a permanent classification that
        // assortment and pricing rules key off. This path holds outlet:write, not channel:write.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var result = await ImportAsync(client, $"""
            code,name,channel,time_zone
            {Unique("OUT")},Corner Shop,Modren Trade,{Zone}
            """);

        Assert.Equal(0, result.Imported);
        Assert.Contains(result.Problems, problem => problem.Message.Contains("no channel called"));

        var channels = await client.GetFromJsonAsync<List<ChannelResponse>>("/api/outlets/channels");
        Assert.DoesNotContain(channels!, channel => channel.Name == "Modren Trade");
    }

    [Fact]
    public async Task A_row_reports_everything_wrong_with_it_at_once()
    {
        // One pass over the spreadsheet, not one error per upload.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var result = await ImportAsync(client, """
            code,name,channel,time_zone,latitude,longitude
            ,,Nowhere,Mars/Olympus_Mons,910,26.1
            """);

        var messages = result.Problems.Select(problem => problem.Message).ToList();

        Assert.Contains(messages, message => message.Contains("code is required"));
        Assert.Contains(messages, message => message.Contains("name is required"));
        Assert.Contains(messages, message => message.Contains("no channel called"));
        Assert.Contains(messages, message => message.Contains("not a known IANA time zone"));
        Assert.Contains(messages, message => message.Contains("Latitude must be between"));
    }

    [Fact]
    public async Task Half_a_coordinate_is_a_mistake_not_a_partial_answer()
    {
        // Taking it silently would put every outlet with a blank longitude on the Greenwich meridian.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channel = await ChannelAsync(client);

        var result = await ImportAsync(client, $"""
            code,name,channel,time_zone,latitude,longitude
            {Unique("OUT")},Corner Shop,{channel},{Zone},44.4268,
            """);

        Assert.Equal(0, result.Imported);
        Assert.Contains(result.Problems, problem => problem.Message.Contains("both a latitude and a longitude"));
    }

    [Fact]
    public async Task Columns_this_import_did_not_use_are_named_rather_than_dropped()
    {
        // A real export is full of legacy_id and last_modified_by, so refusing the file would be
        // hostile — but a mistyped custom-field header looks exactly the same and must not pass
        // unmentioned.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channel = await ChannelAsync(client);

        var result = await ImportAsync(client, $"""
            code,name,channel,time_zone,legacy_id,chller_count
            {Unique("OUT")},Corner Shop,{channel},{Zone},A-991,3
            """);

        Assert.Equal(1, result.Imported);
        Assert.Contains("legacy_id", result.IgnoredColumns);
        Assert.Contains("chller_count", result.IgnoredColumns);
    }

    [Fact]
    public async Task A_quoted_field_survives_the_delimiter_inside_it()
    {
        // The reason this is not a split on commas.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channel = await ChannelAsync(client);
        var code = Unique("OUT");

        var result = await ImportAsync(client, $"""
            code,name,channel,time_zone,street
            {code},"Smith, Jones & Co",{channel},{Zone},"12 Main St, Unit 3"
            """);

        Assert.Equal(1, result.Imported);

        var outlets = (await client.GetFromJsonAsync<PagedList<OutletResponse>>($"/api/outlets?pageSize={Paging.MaxSize}"))!.Items;
        var imported = outlets!.Single(outlet => outlet.Code == code);

        Assert.Equal("Smith, Jones & Co", imported.Name);
        Assert.Equal("12 Main St, Unit 3", imported.Address?.Street);
    }

    [Fact]
    public async Task A_file_that_is_not_CSV_is_refused_as_a_file()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var wrongType = await client.PostAsync(
            "/api/outlets/import", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);

        var noRows = await client.PostAsync("/api/outlets/import", Csv("code,name,channel,time_zone"));
        Assert.Equal(HttpStatusCode.BadRequest, noRows.StatusCode);

        var empty = await client.PostAsync("/api/outlets/import", Csv(""));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task An_oversized_file_is_refused_rather_than_truncated()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channel = await ChannelAsync(client);

        var rows = string.Join("\n", Enumerable
            .Range(0, OutletImportFormat.MaxRows + 1)
            .Select(index => $"OUT-{index},Shop {index},{channel},{Zone}"));

        var response = await client.PostAsync(
            "/api/outlets/import", Csv($"code,name,channel,time_zone\n{rows}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("at most", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Importing_needs_the_permission_that_creating_one_outlet_needs()
    {
        // `viewer` holds outlet:read and not outlet:write.
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        var response = await viewer.PostAsync(
            "/api/outlets/import", Csv($"code,name,channel,time_zone\nX,Y,Z,{Zone}"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task One_tenants_channel_names_mean_nothing_to_another()
    {
        using var tenantA = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var channel = await ChannelAsync(tenantA);

        var result = await ImportAsync(tenantB, $"""
            code,name,channel,time_zone
            {Unique("OUT")},Corner Shop,{channel},{Zone}
            """);

        Assert.Equal(0, result.Imported);
        Assert.Contains(result.Problems, problem => problem.Message.Contains("no channel called"));
    }
}
