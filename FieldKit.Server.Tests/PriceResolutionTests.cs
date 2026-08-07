using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Products;

namespace FieldKit.Server.Tests;

/// <summary>
/// Resolving a price for an outlet on a date over HTTP (<c>PRD-04</c>) — W6 slice 7.
/// </summary>
/// <remarks>
/// The precedence rules themselves are pinned by
/// <see cref="PriceResolutionVectorTests"/> against the shared vector file, not here. What these
/// tests cover is everything the vectors cannot reach: that the right candidates are loaded, that the
/// channel comes from the outlet, that tenants stay apart, and that the answer crosses the wire as
/// <see cref="FieldKit.SharedKernel.Money"/>. Restating a precedence case here would give it two
/// homes and let them drift.
/// </remarks>
[Collection(ServerCollection.Name)]
public class PriceResolutionTests(ServerFixture fixture)
{
    private const string Lists = "/api/products/price-lists";

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..20];

    private static string Prices(Guid outletId, string on) =>
        $"/api/products/outlets/{outletId}/prices?on={on}";

    private static async Task<Guid> ChannelAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    private static async Task<Guid> OutletAsync(HttpClient admin, Guid channelId)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(
                Unique("OUT"), "Corner Shop", channelId, null, null, "Europe/Bucharest"));

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    private static async Task<Guid> ProductAsync(HttpClient writer)
    {
        var response = await writer.PostAsJsonAsync(
            "/api/products", new { sku = Unique("SKU"), name = "Cola 500ml" });

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!.Id;
    }

    /// <summary>Creates a list, prices one product in it, and points it at a scope.</summary>
    private static async Task<Guid> ListAsync(
        HttpClient writer,
        Guid productId,
        string amount,
        DateOnly from,
        DateOnly? to = null,
        Guid? channelId = null,
        Guid? outletId = null,
        string currency = "EUR")
    {
        var created = await writer.PostAsJsonAsync(
            Lists, new CreatePriceListRequest(Unique("List"), currency, from, to));

        Assert.True(
            created.StatusCode == HttpStatusCode.Created,
            $"{created.StatusCode}: {await created.Content.ReadAsStringAsync()}");

        var listId = (await created.Content.ReadFromJsonAsync<PriceListResponse>())!.Id;

        var priced = await writer.PutAsJsonAsync(
            $"{Lists}/{listId}/prices",
            new SetPricesRequest([new PriceLineRequest(productId, amount)]));

        Assert.True(
            priced.StatusCode == HttpStatusCode.OK,
            $"{priced.StatusCode}: {await priced.Content.ReadAsStringAsync()}");

        var assigned = await writer.PutAsJsonAsync(
            $"{Lists}/{listId}/assignments",
            new SetAssignmentsRequest(
                channelId is { } c ? [c] : [], outletId is { } o ? [o] : []));

        Assert.True(
            assigned.StatusCode == HttpStatusCode.OK,
            $"{assigned.StatusCode}: {await assigned.Content.ReadAsStringAsync()}");

        return listId;
    }

    private static async Task<IReadOnlyList<ResolvedPriceResponse>> ResolveAsync(
        HttpClient client, Guid outletId, string on)
    {
        var response = await client.GetAsync(Prices(outletId, on));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await WireJson.ReadAsync<List<ResolvedPriceResponse>>(response))!;
    }

    [Fact]
    public async Task An_outlet_is_charged_its_channels_price()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer);

        var listId = await ListAsync(
            writer, productId, "12.50", new DateOnly(2026, 1, 1), channelId: channelId);

        var resolved = Assert.Single(
            await ResolveAsync(writer, outletId, "2026-03-15"),
            price => price.ProductId == productId);

        Assert.Equal(12.50m, resolved.Price.Amount);
        Assert.Equal("EUR", resolved.Price.Currency);
        Assert.Equal(listId, resolved.PriceListId);
        Assert.Equal(PriceScope.Channel, resolved.Scope);
    }

    [Fact]
    public async Task A_price_set_for_the_shop_itself_beats_its_channels()
    {
        // The end-to-end shape of BR-PRD-2, once. The precedence rule is pinned by the vectors; what
        // this proves is that both scopes actually reach the resolver — an endpoint that loaded only
        // channel assignments would pass every vector and still price this shop wrong.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer);

        await ListAsync(writer, productId, "12.50", new DateOnly(2026, 1, 1), channelId: channelId);
        var special = await ListAsync(
            writer, productId, "11.00", new DateOnly(2026, 1, 1), outletId: outletId);

        var resolved = Assert.Single(
            await ResolveAsync(writer, outletId, "2026-03-15"),
            price => price.ProductId == productId);

        Assert.Equal(11.00m, resolved.Price.Amount);
        Assert.Equal(special, resolved.PriceListId);
        Assert.Equal(PriceScope.Outlet, resolved.Scope);
    }

    [Fact]
    public async Task A_list_that_reaches_a_different_channel_does_not_price_this_outlet()
    {
        // The other half of "the right candidates are loaded". A resolver handed every list in the
        // tenant would answer with this one.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var elsewhere = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer);

        await ListAsync(writer, productId, "12.50", new DateOnly(2026, 1, 1), channelId: elsewhere);

        Assert.DoesNotContain(
            await ResolveAsync(writer, outletId, "2026-03-15"),
            price => price.ProductId == productId);
    }

    [Fact]
    public async Task A_product_with_no_covering_list_is_simply_absent()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer);

        // Priced, but the window closed before the date asked about.
        await ListAsync(
            writer,
            productId,
            "12.50",
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 2, 1),
            channelId: channelId);

        Assert.DoesNotContain(
            await ResolveAsync(writer, outletId, "2026-03-15"),
            price => price.ProductId == productId);
    }

    [Fact]
    public async Task Asking_about_particular_products_narrows_the_answer()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var wanted = await ProductAsync(writer);
        var other = await ProductAsync(writer);

        await ListAsync(writer, wanted, "12.50", new DateOnly(2026, 1, 1), channelId: channelId);
        await ListAsync(writer, other, "9.00", new DateOnly(2026, 1, 1), channelId: channelId);

        var response = await writer.GetAsync(
            $"/api/products/outlets/{outletId}/prices?on=2026-03-15&productId={wanted}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var resolved = (await WireJson.ReadAsync<List<ResolvedPriceResponse>>(response))!;

        Assert.Equal(wanted, Assert.Single(resolved).ProductId);
    }

    [Fact]
    public async Task Money_crosses_the_wire_as_a_string_rather_than_a_number()
    {
        // BR-PRD-8, asserted on the raw body. A client that parsed 12.50 as a JSON number would hold
        // a float, and this endpoint is the one a device prices an order from.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer);

        await ListAsync(writer, productId, "12.50", new DateOnly(2026, 1, 1), channelId: channelId);

        var body = await (await writer.GetAsync(Prices(outletId, "2026-03-15"))).Content
            .ReadAsStringAsync();

        Assert.Contains("\"amount\":\"12.50\"", body);
        Assert.Contains("\"scope\":\"Channel\"", body);
    }

    [Fact]
    public async Task The_date_is_required_and_must_be_a_date()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var outletId = await OutletAsync(admin, await ChannelAsync(admin));

        var missing = await writer.GetAsync($"/api/products/outlets/{outletId}/prices");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(
            "product.price.dateRequired",
            Assert.Single(await Refusals.ProblemsOf(missing)).Code);

        var malformed = await writer.GetAsync(Prices(outletId, "15-03-2026"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        var problem = Assert.Single(await Refusals.ProblemsOf(malformed));
        Assert.Equal("on", problem.Field);
        Assert.Equal("product.price.dateMalformed", problem.Code);
    }

    [Fact]
    public async Task An_outlet_that_does_not_exist_is_not_found()
    {
        using var writer = fixture.CreateAuthenticatedClient();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await writer.GetAsync(Prices(Guid.NewGuid(), "2026-03-15"))).StatusCode);
    }

    [Fact]
    public async Task Another_tenants_outlet_reads_as_not_found_rather_than_forbidden()
    {
        // IOutletClassification is tenant-filtered, so tenant B's outlet is absent from the result and
        // surfaces as 404 — the only answer that does not confirm it exists elsewhere.
        using var tenantBAdmin = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);
        var outletOfB = await OutletAsync(tenantBAdmin, await ChannelAsync(tenantBAdmin));

        using var writer = fixture.CreateAuthenticatedClient();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await writer.GetAsync(Prices(outletOfB, "2026-03-15"))).StatusCode);
    }

    [Fact]
    public async Task Reading_a_price_needs_only_read_permission()
    {
        // A rep prices an order; they do not author price lists. If this needed product:write the
        // whole field force would have to be able to reprice the tenant.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        var outletId = await OutletAsync(admin, await ChannelAsync(admin));

        Assert.Equal(
            HttpStatusCode.OK, (await viewer.GetAsync(Prices(outletId, "2026-03-15"))).StatusCode);
    }
}
