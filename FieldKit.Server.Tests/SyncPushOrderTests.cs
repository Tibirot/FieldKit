using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Order;
using FieldKit.Modules.Order.Contracts;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Products;
using FieldKit.Modules.Sync;
using FieldKit.Modules.Visit;

namespace FieldKit.Server.Tests;

/// <summary>
/// An order drained from a device (<c>OFF-04</c>, <c>ORD-07</c>) — W11 slice 5.
/// </summary>
/// <remarks>
/// <para>
/// The slice where W11's first four meet: the aggregate from slice 1, the lock from slice 3, the
/// rejection and re-open from 4a, and the assortment gate from 4b — all reached through the door a
/// device actually uses. Until now every one of those was tested by resolving <c>IOrderIngest</c>
/// from the container, which proves the rules and not the routing.
/// </para>
/// <para>
/// <b>The mutation id is what makes this arm different.</b> Every other kind of mutation is
/// idempotent on its own subject, so a repeat is recognisable from the payload; an order is not,
/// because the same order id arrives both when a device retries and when a rep corrects a rejection
/// (<c>BR-ORD-9</c>). Sync's id is therefore handed to Order, which records it — and these are the
/// tests that say the two agree.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class SyncPushOrderTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    private static readonly Coordinates Shop = new(44.4638, 26.0946);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private HttpClient Rep() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    [Fact]
    public async Task An_order_pushed_from_a_device_is_stored_and_readable()
    {
        using var rep = Rep();

        var (visitId, assorted) = await ShopfrontAsync(rep);
        var device = await BindDeviceAsync(rep);
        var captured = Captured(visitId, Line(assorted[0]));

        var push = await PushAsync(rep, device, Order(captured));

        Assert.Equal("accepted", Assert.Single(push.Results).Status);

        var stored = await rep.GetFromJsonAsync<OrderReadback>($"/api/orders/by-visit/{visitId}");

        Assert.Equal(captured.OrderId, stored!.Id);
        Assert.Equal("Submitted", stored.Status);
    }

    [Fact]
    public async Task One_refused_order_does_not_take_the_batch_with_it()
    {
        /*
         * The shape `/sync/push` exists for. A rep offline for a day drains everything at once, and a
         * single bad mutation must not cost them the rest — so every mutation gets its own result and
         * the batch never fails as a unit.
         *
         * Ordered deliberately with the refusal *first*: a batch that stopped at the first problem
         * would still pass if the good one came before it.
         */
        using var rep = Rep();

        var (visitId, assorted) = await ShopfrontAsync(rep);
        var device = await BindDeviceAsync(rep);

        var push = await PushAsync(
            rep,
            device,
            Order(Captured(Guid.CreateVersion7(), Line(assorted[0]))),
            Order(Captured(visitId, Line(assorted[0]))));

        Assert.Equal(2, push.Results.Count);
        Assert.Equal("rejected", push.Results[0].Status);
        Assert.Equal("order.ingest.visitUnknown", push.Results[0].Reason);
        Assert.Equal("accepted", push.Results[1].Status);

        // …and the good one is genuinely stored, not merely answered for.
        var stored = await rep.GetFromJsonAsync<OrderReadback>($"/api/orders/by-visit/{visitId}");

        Assert.Equal("Submitted", stored!.Status);
    }

    [Fact]
    public async Task A_retried_push_is_answered_from_the_ledger_rather_than_re_applied()
    {
        // Exactly-once effect over at-least-once delivery. The same mutation id twice: the second
        // answer comes from Sync's ledger without Order being asked again.
        using var rep = Rep();

        var (visitId, assorted) = await ShopfrontAsync(rep);
        var device = await BindDeviceAsync(rep);

        var mutation = Order(Captured(visitId, Line(assorted[0])));

        Assert.Equal("accepted", Assert.Single((await PushAsync(rep, device, mutation)).Results).Status);
        Assert.Equal("accepted", Assert.Single((await PushAsync(rep, device, mutation)).Results).Status);

        var stored = await rep.GetFromJsonAsync<OrderReadback>($"/api/orders/by-visit/{visitId}");

        Assert.Single(stored!.Lines);
    }

    [Fact]
    public async Task An_edit_after_submit_is_rejected_by_name()
    {
        /*
         * `BR-ORD-4` reaching the device as a code it can branch on. A *different* mutation carrying
         * the same order id is an edit after submit, and the device stops retrying: nothing it can do
         * makes one legal, and the documented way back is a rejection it has not been given.
         */
        using var rep = Rep();

        var (visitId, assorted) = await ShopfrontAsync(rep);
        var device = await BindDeviceAsync(rep);
        var captured = Captured(visitId, Line(assorted[0]));

        Assert.Equal("accepted", Assert.Single((await PushAsync(rep, device, Order(captured))).Results).Status);

        var edited = captured with { Lines = [Line(assorted[1], quantity: 99m)] };

        var second = Assert.Single((await PushAsync(rep, device, Order(edited))).Results);

        Assert.Equal("rejected", second.Status);
        Assert.Equal("order.ingest.alreadySubmitted", second.Reason);
    }

    [Fact]
    public async Task An_order_the_shop_may_not_buy_is_accepted_by_the_push_and_rejected_by_the_rule()
    {
        /*
         * The distinction W11 slice 4b turns on, seen from the wire. `BR-ORD-1` is not a transport
         * failure: the push applied, so it answers `accepted`, and the order itself comes back
         * `Rejected` with the offending line named. A device told "rejected" here would conclude the
         * mutation never landed and retry it forever against an order that already exists.
         */
        using var rep = Rep();

        var (visitId, _) = await ShopfrontAsync(rep);
        var device = await BindDeviceAsync(rep);

        var notStocked = Guid.CreateVersion7();
        var captured = Captured(visitId, Line(notStocked));

        var push = await PushAsync(rep, device, Order(captured));

        Assert.Equal("accepted", Assert.Single(push.Results).Status);

        var stored = await rep.GetFromJsonAsync<OrderReadback>($"/api/orders/by-visit/{visitId}");

        Assert.Equal("Rejected", stored!.Status);
        Assert.Equal(notStocked, stored.Rejection!.OffendingProductId);
    }

    [Fact]
    public async Task An_order_naming_somebody_elses_visit_is_rejected_by_name()
    {
        using var rep = Rep();

        var device = await BindDeviceAsync(rep);

        var result = Assert.Single(
            (await PushAsync(rep, device, Order(Captured(Guid.CreateVersion7(), Line())))).Results);

        Assert.Equal("rejected", result.Status);
        Assert.Equal("order.ingest.visitUnknown", result.Reason);
    }

    private static PushedMutation Order(CapturedOrder order) =>
        new(Guid.CreateVersion7(), nameof(CapturedOrder), Order: order);

    private static CapturedOrderLine Line(Guid? productId = null, decimal quantity = 6m) =>
        new(productId ?? Guid.CreateVersion7(), quantity, "case", 12, 4.50m, 27.00m);

    private static CapturedOrder Captured(Guid visitId, params CapturedOrderLine[] lines) => new(
        Guid.CreateVersion7(),
        visitId,
        "EUR",
        lines.Sum(line => line.LineTotal),
        DateTimeOffset.Parse("2026-08-12T09:45:00Z"),
        lines);

    private static async Task<PushResponse> PushAsync(
        HttpClient client, Guid deviceId, params PushedMutation[] mutations)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/push", new PushRequest(deviceId, mutations));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<PushResponse>())!;
    }

    private static async Task<Guid> BindDeviceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/devices", new BindDeviceRequest(Unique("Phone")));

        return (await response.Content.ReadFromJsonAsync<DeviceResponse>())!.Id;
    }

    /// <summary>A visit at a shop that stocks the returned products (<c>BR-ORD-1</c>).</summary>
    private async Task<(Guid VisitId, IReadOnlyList<Guid> Assorted)> ShopfrontAsync(HttpClient client)
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var created = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, Location: Shop));

        var outletId = (await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id;

        // `admin` holds no `product:*` — writing the catalogue is a different job from administering
        // the tenant, and the realm says so deliberately.
        using var writer = fixture.CreateAuthenticatedClient();

        var products = new List<Guid>();

        for (var i = 0; i < 2; i++)
        {
            var product = await writer.PostAsJsonAsync(
                "/api/products", new CreateProductRequest(Unique("SKU"), "Veridian Still"));

            products.Add((await product.Content.ReadFromJsonAsync<ProductResponse>())!.Id);
        }

        var assorted = await writer.PutAsJsonAsync(
            $"/api/products/assortments/channels/{channelId}",
            new SetAssortmentRequest([.. products.Select(id => new AssortmentLineRequest(id))]));

        Assert.Equal(HttpStatusCode.OK, assorted.StatusCode);

        var checkIn = await client.PostAsJsonAsync(
            "/api/visits/check-in", new CheckInRequest(outletId, Shop.Latitude, Shop.Longitude));

        Assert.Equal(HttpStatusCode.Created, checkIn.StatusCode);

        var visit = (await checkIn.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;

        return (visit.Id, products);
    }

    private sealed record RejectionReadback(string Reason, Guid? OffendingProductId, string? Note);

    private sealed record OrderLineReadback(string UnitOfMeasure, decimal Quantity);

    private sealed record OrderReadback(
        Guid Id,
        string Status,
        IReadOnlyList<OrderLineReadback> Lines,
        RejectionReadback? Rejection);
}
