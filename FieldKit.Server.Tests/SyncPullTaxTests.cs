using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.Modules.Products;
using FieldKit.Modules.Sync;

namespace FieldKit.Server.Tests;

/// <summary>
/// Tax rates on the device (<c>OFF-03</c>, <c>PRD-07</c>) — W11 slice 7b.
/// </summary>
/// <remarks>
/// <para>
/// The last pricing input that never travelled. W7 slice 14 gave the device a tax engine and W6
/// slice 13 gave the server the rates; nothing carried one to the other, so an offline device could
/// compute tax it had no rate for.
/// </para>
/// <para>
/// <b>The percentage is a string on this wire, and most of this file is about that.</b> A JSON number
/// would have been through IEEE-754 before <c>decimal.js</c> ever read it, and <c>BR-PRD-9</c> has
/// the device's gross match the server's to the cent.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class SyncPullTaxTests(ServerFixture fixture)
{
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private static async Task<Guid> BindDeviceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/devices", new BindDeviceRequest(Unique("Phone")));

        return (await response.Content.ReadFromJsonAsync<DeviceResponse>())!.Id;
    }

    private static async Task<JsonElement> PullAsync(
        HttpClient client, Guid deviceId, long? taxRates = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/pull", new PullRequest(deviceId, new PullCursors(null, TaxRates: taxRates)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static JsonElement Section(JsonElement pull) =>
        pull.GetProperty("changes").GetProperty("taxRates");

    private static List<JsonElement> Upserts(JsonElement pull) =>
        [.. Section(pull).GetProperty("upserts").EnumerateArray()];

    private static long Cursor(JsonElement pull) => Section(pull).GetProperty("cursor").GetInt64();

    private static async Task<Guid> TaxClassAsync(HttpClient rep)
    {
        var created = await rep.PostAsJsonAsync(
            "/api/products/tax-classes", new TaxClassRequest(Unique("VAT")));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return (await created.Content.ReadFromJsonAsync<TaxClassResponse>())!.Id;
    }

    /// <summary>Replaces a class's rates and hands back what the server stored.</summary>
    private static async Task<IReadOnlyList<TaxRateResponse>> SetRatesAsync(
        HttpClient rep, Guid taxClassId, params TaxRateRequest[] rates)
    {
        var response = await rep.PutAsJsonAsync(
            $"/api/products/tax-classes/{taxClassId}/rates", new SetTaxRatesRequest(rates));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<List<TaxRateResponse>>())!;
    }

    private static TaxRateRequest Rate(
        string country, string percentage, string from = "2026-01-01", string? to = null) =>
        new(country, percentage, DateOnly.Parse(from), to is null ? null : DateOnly.Parse(to));

    [Fact]
    public async Task A_rate_reaches_the_device_with_its_percentage_as_a_string()
    {
        /*
         * The load-bearing assertion of the whole slice.
         *
         * 19.75 rather than 19 on purpose: an integer percentage survives a float round trip
         * unharmed and would pass whatever the wire format was. A fractional one is where a JSON
         * number and `decimal.js` first disagree, which is the failure this feed exists to avoid.
         */
        using var rep = fixture.CreateAuthenticatedClient();

        var device = await BindDeviceAsync(rep);
        var before = Cursor(await PullAsync(rep, device));

        var taxClassId = await TaxClassAsync(rep);
        var stored = await SetRatesAsync(rep, taxClassId, Rate("RO", "19.75"));

        var pull = await PullAsync(rep, device, before);

        var rate = Assert.Single(Upserts(pull), candidate =>
            candidate.GetProperty("id").GetGuid() == stored[0].Id);

        Assert.Equal(JsonValueKind.String, rate.GetProperty("percentage").ValueKind);
        Assert.Equal("19.75", rate.GetProperty("percentage").GetString());
        Assert.Equal(taxClassId, rate.GetProperty("taxClassId").GetGuid());
        Assert.Equal("RO", rate.GetProperty("countryCode").GetString());
    }

    [Fact]
    public async Task A_rate_carries_the_window_it_applies_in()
    {
        /*
         * Without the dates the device cannot resolve — `TaxEngine.Resolve` picks the rate whose
         * half-open window contains the capture date, and a country with a rate change in the middle
         * of the year has two rows that are otherwise identical. An open end must arrive as null
         * rather than as a far-future date, because "still in force" is not the same fact as "ends
         * in 9999".
         */
        using var rep = fixture.CreateAuthenticatedClient();

        var device = await BindDeviceAsync(rep);
        var before = Cursor(await PullAsync(rep, device));

        var taxClassId = await TaxClassAsync(rep);
        var stored = await SetRatesAsync(
            rep,
            taxClassId,
            Rate("RO", "19.00", "2026-01-01", "2026-07-01"),
            Rate("RO", "21.00", "2026-07-01"));

        var sent = Upserts(await PullAsync(rep, device, before))
            .Where(candidate => stored.Any(row => row.Id == candidate.GetProperty("id").GetGuid()))
            .OrderBy(candidate => candidate.GetProperty("effectiveFrom").GetString())
            .ToList();

        Assert.Equal(2, sent.Count);

        Assert.Equal("2026-01-01", sent[0].GetProperty("effectiveFrom").GetString());
        Assert.Equal("2026-07-01", sent[0].GetProperty("effectiveTo").GetString());

        Assert.Equal("2026-07-01", sent[1].GetProperty("effectiveFrom").GetString());
        Assert.Equal(JsonValueKind.Null, sent[1].GetProperty("effectiveTo").ValueKind);
    }

    [Fact]
    public async Task A_device_that_has_pulled_is_told_nothing_twice()
    {
        // The cursor's whole job, and the only thing standing between a rep on a train and a full
        // rate table on every sync.
        using var rep = fixture.CreateAuthenticatedClient();

        var device = await BindDeviceAsync(rep);

        await SetRatesAsync(rep, await TaxClassAsync(rep), Rate("RO", "19.00"));

        var first = await PullAsync(rep, device);
        var again = await PullAsync(rep, device, Cursor(first));

        Assert.Empty(Upserts(again));
    }

    [Fact]
    public async Task A_rate_replaced_by_a_later_edit_arrives_as_a_tombstone()
    {
        /*
         * The PUT replaces the set rather than editing rows — a rate's identity is its country and
         * start date together (see `TaxEndpoints`), so moving a date deletes and recreates. That
         * makes tombstones the *normal* path here rather than the rare one: a device that only ever
         * upserted would keep resolving against a rate its tenant abolished, and being taxed at last
         * year's VAT is exactly the sort of wrong that nobody notices until the invoice.
         */
        using var rep = fixture.CreateAuthenticatedClient();

        var device = await BindDeviceAsync(rep);

        var taxClassId = await TaxClassAsync(rep);
        var original = await SetRatesAsync(rep, taxClassId, Rate("RO", "19.00"));

        var after = Cursor(await PullAsync(rep, device));

        await SetRatesAsync(rep, taxClassId, Rate("RO", "21.00"));

        var pull = await PullAsync(rep, device, after);

        Assert.Contains(
            Section(pull).GetProperty("tombstones").EnumerateArray(),
            tombstone => tombstone.GetProperty("id").GetGuid() == original[0].Id);

        Assert.Contains(
            Upserts(pull), candidate => candidate.GetProperty("percentage").GetString() == "21.00");
    }

    [Fact]
    public async Task Another_tenants_rates_are_not_in_this_devices_pull()
    {
        // Rates are tenant-wide, like the workflows they sit beside — nothing here narrows by
        // territory, so the tenant filter is the only thing between two tenants' VAT.
        using var rep = fixture.CreateAuthenticatedClient();
        using var other = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var device = await BindDeviceAsync(rep);

        var theirs = await SetRatesAsync(other, await TaxClassAsync(other), Rate("RO", "5.00"));

        var pull = await PullAsync(rep, device);

        Assert.DoesNotContain(
            Upserts(pull), candidate => candidate.GetProperty("id").GetGuid() == theirs[0].Id);
    }
}
