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
/// The queue a supervisor works through (<c>ORD-09</c>) — W12 slice 6a.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded from the first line, which is the lesson W12 slice 5a paid for.</b> That slice found
/// <c>GET /api/visits</c> unbounded because its only caller always passed a filter — the tenant-wide
/// question was never asked until a back-office screen asked it. This read exists <i>for</i> the
/// tenant-wide question, so the ceiling is not an afterthought.
/// </para>
/// <para>
/// The status filter is the other half. A queue whose default hid rejected orders would turn "where
/// did that order go" into a support question, so the read takes a filter and the screen chooses.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class OrderQueueTests(ServerFixture fixture)
{
    private const string Lists = "/api/products/price-lists";
    private const string Zone = "Europe/Bucharest";

    private static readonly Coordinates Shop = new(44.4638, 26.0946);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    private HttpClient Rep() => fixture.CreateAuthenticatedClient();

    [Fact]
    public async Task The_queue_is_newest_first_and_can_be_narrowed_to_what_still_needs_working()
    {
        /*
         * Three orders at one shop, one of them rejected. The unfiltered queue holds all three in
         * capture order; asking for `Submitted` holds the two that still need a decision, and asking
         * for `Rejected` holds the one that had one.
         *
         * The rejected order is *named* in both assertions rather than merely counted, so a filter
         * that returned an arbitrary subset of the right size would fail.
         */
        using var client = Admin();

        var shop = await ShopAsync(client);

        var oldest = await OrderAsync(shop, new DateOnly(2026, 10, 5));
        var middle = await OrderAsync(shop, new DateOnly(2026, 10, 12));
        var newest = await OrderAsync(shop, new DateOnly(2026, 10, 19));

        var rejected = await client.PostAsJsonAsync(
            $"/api/orders/{middle}/rejection",
            new OrderRejectionRequest(OrderRejectionReason.OutletClosed, null, "Shut when we called."));

        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        var mine = new[] { oldest, middle, newest };

        var everything = await QueueAsync(client, status: null);
        var ordered = everything.Where(order => mine.Contains(order.Id)).ToList();

        Assert.Equal([newest, middle, oldest], ordered.Select(order => order.Id));

        var submitted = await QueueAsync(client, status: "Submitted");

        Assert.Contains(submitted, order => order.Id == newest);
        Assert.Contains(submitted, order => order.Id == oldest);
        Assert.DoesNotContain(submitted, order => order.Id == middle);

        var refused = await QueueAsync(client, status: "Rejected");

        Assert.Contains(refused, order => order.Id == middle);
        Assert.DoesNotContain(refused, order => order.Id == newest);

        // The rejection travels with the order, so a supervisor reading the queue sees why without
        // opening anything.
        var theRejected = refused.Single(order => order.Id == middle);

        Assert.Equal("OutletClosed", theRejected.Rejection!.Reason);
    }

    [Fact]
    public async Task The_limit_is_clamped_at_both_ends_rather_than_trusted()
    {
        /*
         * <b>Asserted from the floor, because the ceiling cannot be shown cheaply and pretending
         * otherwise is how a test becomes decoration.</b>
         *
         * The obvious test — ask for ten thousand, expect no more than two hundred — passes on any
         * tenant holding fewer than two hundred orders, which is every tenant this suite builds.
         * I wrote it that way first and the sabotage proved it: deleting the clamp entirely left it
         * green. Seeding past the ceiling would mean two hundred visits *and* two hundred orders per
         * run, which is a slow test that slows every other one.
         *
         * `Math.Clamp(0, 1, 200)` is 1, so a caller asking for none gets one — observable with three
         * orders, and it fails the moment the clamp is replaced by a bare `Take(limit)`, which
         * returns nothing. Same expression, both ends; proving one proves the call is being made.
         */
        using var client = Admin();

        var shop = await ShopAsync(client);

        await OrderAsync(shop, new DateOnly(2026, 11, 2));
        await OrderAsync(shop, new DateOnly(2026, 11, 9));

        var none = await client.GetFromJsonAsync<List<OrderReadback>>("/api/orders?limit=0");

        Assert.Single(none!);

        var one = await client.GetFromJsonAsync<List<OrderReadback>>("/api/orders?limit=1");

        Assert.Single(one!);

        // And a limit inside the range is honoured rather than ignored, so the clamp is not simply
        // pinning everything to one.
        var two = await client.GetFromJsonAsync<List<OrderReadback>>("/api/orders?limit=2");

        Assert.Equal(2, two!.Count);

        // The ceiling itself, asserted for what it is worth: it holds, and on this tenant it is not
        // the binding constraint. Kept because it would catch a clamp inverted to a floor.
        var everything = await client.GetFromJsonAsync<List<OrderReadback>>("/api/orders?limit=10000");

        Assert.True(
            everything!.Count <= Ceiling,
            $"{everything.Count} orders came back, above the ceiling of {Ceiling}");

        Assert.Equal(
            everything.Select(order => order.CapturedAtUtc).OrderByDescending(at => at),
            everything.Select(order => order.CapturedAtUtc));
    }

    [Fact]
    public async Task Reading_the_queue_needs_permission()
    {
        // Every order the tenant has taken, with totals. `rep` holds no `visit:read` — orders are
        // read under it, borrowed from Visit, and `OrderEndpoints` says why.
        using var rep = Rep();

        Assert.Equal(HttpStatusCode.Forbidden, (await rep.GetAsync("/api/orders")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("/api/orders")).StatusCode);
    }

    /// <summary>
    /// Mirrors <c>OrderQueryService.MaximumRecent</c>, which is internal and stays that way.
    /// </summary>
    /// <remarks>
    /// The same trade-off <c>VisitListTests</c> records: this project has no
    /// <c>InternalsVisibleTo</c>, so the number is duplicated and the assertions are written about
    /// the <i>property</i> — bounded, and the newest — rather than about the value.
    /// </remarks>
    private const int Ceiling = 200;

    private async Task<List<OrderReadback>> QueueAsync(HttpClient client, string? status)
    {
        var query = status is null ? string.Empty : $"?status={status}";
        var response = await client.GetAsync($"/api/orders{query}");

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<List<OrderReadback>>())!;
    }

    private sealed record RejectionReadback(string Reason, Guid? OffendingProductId, string? Note);

    private sealed record OrderReadback(
        Guid Id,
        Guid VisitId,
        Guid OutletId,
        string Status,
        string CurrencyCode,
        decimal Total,
        DateTimeOffset CapturedAtUtc,
        RejectionReadback? Rejection);

    private sealed record Shopfront(Guid OutletId, IReadOnlyList<Guid> ProductIds);

    /// <summary>Takes an order on <paramref name="on"/> and returns its id.</summary>
    private async Task<Guid> OrderAsync(Shopfront shop, DateOnly on)
    {
        var visitId = await VisitAsync(shop.OutletId);

        var captured = new CapturedOrder(
            Guid.CreateVersion7(),
            visitId,
            "RON",
            27.00m,
            new DateTimeOffset(on.ToDateTime(new TimeOnly(9, 30)), TimeSpan.Zero),
            [new CapturedOrderLine(shop.ProductIds[0], 6m, "case", 12, 4.50m, 27.00m)]);

        var result = await AsTenant.RunAsync(fixture, fixture.AdminAccessToken, services => services
            .GetRequiredService<IOrderIngest>()
            .IngestAsync(captured, Guid.CreateVersion7(), AsTenant.SubjectOf(fixture.AdminAccessToken)));

        Assert.Equal(OrderIngestRefusal.None, result.Refusal);

        return captured.OrderId;
    }

    private async Task<Shopfront> ShopAsync(HttpClient client)
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));
        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var created = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, Location: Shop));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var outletId = (await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id;

        using var writer = Rep();

        var product = await writer.PostAsJsonAsync(
            "/api/products", new { sku = Unique("SKU"), name = "Cola 500ml" });

        Assert.Equal(HttpStatusCode.Created, product.StatusCode);

        var productId = (await product.Content.ReadFromJsonAsync<ProductResponse>())!.Id;

        var assorted = await writer.PutAsJsonAsync(
            $"/api/products/assortments/channels/{channelId}",
            new SetAssortmentRequest([new AssortmentLineRequest(productId)]));

        Assert.Equal(HttpStatusCode.OK, assorted.StatusCode);

        var list = await writer.PostAsJsonAsync(
            Lists, new CreatePriceListRequest(Unique("List"), "RON", new DateOnly(2026, 1, 1), null));

        Assert.Equal(HttpStatusCode.Created, list.StatusCode);

        var listId = (await list.Content.ReadFromJsonAsync<PriceListResponse>())!.Id;

        await writer.PutAsJsonAsync(
            $"{Lists}/{listId}/prices", new SetPricesRequest([new PriceLineRequest(productId, "4.50")]));

        await writer.PutAsJsonAsync(
            $"{Lists}/{listId}/assignments", new SetAssignmentsRequest([], [outletId]));

        return new Shopfront(outletId, [productId]);
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
