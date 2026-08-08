using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Products;

namespace FieldKit.Server.Tests;

/// <summary>
/// Tax rates and which one applies (<c>PRD-07</c>) — W6 slice 13.
/// </summary>
/// <remarks>
/// Rate selection and the rounding policy are pinned by <see cref="TaxVectorTests"/> against the
/// shared vector file. What these cover is what the vectors cannot reach: that rates hang off a tax
/// class, that the country comes from the outlet, and — the one that matters most — that a missing
/// country or a missing rate answers <i>unknown</i> rather than zero.
/// </remarks>
[Collection(ServerCollection.Name)]
public class TaxTests(ServerFixture fixture)
{
    private const string Classes = "/api/products/tax-classes";

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..20];

    private static DateOnly Opens => new(2026, 1, 1);

    private static string Url(Guid outletId, Guid productId, string on = "2026-06-15") =>
        $"/api/products/outlets/{outletId}/tax?on={on}&productId={productId}";

    private static async Task<Guid> ChannelAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/outlets/channels", new { name = Unique("Channel") });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    /// <summary>An outlet with an address, and so a country — or without one when asked.</summary>
    private static async Task<Guid> OutletAsync(
        HttpClient admin, Guid channelId, string? countryCode = "RO")
    {
        var response = await admin.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(
                Unique("OUT"),
                "Corner Shop",
                channelId,
                "Europe/Bucharest",
                Address: countryCode is null
                    ? null
                    : new Address("Calea Dorobanți 172", "București", "010581", countryCode)));

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    private static async Task<Guid> TaxClassAsync(HttpClient writer)
    {
        var response = await writer.PostAsJsonAsync(Classes, new { name = Unique("Tax") });

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<TaxClassResponse>())!.Id;
    }

    private static async Task<Guid> ProductAsync(HttpClient writer, Guid? taxClassId = null)
    {
        var response = await writer.PostAsJsonAsync(
            "/api/products", new { sku = Unique("SKU"), name = "Cola 500ml", taxClassId });

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!.Id;
    }

    private static async Task<HttpResponseMessage> SetRatesAsync(
        HttpClient writer, Guid taxClassId, params TaxRateRequest[] rates) =>
        await writer.PutAsJsonAsync($"{Classes}/{taxClassId}/rates", new SetTaxRatesRequest(rates));

    private static async Task<ResolvedTaxResponse?> ResolveAsync(
        HttpClient client, Guid outletId, Guid productId, string on = "2026-06-15")
    {
        var response = await client.GetAsync(Url(outletId, productId, on));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<TaxResolutionResponse>())!.Tax;
    }

    [Fact]
    public async Task A_rate_is_authored_against_a_class_and_a_country()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        var taxClassId = await TaxClassAsync(writer);

        var response = await SetRatesAsync(
            writer,
            taxClassId,
            new TaxRateRequest("ro", "19", Opens),
            new TaxRateRequest("DE", "19", Opens));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var rates = (await response.Content.ReadFromJsonAsync<List<TaxRateResponse>>())!;

        Assert.Equal(2, rates.Count);
        Assert.Equal(["DE", "RO"], rates.Select(rate => rate.CountryCode));
        Assert.All(rates, rate => Assert.Equal("19.00", rate.Percentage));
    }

    [Fact]
    public async Task A_zero_rate_is_authorable_and_is_not_the_same_as_none()
    {
        // The distinction the design turns on: zero-rated goods are taxed at 0%, and forcing a tenant
        // to express that by omitting a rate would make "no VAT here" and "we never set this up" the
        // same state.
        using var writer = fixture.CreateAuthenticatedClient();
        var taxClassId = await TaxClassAsync(writer);

        var response = await SetRatesAsync(writer, taxClassId, new TaxRateRequest("RO", "0", Opens));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rates = (await response.Content.ReadFromJsonAsync<List<TaxRateResponse>>())!;

        Assert.Equal("0.00", Assert.Single(rates).Percentage);
    }

    [Fact]
    public async Task A_rate_outside_zero_to_a_hundred_is_refused()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        var taxClassId = await TaxClassAsync(writer);

        var response = await SetRatesAsync(
            writer, taxClassId, new TaxRateRequest("RO", "-1", Opens));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("rates[0].percentage", problem.Field);
        Assert.Equal("product.tax.percentageOutOfRange", problem.Code);
    }

    [Fact]
    public async Task A_country_that_is_not_a_two_letter_code_is_refused()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        var taxClassId = await TaxClassAsync(writer);

        var response = await SetRatesAsync(
            writer, taxClassId, new TaxRateRequest("Romania", "19", Opens));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("rates[0].countryCode", problem.Field);
        Assert.Equal("product.tax.countryInvalid", problem.Code);
    }

    [Fact]
    public async Task A_comma_decimal_is_refused_rather_than_read_as_thousands()
    {
        // "19,5" would parse to 195 if thousands separators were allowed — a tenfold tax rate that
        // the range check would then catch, but only by luck. Same refusal as every other rate here.
        using var writer = fixture.CreateAuthenticatedClient();
        var taxClassId = await TaxClassAsync(writer);

        var response = await SetRatesAsync(
            writer, taxClassId, new TaxRateRequest("RO", "19,5", Opens));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.tax.percentageNotANumber",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task Two_rates_for_the_same_country_and_start_date_are_refused()
    {
        // The unique index would refuse these anyway; the endpoint names the field and the count
        // instead of surfacing a constraint violation.
        using var writer = fixture.CreateAuthenticatedClient();
        var taxClassId = await TaxClassAsync(writer);

        var response = await SetRatesAsync(
            writer,
            taxClassId,
            new TaxRateRequest("RO", "19", Opens),
            new TaxRateRequest("RO", "21", Opens));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.tax.rateDuplicated",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task Successive_rates_for_one_country_are_fine()
    {
        // How a rate change is authored: the old one ends exactly where the new one starts.
        using var writer = fixture.CreateAuthenticatedClient();
        var taxClassId = await TaxClassAsync(writer);

        var response = await SetRatesAsync(
            writer,
            taxClassId,
            new TaxRateRequest("RO", "19", Opens, new DateOnly(2026, 7, 1)),
            new TaxRateRequest("RO", "21", new DateOnly(2026, 7, 1)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            2, (await response.Content.ReadFromJsonAsync<List<TaxRateResponse>>())!.Count);
    }

    [Fact]
    public async Task An_inverted_window_is_refused()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        var taxClassId = await TaxClassAsync(writer);

        var response = await SetRatesAsync(
            writer, taxClassId, new TaxRateRequest("RO", "19", Opens, Opens));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.tax.windowInverted",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task Setting_rates_replaces_the_whole_set()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        var taxClassId = await TaxClassAsync(writer);

        await SetRatesAsync(writer, taxClassId, new TaxRateRequest("RO", "19", Opens));

        var response = await SetRatesAsync(
            writer, taxClassId, new TaxRateRequest("DE", "19", Opens));

        var rates = (await response.Content.ReadFromJsonAsync<List<TaxRateResponse>>())!;
        Assert.Equal("DE", Assert.Single(rates).CountryCode);
    }

    [Fact]
    public async Task The_rate_that_applies_comes_from_the_outlets_country()
    {
        // The end-to-end point of growing IOutletClassification: Products cannot see the outlet's
        // address (AT-1), so without the contract the country would have to be guessed or passed in.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var romanian = await OutletAsync(admin, channelId, "RO");
        var german = await OutletAsync(admin, channelId, "DE");

        var taxClassId = await TaxClassAsync(writer);
        var productId = await ProductAsync(writer, taxClassId);

        await SetRatesAsync(
            writer,
            taxClassId,
            new TaxRateRequest("RO", "19", Opens),
            new TaxRateRequest("DE", "7", Opens));

        Assert.Equal("19.00", (await ResolveAsync(writer, romanian, productId))!.Percentage);
        Assert.Equal("7.00", (await ResolveAsync(writer, german, productId))!.Percentage);
    }

    [Fact]
    public async Task A_rate_change_takes_over_on_its_announced_day()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var outletId = await OutletAsync(admin, await ChannelAsync(admin));
        var taxClassId = await TaxClassAsync(writer);
        var productId = await ProductAsync(writer, taxClassId);

        await SetRatesAsync(
            writer,
            taxClassId,
            new TaxRateRequest("RO", "19", Opens, new DateOnly(2026, 7, 1)),
            new TaxRateRequest("RO", "21", new DateOnly(2026, 7, 1)));

        Assert.Equal(
            "19.00", (await ResolveAsync(writer, outletId, productId, "2026-06-30"))!.Percentage);

        Assert.Equal(
            "21.00", (await ResolveAsync(writer, outletId, productId, "2026-07-01"))!.Percentage);
    }

    [Fact]
    public async Task An_outlet_with_no_country_answers_unknown_rather_than_untaxed()
    {
        // The case that decides whether a setup gap invoices as tax-free. Null, never zero — and
        // asserted on the raw body, because the difference between them is the whole point.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var outletId = await OutletAsync(admin, await ChannelAsync(admin), countryCode: null);
        var taxClassId = await TaxClassAsync(writer);
        var productId = await ProductAsync(writer, taxClassId);

        await SetRatesAsync(writer, taxClassId, new TaxRateRequest("RO", "19", Opens));

        var response = await writer.GetAsync(Url(outletId, productId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"tax\":null}", (await response.Content.ReadAsStringAsync()).Trim());
    }

    [Fact]
    public async Task No_rate_for_this_country_answers_unknown()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var outletId = await OutletAsync(admin, await ChannelAsync(admin), "RO");
        var taxClassId = await TaxClassAsync(writer);
        var productId = await ProductAsync(writer, taxClassId);

        // Authored for somewhere else entirely.
        await SetRatesAsync(writer, taxClassId, new TaxRateRequest("DE", "19", Opens));

        Assert.Null(await ResolveAsync(writer, outletId, productId));
    }

    [Fact]
    public async Task A_product_with_no_tax_class_answers_unknown()
    {
        // The same kind of unknown: nothing has said what kind of thing this is, so nothing can say
        // what it costs to sell.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var outletId = await OutletAsync(admin, await ChannelAsync(admin), "RO");
        var productId = await ProductAsync(writer);

        Assert.Null(await ResolveAsync(writer, outletId, productId));
    }

    [Fact]
    public async Task A_zero_rate_resolves_to_zero_rather_than_to_unknown()
    {
        // The pair to the case above, and the reason both exist: one answers 0.00, the other answers
        // nothing, and a caller has to be able to tell them apart.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var outletId = await OutletAsync(admin, await ChannelAsync(admin), "RO");
        var taxClassId = await TaxClassAsync(writer);
        var productId = await ProductAsync(writer, taxClassId);

        await SetRatesAsync(writer, taxClassId, new TaxRateRequest("RO", "0", Opens));

        var resolved = await ResolveAsync(writer, outletId, productId);

        Assert.NotNull(resolved);
        Assert.Equal("0.00", resolved.Percentage);
        Assert.Equal("RO", resolved.CountryCode);
        Assert.Equal(taxClassId, resolved.TaxClassId);
    }

    [Fact]
    public async Task Another_tenants_rates_do_not_apply()
    {
        using var tenantBAdmin = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);
        var outletOfB = await OutletAsync(tenantBAdmin, await ChannelAsync(tenantBAdmin), "RO");

        using var writer = fixture.CreateAuthenticatedClient();
        var productId = await ProductAsync(writer, await TaxClassAsync(writer));

        // Tenant B's outlet is absent from a tenant-filtered contract, which surfaces as 404.
        Assert.Equal(
            HttpStatusCode.NotFound, (await writer.GetAsync(Url(outletOfB, productId))).StatusCode);
    }

    [Fact]
    public async Task The_date_and_the_product_are_both_required()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var outletId = await OutletAsync(admin, await ChannelAsync(admin), "RO");
        var productId = await ProductAsync(writer);
        var url = $"/api/products/outlets/{outletId}/tax";

        var noDate = await writer.GetAsync($"{url}?productId={productId}");
        Assert.Equal(HttpStatusCode.BadRequest, noDate.StatusCode);
        Assert.Equal(
            "product.tax.dateRequired",
            Assert.Single(await Refusals.ProblemsOf(noDate)).Code);

        var noProduct = await writer.GetAsync($"{url}?on=2026-06-15");
        Assert.Equal(HttpStatusCode.BadRequest, noProduct.StatusCode);
        Assert.Equal(
            "product.tax.productRequired",
            Assert.Single(await Refusals.ProblemsOf(noProduct)).Code);
    }

    [Fact]
    public async Task Rates_on_a_class_that_does_not_exist_are_not_found()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        var absent = Guid.NewGuid();

        Assert.Equal(
            HttpStatusCode.NotFound, (await writer.GetAsync($"{Classes}/{absent}/rates")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await SetRatesAsync(writer, absent)).StatusCode);
    }

    [Fact]
    public async Task Reading_rates_and_setting_them_are_different_capabilities()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);
        var taxClassId = await TaxClassAsync(writer);

        Assert.Equal(
            HttpStatusCode.OK, (await viewer.GetAsync($"{Classes}/{taxClassId}/rates")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await SetRatesAsync(viewer, taxClassId, new TaxRateRequest("RO", "19", Opens))).StatusCode);
    }
}
