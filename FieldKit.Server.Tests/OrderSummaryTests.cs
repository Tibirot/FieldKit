using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Order;
using FieldKit.Modules.Order.Contracts;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Products;
using FieldKit.Modules.Visit;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// Order capture across a territory and a month (<c>ORD-09</c>) — W12 slice 2c.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two of this contract's numbers cannot be tested, and saying which is the point.</b> Nothing in
/// the system sets <c>Accepted</c> or <c>Cancelled</c> — the only transition the server has is
/// rejection — so there is no way to produce an order in either state to count. They are classified
/// by the summary anyway, because a state that arrives once W12 slice 6 builds the back office must
/// not fall silently out of both the value and the counts.
/// </para>
/// <para>
/// What is exercised is what a real tenant has today: submitted orders, rejected ones, more than one
/// currency, and the server's re-pricing verdict. The disagreement count is asserted against
/// <c>ForVisitAsync</c>'s own <c>Agreement</c> rather than against a number written here, because the
/// summary necessarily re-implements that rule in SQL — a computed property cannot cross into a
/// query — and this is what stops the two drifting apart.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class OrderSummaryTests(ServerFixture fixture)
{
    private const string Lists = "/api/products/price-lists";
    private const string Zone = "Europe/Bucharest";

    private static readonly Coordinates Shop = new(44.4638, 26.0946);

    private static readonly DateOnly JuneFirst = new(2026, 6, 1);
    private static readonly DateOnly JuneLast = new(2026, 6, 30);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    private HttpClient Rep() => fixture.CreateAuthenticatedClient();

    [Fact]
    public async Task Value_is_what_the_shop_agreed_to_pay_and_the_lines_behind_it()
    {
        using var client = Admin();

        var shop = await ShopAsync(client);

        await OrderAsync(shop, new DateOnly(2026, 6, 2), net: 27.00m, tax: 5.13m, lines: 2);
        await OrderAsync(shop, new DateOnly(2026, 6, 9), net: 13.50m, tax: 2.57m, lines: 1);

        var summary = await SummariseAsync([shop.OutletId]);

        Assert.Equal(2, summary.Orders);
        Assert.Equal(3, summary.Lines);
        Assert.Equal(1.5m, summary.LinesPerOrder);

        var value = Assert.Single(summary.Value);

        Assert.Equal("RON", value.CurrencyCode);
        Assert.Equal(40.50m, value.Net);
        Assert.Equal(7.70m, value.Tax);
        Assert.Equal(48.20m, value.Gross);
        Assert.Equal(2, value.Orders);
    }

    [Fact]
    public async Task A_rejected_order_is_counted_and_never_banked()
    {
        /*
         * The back office refused it, so it is not revenue — adding it to a territory's number would
         * report money somebody has already said no to. It is still counted, because a territory
         * writing off a tenth of its orders is a fact about that territory, and `BR-ORD-9`'s whole
         * re-open path exists to move that number.
         *
         * The order that stands is what makes the value non-zero, so this cannot pass by counting
         * nothing at all.
         */
        using var client = Admin();

        var shop = await ShopAsync(client);

        await OrderAsync(shop, new DateOnly(2026, 6, 3), net: 27.00m, tax: 5.13m, lines: 1);
        var doomed = await OrderAsync(shop, new DateOnly(2026, 6, 4), net: 90.00m, tax: 17.10m, lines: 3);

        var rejected = await client.PostAsJsonAsync(
            $"/api/orders/{doomed.OrderId}/rejection",
            new OrderRejectionRequest(OrderRejectionReason.OutletClosed, null, "Shut when we called."));

        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        var summary = await SummariseAsync([shop.OutletId]);

        Assert.Equal(1, summary.Orders);
        Assert.Equal(1, summary.Rejected);

        // The rejected order's three lines are not in the count either, and its 90.00 is not banked.
        Assert.Equal(1, summary.Lines);
        Assert.Equal(27.00m, Assert.Single(summary.Value).Net);
    }

    [Fact]
    public async Task Two_currencies_are_two_figures_rather_than_one_sum()
    {
        /*
         * Adding RON to EUR is not arithmetic, and a single `Total` over both would be a number with
         * no unit. The split is asserted rather than the sum — and the sum is asserted *not* to
         * appear, by checking each entry's own count, so a version that merged the two and labelled
         * the result with the first currency it saw would fail.
         */
        using var client = Admin();

        var romanian = await ShopAsync(client);
        var european = await ShopAsync(client, currency: "EUR");

        await OrderAsync(romanian, new DateOnly(2026, 6, 10), net: 20.00m, tax: 3.80m, lines: 1);
        await OrderAsync(european, new DateOnly(2026, 6, 11), net: 15.00m, tax: 2.85m, lines: 2);
        await OrderAsync(european, new DateOnly(2026, 6, 12), net: 5.00m, tax: 0.95m, lines: 1);

        var summary = await SummariseAsync([romanian.OutletId, european.OutletId]);

        Assert.Equal(3, summary.Orders);
        Assert.Equal(4, summary.Lines);

        // Ascending by code, so a reader can rely on the order.
        Assert.Equal(["EUR", "RON"], summary.Value.Select(row => row.CurrencyCode));

        var euros = summary.Value.Single(row => row.CurrencyCode == "EUR");
        var lei = summary.Value.Single(row => row.CurrencyCode == "RON");

        Assert.Equal(20.00m, euros.Net);
        Assert.Equal(2, euros.Orders);

        Assert.Equal(20.00m, lei.Net);
        Assert.Equal(1, lei.Orders);
    }

    [Fact]
    public async Task An_order_the_server_disputes_is_still_the_order()
    {
        /*
         * `BR-ORD-2`: the server re-prices and *flags*, never applies. The value reported is the
         * device's — what the rep and the shopkeeper settled at the counter — and reporting the
         * server's would report a figure nobody agreed to.
         *
         * Asserted against `ForVisitAsync`'s own `Agreement`, not against a hard-coded 1. The summary
         * re-implements that rule in SQL because a computed property cannot cross into a query, and
         * comparing the two is the only thing that keeps the second implementation honest.
         */
        using var client = Admin();

        var shop = await ShopAsync(client, unitPrice: "10.00");

        // The device says 17.50 for two units the server prices at 10.00 each.
        var disputed = await OrderAsync(
            shop, new DateOnly(2026, 6, 17), net: 17.50m, tax: 3.33m, lines: 1, quantity: 2m);

        var agreed = await OrderAsync(
            shop, new DateOnly(2026, 6, 18), net: 20.00m, tax: 0m, lines: 1, quantity: 2m);

        var verdicts = await Task.WhenAll(
            AgreementAsync(disputed.VisitId), AgreementAsync(agreed.VisitId));

        // The fixture has to actually produce one of each, or the assertion below is vacuous.
        Assert.Equal(PriceAgreement.Differs, verdicts[0]);
        Assert.Equal(PriceAgreement.Agrees, verdicts[1]);

        var summary = await SummariseAsync([shop.OutletId]);

        Assert.Equal(verdicts.Count(verdict => verdict == PriceAgreement.Differs), summary.PriceDisagreements);

        // …and the disputed order's own numbers are what got banked.
        Assert.Equal(37.50m, Assert.Single(summary.Value).Net);
    }

    [Fact]
    public async Task It_answers_about_the_shops_and_the_days_it_was_asked_about()
    {
        // Scope and window, each with a mirror on the other side of the line so that neither can
        // pass against an empty set.
        using var client = Admin();

        var shop = await ShopAsync(client);
        var elsewhere = await ShopAsync(client);

        await OrderAsync(shop, JuneFirst, net: 10.00m, tax: 0m, lines: 1);
        await OrderAsync(shop, JuneLast, net: 10.00m, tax: 0m, lines: 1);
        await OrderAsync(shop, JuneFirst.AddDays(-1), net: 99.00m, tax: 0m, lines: 1);
        await OrderAsync(shop, JuneLast.AddDays(1), net: 99.00m, tax: 0m, lines: 1);
        await OrderAsync(elsewhere, new DateOnly(2026, 6, 15), net: 99.00m, tax: 0m, lines: 1);

        var june = await SummariseAsync([shop.OutletId]);

        Assert.Equal(2, june.Orders);
        Assert.Equal(20.00m, Assert.Single(june.Value).Net);

        var wider = await SummariseAsync(
            [shop.OutletId], JuneFirst.AddDays(-1), JuneLast.AddDays(1));

        Assert.Equal(4, wider.Orders);

        // The other shop's order is real and simply not this question's.
        Assert.Equal(1, (await SummariseAsync([elsewhere.OutletId])).Orders);

        var nothing = await SummariseAsync([]);

        Assert.Equal(0, nothing.Orders);
        Assert.Empty(nothing.Value);
        Assert.Null(nothing.LinesPerOrder);
    }

    private Task<OrderSummary> SummariseAsync(
        IReadOnlyCollection<Guid> outletIds, DateOnly? from = null, DateOnly? to = null) =>
        AsTenant.RunAsync(fixture, fixture.AdminAccessToken, services => services
            .GetRequiredService<IOrderQuery>()
            .SummariseAsync(outletIds, from ?? JuneFirst, to ?? JuneLast));

    private async Task<PriceAgreement> AgreementAsync(Guid visitId)
    {
        var order = await AsTenant.RunAsync(fixture, fixture.AdminAccessToken, services => services
            .GetRequiredService<IOrderQuery>()
            .ForVisitAsync(visitId));

        return order!.Agreement;
    }

    /// <summary>
    /// A shop, the products it stocks, and the currency its price list is in.
    /// </summary>
    /// <remarks>
    /// <b>Three products rather than one</b>, because <c>OrderRefusal.DuplicateProduct</c> refuses two
    /// lines for the same SKU — two answers to "how many", with no rule picking one. A multi-line
    /// order therefore needs a line per product, which the first version of this fixture did not
    /// have: every order came back <c>Invalid</c>.
    /// </remarks>
    private sealed record Shopfront(Guid OutletId, IReadOnlyList<Guid> ProductIds, string Currency);

    /// <summary>An order that was taken: its own id, and the visit it was taken during.</summary>
    /// <remarks>
    /// Both, because the two things this file does with an order need different handles — rejection
    /// is addressed by <b>order</b> id (<c>POST /api/orders/{id}/rejection</c>) and reading one back
    /// through <c>IOrderQuery</c> is addressed by <b>visit</b>.
    /// </remarks>
    private sealed record Taken(Guid OrderId, Guid VisitId);

    /// <summary>Takes an order at <paramref name="shop"/> on <paramref name="on"/>.</summary>
    private async Task<Taken> OrderAsync(
        Shopfront shop, DateOnly on, decimal net, decimal tax, int lines, decimal quantity = 6m)
    {
        var visitId = await VisitAsync(shop.OutletId);

        // The net is split evenly across the lines, so `Lines` counts something the caller chose
        // while `Net` stays the number the assertion names.
        var perLine = Math.Round(net / lines, 2, MidpointRounding.AwayFromZero);

        var captured = new CapturedOrder(
            Guid.CreateVersion7(),
            visitId,
            shop.Currency,
            net,
            new DateTimeOffset(on.ToDateTime(new TimeOnly(9, 30)), TimeSpan.Zero),
            [.. Enumerable.Range(0, lines).Select(index => new CapturedOrderLine(
                shop.ProductIds[index],
                quantity,
                "case",
                12,
                4.50m,

                // The last line carries the rounding remainder, so the lines add to the net.
                index == lines - 1 ? net - (perLine * (lines - 1)) : perLine))],
            TaxTotal: tax);

        var result = await AsTenant.RunAsync(fixture, fixture.AdminAccessToken, services => services
            .GetRequiredService<IOrderIngest>()
            .IngestAsync(captured, Guid.CreateVersion7(), AsTenant.SubjectOf(fixture.AdminAccessToken)));

        Assert.Equal(OrderIngestRefusal.None, result.Refusal);

        return new Taken(captured.OrderId, visitId);
    }

    /// <summary>A shop stocking one priced product, in one currency.</summary>
    private async Task<Shopfront> ShopAsync(
        HttpClient client, string currency = "RON", string unitPrice = "10.00")
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));
        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var created = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, Location: Shop));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var outletId = (await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id;

        // A separate client, because the realm deliberately gives `admin` no `product:*`.
        using var writer = Rep();

        var productIds = new List<Guid>();

        for (var index = 0; index < 3; index++)
        {
            var product = await writer.PostAsJsonAsync(
                "/api/products", new { sku = Unique("SKU"), name = "Cola 500ml" });

            Assert.Equal(HttpStatusCode.Created, product.StatusCode);

            productIds.Add((await product.Content.ReadFromJsonAsync<ProductResponse>())!.Id);
        }

        var assorted = await writer.PutAsJsonAsync(
            $"/api/products/assortments/channels/{channelId}",
            new SetAssortmentRequest([.. productIds.Select(id => new AssortmentLineRequest(id))]));

        Assert.Equal(HttpStatusCode.OK, assorted.StatusCode);

        var list = await writer.PostAsJsonAsync(
            Lists, new CreatePriceListRequest(Unique("List"), currency, JuneFirst.AddDays(-60), null));

        Assert.Equal(HttpStatusCode.Created, list.StatusCode);

        var listId = (await list.Content.ReadFromJsonAsync<PriceListResponse>())!.Id;

        await writer.PutAsJsonAsync(
            $"{Lists}/{listId}/prices",
            new SetPricesRequest([.. productIds.Select(id => new PriceLineRequest(id, unitPrice))]));

        await writer.PutAsJsonAsync(
            $"{Lists}/{listId}/assignments", new SetAssignmentsRequest([], [outletId]));

        return new Shopfront(outletId, productIds, currency);
    }

    private async Task<Guid> VisitAsync(Guid outletId)
    {
        using var client = Admin();

        var response = await client.PostAsJsonAsync(
            "/api/visits/check-in", new CheckInRequest(outletId, Shop.Latitude, Shop.Longitude));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit.Id;
    }
}
