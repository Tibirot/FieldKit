using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Products;

namespace FieldKit.Server.Tests;

/// <summary>
/// Price lists, their currency and their window (<c>PRD-03</c>) — W6 slice 5.
/// </summary>
/// <remarks>
/// This is the first time <c>Money</c> crosses the wire, so several of these are about the shape of
/// the JSON rather than about behaviour. That is deliberate: <c>BR-PRD-8</c> is a rule about
/// representation, and a test that round-trips through a typed client cannot see it.
/// </remarks>
[Collection(ServerCollection.Name)]
public class PriceListTests(ServerFixture fixture)
{
    private const string Lists = "/api/products/price-lists";

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..20];

    private static DateOnly Today => new(2026, 1, 1);

    private static async Task<Guid> ProductAsync(HttpClient writer)
    {
        var sku = Unique("SKU");
        var response = await writer.PostAsJsonAsync("/api/products", new CreateProductRequest(sku, sku));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!.Id;
    }

    private static async Task<PriceListResponse> ListAsync(
        HttpClient writer, string currency = "EUR", DateOnly? from = null, DateOnly? to = null)
    {
        var response = await writer.PostAsJsonAsync(
            Lists, new CreatePriceListRequest(Unique("List"), currency, from ?? Today, to));

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<PriceListResponse>())!;
    }

    private static async Task<IReadOnlyList<PriceResponse>> SetPricesAsync(
        HttpClient writer, Guid listId, params PriceLineRequest[] prices)
    {
        var response = await writer.PutAsJsonAsync(
            $"{Lists}/{listId}/prices", new SetPricesRequest(prices));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await WireJson.ReadAsync<List<PriceResponse>>(response))!;
    }

    [Fact]
    public async Task An_amount_leaves_this_api_as_a_string_never_a_number()
    {
        // BR-PRD-8, and the reason the converter is registered globally rather than attributed.
        // JavaScript has no decimal type: a JSON *number* becomes an IEEE-754 float the moment the
        // browser parses it, and the device pricing engine would be doing float maths before it ever
        // reached decimal.js. Asserted on the raw body, because a typed client deserializes either
        // form happily and would prove nothing.
        using var writer = fixture.CreateAuthenticatedClient();
        var list = await ListAsync(writer);
        var productId = await ProductAsync(writer);

        await SetPricesAsync(writer, list.Id, new PriceLineRequest(productId, "12.50"));

        var body = await writer.GetStringAsync($"{Lists}/{list.Id}/prices");

        Assert.Contains("""{"amount":"12.50","currency":"EUR"}""", body);
        Assert.DoesNotContain("\"amount\":12.5", body);
    }

    [Fact]
    public async Task A_price_keeps_its_minor_units_rather_than_being_trimmed()
    {
        // "12.50", not "12.5". A client should not have to know a currency's minor units to render
        // what the server already knows.
        using var writer = fixture.CreateAuthenticatedClient();
        var list = await ListAsync(writer);
        var productId = await ProductAsync(writer);

        await SetPricesAsync(writer, list.Id, new PriceLineRequest(productId, "12.5"));

        Assert.Contains("\"amount\":\"12.50\"", await writer.GetStringAsync($"{Lists}/{list.Id}/prices"));
    }

    [Fact]
    public async Task A_sub_cent_unit_price_survives_storage()
    {
        // The reason the column is numeric(18,4) rather than (18,2). A case of 24 at 11.99 divides
        // to 0.4996 per unit; truncating at the column would lose the money the rounding policy
        // (BR-PRD-9) exists to control, before the engine ever sees it.
        using var writer = fixture.CreateAuthenticatedClient();
        var list = await ListAsync(writer);
        var productId = await ProductAsync(writer);

        await SetPricesAsync(writer, list.Id, new PriceLineRequest(productId, "0.4996"));

        var price = Assert.Single(await WireJson.GetAsync<List<PriceResponse>>(writer, $"{Lists}/{list.Id}/prices"));
        Assert.Equal(0.4996m, price.Price.Amount);
    }

    [Fact]
    public async Task A_price_carries_the_lists_currency_rather_than_its_own()
    {
        // BR-PRD-1. The currency is held once, on the list, so a line cannot disagree with its
        // neighbours and no read has to ask what a number means.
        using var writer = fixture.CreateAuthenticatedClient();
        var list = await ListAsync(writer, currency: "ron");
        var productId = await ProductAsync(writer);

        var prices = await SetPricesAsync(writer, list.Id, new PriceLineRequest(productId, "59.90"));

        // Upper-cased on the way in, so "ron" and "RON" are one currency rather than two.
        Assert.Equal("RON", list.Currency);
        Assert.Equal("RON", Assert.Single(prices).Price.Currency);
    }

    [Theory]
    [InlineData("Euro")]
    [InlineData("EU")]
    [InlineData("EUR ")]
    public async Task A_currency_that_is_not_a_three_letter_code_is_refused(string currency)
    {
        using var writer = fixture.CreateAuthenticatedClient();

        var response = await writer.PostAsJsonAsync(
            Lists, new CreatePriceListRequest(Unique("List"), currency, Today, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("currency", problem.Field);
        Assert.Equal("product.priceList.currencyInvalid", problem.Code);
    }

    [Fact]
    public async Task A_window_that_ends_before_it_starts_is_refused_and_so_is_one_that_ends_when_it_starts()
    {
        // Half-open, so equal dates are an empty window rather than a single day — a list that is
        // never in effect, which nobody meant to author.
        using var writer = fixture.CreateAuthenticatedClient();

        foreach (var end in new[] { Today.AddDays(-1), Today })
        {
            var response = await writer.PostAsJsonAsync(
                Lists, new CreatePriceListRequest(Unique("List"), "EUR", Today, end));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(
                "product.priceList.windowInverted",
                Assert.Single(await Refusals.ProblemsOf(response)).Code);
        }
    }

    [Fact]
    public async Task An_open_ended_list_is_allowed()
    {
        using var writer = fixture.CreateAuthenticatedClient();

        var list = await ListAsync(writer, to: null);

        Assert.Null(list.EffectiveTo);
    }

    [Fact]
    public async Task The_currency_cannot_be_changed_after_the_fact()
    {
        // Not by refusing it — by there being nowhere to put it. Changing a list's currency would
        // reinterpret every price in it, and 12.50 EUR becoming 12.50 RON is not a conversion.
        using var writer = fixture.CreateAuthenticatedClient();
        var list = await ListAsync(writer, currency: "EUR");

        var response = await writer.PutAsJsonAsync(
            $"{Lists}/{list.Id}", new UpdatePriceListRequest("Renamed", Today, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<PriceListResponse>();
        Assert.Equal("Renamed", updated!.Name);
        Assert.Equal("EUR", updated.Currency);
    }

    [Fact]
    public async Task A_price_of_zero_is_allowed_and_a_negative_one_is_not()
    {
        // Zero is a free line, a sample, a listing fee absorbed elsewhere. Negative is a rebate,
        // which is a promotion's job — letting one in here would have every total quietly able to go
        // the wrong way.
        using var writer = fixture.CreateAuthenticatedClient();
        var list = await ListAsync(writer);
        var free = await ProductAsync(writer);

        Assert.Equal(0m, Assert.Single(await SetPricesAsync(
            writer, list.Id, new PriceLineRequest(free, "0"))).Price.Amount);

        var negative = await writer.PutAsJsonAsync(
            $"{Lists}/{list.Id}/prices",
            new SetPricesRequest([new PriceLineRequest(free, "-1.00")]));

        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Equal("product.price.negative", Assert.Single(await Refusals.ProblemsOf(negative)).Code);
    }

    [Fact]
    public async Task An_amount_that_is_not_a_number_is_refused_rather_than_read_as_zero()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        var list = await ListAsync(writer);
        var productId = await ProductAsync(writer);

        var response = await writer.PutAsJsonAsync(
            $"{Lists}/{list.Id}/prices",
            new SetPricesRequest([new PriceLineRequest(productId, "twelve fifty")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("product.price.notANumber", Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task A_comma_decimal_is_refused_rather_than_read_as_a_hundredfold_price()
    {
        // Parsed invariantly on purpose. "12,50" read under a comma-decimal culture becomes 1250 —
        // a hundredfold error that looks like a plausible price and would only appear on machines
        // configured a certain way.
        using var writer = fixture.CreateAuthenticatedClient();
        var list = await ListAsync(writer);
        var productId = await ProductAsync(writer);

        var response = await writer.PutAsJsonAsync(
            $"{Lists}/{list.Id}/prices",
            new SetPricesRequest([new PriceLineRequest(productId, "12,50")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("product.price.notANumber", Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task Setting_prices_replaces_them_and_repricing_keeps_the_line()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        var list = await ListAsync(writer);
        var first = await ProductAsync(writer);
        var second = await ProductAsync(writer);

        await SetPricesAsync(writer, list.Id, new PriceLineRequest(first, "10.00"));

        var repriced = await SetPricesAsync(writer, list.Id, new PriceLineRequest(first, "11.00"));
        Assert.Equal(11.00m, Assert.Single(repriced).Price.Amount);

        var replaced = await SetPricesAsync(writer, list.Id, new PriceLineRequest(second, "9.00"));
        Assert.Equal(second, Assert.Single(replaced).ProductId);
    }

    [Fact]
    public async Task The_same_product_priced_twice_is_refused()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        var list = await ListAsync(writer);
        var productId = await ProductAsync(writer);

        var response = await writer.PutAsJsonAsync(
            $"{Lists}/{list.Id}/prices",
            new SetPricesRequest(
                [new PriceLineRequest(productId, "10.00"), new PriceLineRequest(productId, "11.00")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.price.duplicateProduct",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task One_tenants_price_lists_are_invisible_to_another()
    {
        using var tenantA = fixture.CreateAuthenticatedClient();
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var mine = await ListAsync(tenantA);

        var theirs = await tenantB.GetFromJsonAsync<List<PriceListResponse>>(Lists);
        Assert.DoesNotContain(theirs!, list => list.Id == mine.Id);

        var byId = await tenantB.GetAsync($"{Lists}/{mine.Id}/prices");
        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);
    }

    [Fact]
    public async Task Reading_prices_and_setting_them_are_different_capabilities()
    {
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Lists)).StatusCode);

        var write = await viewer.PostAsJsonAsync(
            Lists, new CreatePriceListRequest(Unique("Nope"), "EUR", Today, null));

        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }
}
