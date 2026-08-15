using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Products;
using FieldKit.Modules.Products.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// What an order costs, gathered from real data (<c>ORD-02</c>, <c>ORD-03</c>) — W11 slice 2c.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LinePricingTests"/> and the shared vectors cover the arithmetic. What is asserted here
/// is everything <see cref="PricingService"/> adds: that the right candidates are gathered for the
/// right outlet, that a set of lines costs the same as the lines would one at a time, that a missing
/// price is reported rather than invented, and that the totals are the sum of the rows above them.
/// </para>
/// <para>
/// The tenant-context harness this file once carried a third copy of now lives in
/// <see cref="AsTenant"/>. This note said the hoist belonged in its own PR; that PR happened.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class PricingServiceTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";
    private static readonly DateOnly Today = new(2026, 8, 12);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    /// <summary>Outlets and channels. This user holds no <c>product:*</c> — see realms/README.md.</summary>
    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    /// <summary>
    /// Products, price lists and promotions.
    /// </summary>
    /// <remarks>
    /// A second client rather than one, because the fixture's <c>admin</c> deliberately holds no
    /// <c>product:*</c> permission at all: that disjointness is how the realm demonstrates
    /// permission-based authorization rather than tiers. Setting this data up as <c>admin</c> answers 403,
    /// which is the realm working rather than the test being wrong.
    /// </remarks>
    private HttpClient Rep() => fixture.CreateAuthenticatedClient(fixture.AccessToken);

    [Fact]
    public async Task An_order_costs_what_its_lines_cost()
    {
        using var admin = Admin();
        using var rep = Rep();

        var shop = await ShopAsync(admin);
        var beer = await ProductAsync(rep);
        var crisps = await ProductAsync(rep);

        await PricedAsync(rep, shop, (beer, "4.50"), (crisps, "1.20"));

        var order = await PriceAsync(shop.OutletId, [new(beer, 6m), new(crisps, 10m)]);

        Assert.NotNull(order);
        Assert.Empty(order.Unpriced);
        Assert.Equal("EUR", order.CurrencyCode);

        // 6 × 4.50 = 27.00, 10 × 1.20 = 12.00.
        Assert.Equal(27.00m, order.Lines[0].Subtotal.Amount);
        Assert.Equal(12.00m, order.Lines[1].Subtotal.Amount);

        // The total is the sum of the rows, which is the property a document depends on.
        Assert.Equal(39.00m, order.Subtotal.Amount);
        Assert.Equal(39.00m, order.Total.Amount);
    }

    [Fact]
    public async Task A_product_with_no_price_is_reported_rather_than_charged_at_nothing()
    {
        /*
         * A missing price is a configuration gap — a product outside every list reaching this shop —
         * and the caller needs it apart from "the tenant charges nothing". Zero would pass the first
         * off as the second, and an order would be submitted for goods nobody priced.
         */
        using var admin = Admin();
        using var rep = Rep();

        var shop = await ShopAsync(admin);
        var priced = await ProductAsync(rep);
        var orphan = await ProductAsync(rep);

        await PricedAsync(rep, shop, (priced, "3.00"));

        var order = await PriceAsync(shop.OutletId, [new(priced, 2m), new(orphan, 5m)]);

        Assert.Equal(orphan, Assert.Single(order!.Unpriced));
        Assert.Equal(priced, Assert.Single(order.Lines).ProductId);
        Assert.Equal(6.00m, order.Total.Amount);
    }

    [Fact]
    public async Task An_order_of_nothing_but_unpriced_products_reports_no_currency()
    {
        // Rather than an empty string standing in for one, or a fabricated "EUR". No lines means no
        // money, and the ids are the whole answer.
        using var admin = Admin();
        using var rep = Rep();

        var shop = await ShopAsync(admin);
        var orphan = await ProductAsync(rep);

        var order = await PriceAsync(shop.OutletId, [new(orphan, 1m)]);

        Assert.Empty(order!.Lines);
        Assert.Equal(string.Empty, order.CurrencyCode);
        Assert.Equal(orphan, Assert.Single(order.Unpriced));
    }

    [Fact]
    public async Task An_outlet_this_tenant_does_not_have_is_null_rather_than_empty()
    {
        // "No such outlet" and "an outlet whose products are all unpriced" are different facts, and
        // a rep shown an empty total needs to know which one they are looking at.
        var order = await PriceAsync(Guid.CreateVersion7(), [new(Guid.CreateVersion7(), 1m)]);

        Assert.Null(order);
    }

    [Fact]
    public async Task A_promotion_reaching_the_outlet_is_applied_and_named()
    {
        /*
         * The gathering this service exists for: the promotion has to be *targeted* at the product
         * and *assigned* to the outlet before it counts. The id comes back so a rep told an
         * unexpected total can be answered without opening a database.
         */
        using var admin = Admin();
        using var rep = Rep();

        var shop = await ShopAsync(admin);
        var beer = await ProductAsync(rep);

        await PricedAsync(rep, shop, (beer, "10.00"));
        var promotionId = await PromotionAsync(rep, shop, beer, percentOff: "20.00");

        var order = await PriceAsync(shop.OutletId, [new(beer, 5m)]);

        var line = Assert.Single(order!.Lines);

        Assert.Equal(promotionId, line.PromotionId);
        Assert.Equal(50.00m, line.Subtotal.Amount);
        Assert.Equal(10.00m, line.Discount.Amount);
        Assert.Equal(40.00m, line.Net.Amount);
    }

    [Fact]
    public async Task A_promotion_that_does_not_reach_this_outlet_is_not_applied()
    {
        // Authored, targeted, in date — and assigned to nobody. The scope is what decides, and a
        // gathering that skipped the assignment join would price this line 20% lighter.
        using var admin = Admin();
        using var rep = Rep();

        var shop = await ShopAsync(admin);
        var beer = await ProductAsync(rep);

        await PricedAsync(rep, shop, (beer, "10.00"));
        await PromotionAsync(rep, shop, beer, percentOff: "20.00", assign: false);

        var line = Assert.Single((await PriceAsync(shop.OutletId, [new(beer, 5m)]))!.Lines);

        Assert.Null(line.PromotionId);
        Assert.Equal(50.00m, line.Net.Amount);
    }

    [Fact]
    public async Task A_fractional_quantity_reaches_a_tier_only_on_whole_units()
    {
        /*
         * The resolver takes an int and a line carries a decimal, so this service truncates. A tier
         * reading "buy 6 or more" is a promise about whole units taken, and 5.9 kg has not reached
         * six of anything — while rounding up would hand over a discount that then applies to the
         * *whole* line, so the error is not proportional to the rounding.
         */
        using var admin = Admin();
        using var rep = Rep();

        var shop = await ShopAsync(admin);
        var flour = await ProductAsync(rep);

        await PricedAsync(rep, shop, (flour, "2.00"));
        await PromotionAsync(rep, shop, flour, percentOff: "50.00", tierAt: 6);

        var under = Assert.Single((await PriceAsync(shop.OutletId, [new(flour, 5.9m)]))!.Lines);
        var over = Assert.Single((await PriceAsync(shop.OutletId, [new(flour, 6m)]))!.Lines);

        Assert.Null(under.PromotionId);
        Assert.Equal(promotionOf(over), over.PromotionId);
        Assert.Equal(6.00m, over.Discount.Amount);

        static Guid? promotionOf(PricedOrderLine line) => line.PromotionId;
    }

    [Fact]
    public async Task Pricing_the_same_line_twice_in_one_call_costs_the_same_both_times()
    {
        // The candidates are gathered once for a distinct set of products and then indexed per line.
        // A gathering keyed by position rather than by product would give the second line nothing.
        using var admin = Admin();
        using var rep = Rep();

        var shop = await ShopAsync(admin);
        var beer = await ProductAsync(rep);

        await PricedAsync(rep, shop, (beer, "4.00"));

        var order = await PriceAsync(shop.OutletId, [new(beer, 2m), new(beer, 3m)]);

        Assert.Equal(2, order!.Lines.Count);
        Assert.Equal(8.00m, order.Lines[0].Total.Amount);
        Assert.Equal(12.00m, order.Lines[1].Total.Amount);
        Assert.Equal(20.00m, order.Total.Amount);
    }

    private Task<PricedOrder?> PriceAsync(Guid outletId, IReadOnlyList<LineToPrice> lines) =>
        AsTenant.RunAsync(fixture, fixture.AdminAccessToken, services => services
            .GetRequiredService<IPricingService>()
            .PriceAsync(outletId, Today, lines));

    private sealed record Shop(Guid OutletId, Guid ChannelId);

    private static async Task<Shop> ShopAsync(HttpClient client)
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var created = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var outletId = (await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id;

        return new Shop(outletId, channelId);
    }

    private static async Task<Guid> ProductAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync(
            "/api/products", new { sku = Unique("SKU"), name = "A thing" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return (await created.Content.ReadFromJsonAsync<CreatedId>())!.Id;
    }

    /// <summary>A price list in EUR, reaching this shop's channel, with these prices on it.</summary>
    private static async Task PricedAsync(
        HttpClient client, Shop shop, params (Guid ProductId, string Amount)[] prices)
    {
        var list = await client.PostAsJsonAsync(
            "/api/products/price-lists",
            new CreatePriceListRequest(Unique("List"), "EUR", Today.AddDays(-30)));

        Assert.Equal(HttpStatusCode.Created, list.StatusCode);

        var listId = (await list.Content.ReadFromJsonAsync<CreatedId>())!.Id;

        var set = await client.PutAsJsonAsync(
            $"/api/products/price-lists/{listId}/prices",
            new SetPricesRequest([.. prices.Select(p => new PriceLineRequest(p.ProductId, p.Amount))]));

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        var assigned = await client.PutAsJsonAsync(
            $"/api/products/price-lists/{listId}/assignments",
            new SetAssignmentsRequest([shop.ChannelId], []));

        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
    }

    /// <summary>A promotion on one product, optionally tiered, optionally reaching the shop.</summary>
    private static async Task<Guid> PromotionAsync(
        HttpClient client,
        Shop shop,
        Guid productId,
        string percentOff,
        bool assign = true,
        int? tierAt = null)
    {
        var request = tierAt is null
            ? new CreatePromotionRequest(
                Unique("Promo"), PromotionType.PercentOff, Today.AddDays(-1), Value: percentOff)
            : new CreatePromotionRequest(
                Unique("Promo"), PromotionType.VolumeTiered, Today.AddDays(-1));

        var created = await client.PostAsJsonAsync("/api/products/promotions", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var promotionId = (await created.Content.ReadFromJsonAsync<CreatedId>())!.Id;

        if (tierAt is { } threshold)
        {
            var tiers = await client.PutAsJsonAsync(
                $"/api/products/promotions/{promotionId}/tiers",
                new SetPromotionTiersRequest([
                    new PromotionTierRequest(threshold, percentOff),
                ]));

            Assert.Equal(HttpStatusCode.OK, tiers.StatusCode);
        }

        var targets = await client.PutAsJsonAsync(
            $"/api/products/promotions/{promotionId}/targets",
            new SetPromotionTargetsRequest([productId], []));

        Assert.Equal(HttpStatusCode.OK, targets.StatusCode);

        if (assign)
        {
            var scope = await client.PutAsJsonAsync(
                $"/api/products/promotions/{promotionId}/assignments",
                new SetPromotionScopeRequest([shop.ChannelId], []));

            Assert.Equal(HttpStatusCode.OK, scope.StatusCode);
        }

        return promotionId;
    }

    private sealed record CreatedId(Guid Id);

}
