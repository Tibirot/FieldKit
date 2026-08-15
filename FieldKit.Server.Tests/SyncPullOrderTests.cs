using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.Modules.Order;
using FieldKit.Modules.Order.Contracts;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Products;
using FieldKit.Modules.Sync;
using FieldKit.Modules.Visit;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// What the back office made of a rep's orders, on the pull feed (<c>BR-ORD-9</c>) — W12 F5a.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first entity on this feed that is not reference data.</b> Every other one is a copy of
/// something the server owns; an order is the device's own work, and what travels back down is an
/// annotation on it. That is the whole of regression F5: <c>POST /api/orders/{id}/rejection</c>,
/// <c>Order.Resubmit</c> and the terminal-mutation rule were all built in W11 slice 4a, and no rep
/// could begin `BR-ORD-9`'s correction because none could learn their order had been rejected.
/// </para>
/// <para>
/// Rejections are raised by the administrator here because there is still no back-office screen —
/// `ORD-09` is Phase 4. That is what made this finding *bounded* rather than urgent, and it is not
/// what made it wrong.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class SyncPullOrderTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    private static readonly Coordinates Shop = new(44.4638, 26.0946);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    private HttpClient Rep() => fixture.CreateAuthenticatedClient();

    [Fact]
    public async Task A_rejected_order_reaches_the_rep_who_took_it()
    {
        /*
         * <b>The finding, closed.</b> The rep pushes, an operator refuses it, and the next pull
         * carries the verdict — which is the first half of `BR-ORD-9` and the half that had no way
         * to happen.
         *
         * Two pulls, as every test in `SyncPullJourneyTests` does: a first that returns something
         * proves almost nothing on its own, since a feed that re-sent everything forever would pass
         * it and would cost a rep their data allowance on every reconnect.
         */
        using var admin = Admin();

        var (visitId, products) = await ShopAsync(admin);
        var orderId = await SubmitAsync(visitId, products[0]);

        var device = await BindDeviceAsync(admin);
        var before = await PullAsync(admin, device);

        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(admin, orderId, products[0])).StatusCode);

        var after = await PullAsync(admin, device, Cursor(before));
        var verdict = Assert.Single(Verdicts(after), sent => Id(sent) == orderId);

        Assert.Equal("Rejected", verdict.GetProperty("status").GetString());

        // The reason and the line, together. A status with nothing beside it is what `OFF-09` and
        // W11½ R5 exist to stop — a rep told *needs attention* and given no handle on it.
        var rejection = verdict.GetProperty("rejection");

        Assert.Equal("OffAssortment", rejection.GetProperty("reason").GetString());
        Assert.Equal(products[0], rejection.GetProperty("offendingProductId").GetGuid());

        // And nothing has changed since, so the device is told nothing and stays where it is.
        var third = await PullAsync(admin, device, Cursor(after));

        Assert.DoesNotContain(Verdicts(third), sent => Id(sent) == orderId);
        Assert.Equal(Cursor(after), Cursor(third));
    }

    [Fact]
    public async Task A_correction_comes_back_as_submitted_with_the_rejection_gone()
    {
        /*
         * <b>The second half of `BR-ORD-9`, and the reason this feed carries good news too.</b>
         *
         * The rep fixes the flagged line and pushes again; the order keeps its identity and returns
         * to `Submitted`. A feed that sent only rejections could never say so, and the device would
         * show *refused* against an order the back office has since accepted — for the life of the
         * install, because a delta only ever carries what changed.
         */
        using var admin = Admin();

        var (visitId, products) = await ShopAsync(admin);
        var orderId = await SubmitAsync(visitId, products[0]);

        await RejectAsync(admin, orderId, products[0]);

        var device = await BindDeviceAsync(admin);
        var rejected = await PullAsync(admin, device);

        Assert.Equal(
            "Rejected",
            Assert.Single(Verdicts(rejected), sent => Id(sent) == orderId)
                .GetProperty("status").GetString());

        // The correction: a different line, under a new mutation id, on the same order.
        await SubmitAsync(visitId, products[1], orderId);

        var corrected = Assert.Single(
            Verdicts(await PullAsync(admin, device, Cursor(rejected))), sent => Id(sent) == orderId);

        Assert.Equal("Submitted", corrected.GetProperty("status").GetString());

        // Null rather than absent, and the device reads it as "no longer current" rather than "not
        // sent this time" — a delta has no way to express a field it has stopped having an opinion
        // about, so the opinion has to be an explicit nothing.
        Assert.Equal(JsonValueKind.Null, corrected.GetProperty("rejection").ValueKind);
    }

    [Fact]
    public async Task Another_reps_order_stays_off_this_device()
    {
        /*
         * An order belongs to the person who took it and never changes hands, which is what makes a
         * cursor sufficient here — and also what a modified client must not be able to turn into an
         * oracle for somebody else's day. The same rule `IJourneyQuery` follows.
         *
         * <b>Asked of the feed rather than through a second device</b>, and the fixture is the
         * reason. `IOrderIngest` refuses an order for a visit the pushing subject does not own
         * (`UnknownVisit`), and in this realm the administrator is the only subject who may check
         * in — so there is no way to give a *second* person an order to be excluded from. Driving
         * it over HTTP would test the fixture's permissions, not the scope.
         *
         * So this asks both directions of the one question the clause decides: the owner sees it,
         * and somebody else does not.
         */
        using var admin = Admin();

        var (visitId, products) = await ShopAsync(admin);
        var orderId = await SubmitAsync(visitId, products[0]);

        await RejectAsync(admin, orderId, products[0]);

        var owner = AsTenant.SubjectOf(fixture.AdminAccessToken);
        var somebodyElse = AsTenant.SubjectOf(fixture.AccessToken);

        var (mine, theirs) = await AsTenant.RunAsync(fixture, fixture.AdminAccessToken, async services =>
        {
            var feed = services.GetRequiredService<IOrderVerdictFeed>();

            return (
                await feed.GetChangesAsync(0, owner, 200),
                await feed.GetChangesAsync(0, somebodyElse, 200));
        });

        // Both halves. The exclusion alone would be satisfied by a feed that returns nothing at all,
        // which is exactly how a scope check goes green against a broken query.
        Assert.Contains(mine.Upserts, verdict => verdict.OrderId == orderId);
        Assert.DoesNotContain(theirs.Upserts, verdict => verdict.OrderId == orderId);
    }

    [Fact]
    public async Task The_wire_carries_no_money_at_all()
    {
        /*
         * <b>The design decision, asserted rather than described.</b> `BR-ORD-6` puts the device's
         * totals beyond the server's reach — they are what the rep and the shopkeeper agreed, and
         * the server's arithmetic is an annotation beside them, never over them.
         *
         * A snapshot carrying `total` would put that number on the wire pointed at a store that
         * already holds a different copy of it, with no type saying which wins. This asserts the
         * shape of the payload, because the property is *absence* and absence is what a reviewer
         * cannot see.
         */
        using var admin = Admin();

        var (visitId, products) = await ShopAsync(admin);
        var orderId = await SubmitAsync(visitId, products[0]);

        await RejectAsync(admin, orderId, products[0]);

        var device = await BindDeviceAsync(admin);
        var verdict = Assert.Single(
            Verdicts(await PullAsync(admin, device)), sent => Id(sent) == orderId);

        Assert.Equal(
            ["orderId", "status", "rejection", "rowVersion"],
            verdict.EnumerateObject().Select(field => field.Name).ToArray());
    }

    private static Guid Id(JsonElement verdict) => verdict.GetProperty("orderId").GetGuid();

    private static JsonElement Orders(JsonElement pull) =>
        pull.GetProperty("changes").GetProperty("orders");

    private static long Cursor(JsonElement pull) => Orders(pull).GetProperty("cursor").GetInt64();

    private static List<JsonElement> Verdicts(JsonElement pull) =>
        [.. Orders(pull).GetProperty("upserts").EnumerateArray()];

    private static async Task<Guid> BindDeviceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/devices", new BindDeviceRequest(Unique("Phone")));

        return (await response.Content.ReadFromJsonAsync<DeviceResponse>())!.Id;
    }

    private static async Task<JsonElement> PullAsync(
        HttpClient client, Guid deviceId, long? orders = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/pull", new PullRequest(deviceId, new PullCursors(null, Orders: orders)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private Task<HttpResponseMessage> RejectAsync(HttpClient admin, Guid orderId, Guid offending) =>
        admin.PostAsJsonAsync(
            $"/api/orders/{orderId}/rejection",
            new OrderRejectionRequest(OrderRejectionReason.OffAssortment, offending));

    /// <summary>Captures an order, or corrects one when <paramref name="orderId"/> names an existing one.</summary>
    private async Task<Guid> SubmitAsync(
        Guid visitId, Guid productId, Guid? orderId = null, string? userId = null)
    {
        var captured = new CapturedOrder(
            orderId ?? Guid.CreateVersion7(),
            visitId,
            "RON",
            20.00m,
            new DateTimeOffset(2026, 4, 6, 9, 45, 0, TimeSpan.Zero),
            [new CapturedOrderLine(productId, 2m, "unit", null, 10.00m, 20.00m, 0m)],
            0m,
            null);

        var result = await AsTenant.RunAsync(fixture, fixture.AdminAccessToken, async services =>
            await services.GetRequiredService<IOrderIngest>().IngestAsync(
                captured,
                Guid.CreateVersion7(),
                userId ?? AsTenant.SubjectOf(fixture.AdminAccessToken)));

        Assert.Equal(OrderIngestRefusal.None, result.Refusal);

        return captured.OrderId;
    }

    /// <summary>A shop that stocks three products, and a visit at it.</summary>
    private async Task<(Guid VisitId, IReadOnlyList<Guid> Products)> ShopAsync(HttpClient admin)
    {
        var channel = await admin.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var created = await admin.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, Location: Shop));

        var outletId = (await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id;

        // The realm gives `admin` no `product:*` — writing the catalogue is a different job.
        using var writer = Rep();

        var products = new List<Guid>();

        for (var index = 0; index < 2; index++)
        {
            var product = await writer.PostAsJsonAsync(
                "/api/products", new CreateProductRequest(Unique("SKU"), "Veridian Still"));

            products.Add((await product.Content.ReadFromJsonAsync<ProductResponse>())!.Id);
        }

        var assorted = await writer.PutAsJsonAsync(
            $"/api/products/assortments/channels/{channelId}",
            new SetAssortmentRequest([.. products.Select(id => new AssortmentLineRequest(id))]));

        Assert.Equal(HttpStatusCode.OK, assorted.StatusCode);

        var response = await admin.PostAsJsonAsync(
            "/api/visits/check-in", new CheckInRequest(outletId, Shop.Latitude, Shop.Longitude));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return ((await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit.Id, products);
    }

}
