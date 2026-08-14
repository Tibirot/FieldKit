using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FieldKit.Modules.Order;
using FieldKit.Modules.Order.Contracts;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Products;
using FieldKit.Modules.Visit;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// The server prices the order again and says whether it agrees (<c>BR-ORD-6</c>, <c>ORD-08</c>) —
/// W11 slice 14.
/// </summary>
/// <remarks>
/// <para>
/// <b>Flagged, never applied.</b> An order's prices are what a rep and a shopkeeper agreed to, so the
/// device's numbers are the record and the server's arithmetic is an annotation beside them. That is
/// the opposite call to <c>BR-AUD-8</c>, where a recomputed score replaces the device's — a score is
/// a measurement, a price is an agreement, and only one of those is anybody's to correct after the
/// fact.
/// </para>
/// <para>
/// These go through the real ingest path with real price lists, because the interesting failure is
/// not arithmetic — it is *which day* the arithmetic runs against.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class OrderRepriceTests(ServerFixture fixture)
{
    private const string Lists = "/api/products/price-lists";
    private const string Zone = "Europe/Bucharest";

    /// <summary>A shop on Calea Dorobanți, and a doorway to stand in.</summary>
    private static readonly Coordinates Shop = new(44.4638, 26.0946);

    /// <summary>When the rep took the order — comfortably in the past, so "today" is a different day.</summary>
    private static readonly DateTimeOffset Captured = new(2026, 4, 6, 9, 45, 0, TimeSpan.Zero);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    private HttpClient Rep() => fixture.CreateAuthenticatedClient();

    [Fact]
    public async Task Records_what_it_made_of_the_order_beside_what_the_device_did()
    {
        // The device's arithmetic is right, so the two agree — and the annotation exists either way,
        // because "we looked and agreed" is a different fact from "we have not looked".
        var (visitId, productId) = await ShopWithOnePricedProductAsync("10.00");

        await SubmitAsync(visitId, productId, net: 20.00m, tax: 0m);

        var order = await StoredAsync(visitId);

        Assert.Equal(PriceAgreement.Agrees, order.Agreement);
        Assert.Equal(20.00m, order.ServerTotal);
        Assert.NotNull(order.RepricedAtUtc);
    }

    [Fact]
    public async Task Flags_a_disagreement_and_leaves_the_device_s_numbers_alone()
    {
        /*
         * <b>The whole rule in one assertion.</b> The device sent a total the server does not get —
         * a stale price list, a promotion the rep's device had and the server has withdrawn — and the
         * response is an annotation, not a correction.
         *
         * The second half matters more than the first: if `Total` had moved, the back office would be
         * holding a number nobody in the shop ever agreed to, and the rep would have no way to know.
         */
        var (visitId, productId) = await ShopWithOnePricedProductAsync("10.00");

        await SubmitAsync(visitId, productId, net: 17.50m, tax: 3.33m);

        var order = await StoredAsync(visitId);

        Assert.Equal(PriceAgreement.Differs, order.Agreement);

        // Untouched. This is the record.
        Assert.Equal(17.50m, order.Total);
        Assert.Equal(3.33m, order.TaxTotal);
        Assert.Equal(17.50m, order.Lines.Single().LineTotal);

        // And beside it, what the server got.
        Assert.Equal(20.00m, order.ServerTotal);
    }

    [Fact]
    public async Task Prices_against_the_day_the_order_was_taken_rather_than_today()
    {
        /*
         * <b>The reason `IPricingService` takes a date and refuses to read a clock.</b>
         *
         * Two price lists: the one that was in force when the rep stood in the shop, and the one that
         * replaced it afterwards. An order captured under the first is re-priced under the first — a
         * server that used *today* would report an ordinary mid-week price rise as the rep having got
         * it wrong, on every order taken before it.
         *
         * This is the test that would fail if someone "simplified" the date away, and nothing else
         * here would.
         */
        var channelId = await ChannelAsync();
        var outletId = await OutletAsync(channelId);
        var productId = await ProductAsync();
        await AssortAsync(channelId, productId);

        var capturedOn = DateOnly.FromDateTime(Captured.UtcDateTime);

        // In force when the order was taken, and retired the day after.
        await PriceListAsync(productId, "10.00", capturedOn.AddDays(-30), capturedOn.AddDays(1), outletId);

        // What the shop pays now, at twice the money.
        await PriceListAsync(productId, "20.00", capturedOn.AddDays(2), null, outletId);

        var visitId = await VisitAsync(outletId);

        // Priced at the old list, which is what the rep's device had.
        await SubmitAsync(visitId, productId, net: 20.00m, tax: 0m);

        var order = await StoredAsync(visitId);

        Assert.Equal(PriceAgreement.Agrees, order.Agreement);
        Assert.Equal(20.00m, order.ServerTotal);
    }

    [Fact]
    public async Task Flags_a_disagreement_about_the_tax_alone()
    {
        /*
         * <b>Tax is half the comparison, and this is what proves it.</b> The nets match exactly; only
         * the tax does not. A server that compared totals and ignored tax would call this agreement —
         * and would then be silent about the one number a shopkeeper is most likely to query.
         *
         * These shops have no tax rate configured, so the server's tax is zero. That is what makes
         * the case easy to construct here, and it is also a real configuration: `priceLine` reads a
         * missing rate as *unknown* rather than as nothing owed, and a tenant part-way through
         * setting tax up produces exactly this shape.
         */
        var (visitId, productId) = await ShopWithOnePricedProductAsync("10.00");

        await SubmitAsync(visitId, productId, net: 20.00m, tax: 3.80m);

        var order = await StoredAsync(visitId);

        Assert.Equal(PriceAgreement.Differs, order.Agreement);

        // The net agreed. The disagreement is entirely in the tax, and both numbers survive to say so.
        Assert.Equal(20.00m, order.ServerTotal);
        Assert.Equal(0m, order.ServerTaxTotal);
        Assert.Equal(3.80m, order.TaxTotal);
    }

    [Fact]
    public async Task Says_nothing_rather_than_disagreeing_when_a_line_has_no_price()
    {
        /*
         * A product this shop has no price for is a configuration gap, not a dispute. Totalling
         * around it would compare the device's whole order against the server's partial one and
         * report a difference the exact size of the missing line — which reads as "the rep charged
         * too much" and is nothing of the kind.
         */
        var channelId = await ChannelAsync();
        var outletId = await OutletAsync(channelId);
        var productId = await ProductAsync();

        // Assorted, so the order is not rejected — but never priced.
        await AssortAsync(channelId, productId);

        var visitId = await VisitAsync(outletId);

        await SubmitAsync(visitId, productId, net: 20.00m, tax: 3.80m);

        var order = await StoredAsync(visitId);

        Assert.Equal(PriceAgreement.NotRepriced, order.Agreement);
        Assert.Null(order.ServerTotal);

        // And the order is still stored, in full. A server that cannot check an order has not found
        // anything wrong with it.
        Assert.Equal(20.00m, order.Total);
    }

    [Fact]
    public async Task Keeps_the_tax_the_device_worked_out()
    {
        /*
         * The field the wire was missing for three slices. Before it, `LineTotal` was the net and
         * there was nowhere to put the rest, so the back office received every order short of its
         * VAT — and this comparison had nothing like-for-like to run against.
         */
        var (visitId, productId) = await ShopWithOnePricedProductAsync("10.00");

        await SubmitAsync(visitId, productId, net: 20.00m, tax: 3.80m);

        var order = await StoredAsync(visitId);

        Assert.Equal(3.80m, order.TaxTotal);
        Assert.Equal(3.80m, order.Lines.Single().TaxAmount);
    }

    [Fact]
    public async Task Keeps_what_the_device_priced_against()
    {
        // `ORD-08`. Six numbers rather than one, because pricing has six inputs that advance
        // independently — and the point of keeping them is to be able to say which one was stale.
        var (visitId, productId) = await ShopWithOnePricedProductAsync("10.00");

        await SubmitAsync(
            visitId,
            productId,
            net: 20.00m,
            tax: 3.80m,
            against: new PricingSnapshot(41, 118, 27, 9, 14, 6));

        var order = await StoredAsync(visitId);

        Assert.Equal(new PricingSnapshot(41, 118, 27, 9, 14, 6), order.CapturedAgainst);
    }

    [Fact]
    public async Task Takes_no_snapshot_from_a_device_that_did_not_send_one()
    {
        // Null, not zeros: a device that never said what it priced against and one that had pulled
        // nothing are different, and only the second means "priced against an empty catalogue".
        var (visitId, productId) = await ShopWithOnePricedProductAsync("10.00");

        await SubmitAsync(visitId, productId, net: 20.00m, tax: 3.80m, against: null);

        Assert.Null((await StoredAsync(visitId)).CapturedAgainst);
    }

    [Fact]
    public async Task Tells_a_reader_the_verdict_and_both_sides_arithmetic()
    {
        /*
         * <b>The finding, as a test</b> (regression F3). Every assertion above this one reads the
         * aggregate out of the DbContext — so the comparison was computed on every pushed order, was
         * correct, and reached nobody. `OrderResponse` carried the device's net and stopped there.
         *
         * The disagreement case, because it is the one with something to say: four numbers and a
         * word, and a reader who can act on the pair without re-deriving the rule.
         */
        var (visitId, productId) = await ShopWithOnePricedProductAsync("10.00");

        await SubmitAsync(visitId, productId, net: 17.50m, tax: 3.33m);

        var order = await Admin().GetFromJsonAsync<OrderReadback>($"/api/orders/by-visit/{visitId}");

        Assert.NotNull(order);

        // What the shop agreed to — the record, and still the first thing the response says.
        Assert.Equal(17.50m, order.Total);
        Assert.Equal(3.33m, order.TaxTotal);

        // And beside it, what this server made of it.
        Assert.Equal(20.00m, order.ServerTotal);
        Assert.Equal(0m, order.ServerTaxTotal);

        // The name, not the ordinal — `2` here would be a number the reader has to look up, and one
        // that moves the day a member is inserted into the middle of the enum.
        Assert.Equal("Differs", order.Agreement);
    }

    [Fact]
    public async Task Tells_a_reader_it_did_not_look_rather_than_that_it_agreed()
    {
        /*
         * <b>Three states, not two</b>, and this is the one a boolean would lose. An unpriced line
         * means the server has no opinion — and a response that reported that as agreement would send
         * an exception queue past exactly the orders it exists to catch.
         *
         * `NotRepriced` is also the default on `OrderDescriptor`, which is what makes this worth
         * asserting over the wire rather than trusting: a mapping that silently dropped the field
         * would produce this exact answer for every order in the system.
         */
        var channelId = await ChannelAsync();
        var outletId = await OutletAsync(channelId);
        var productId = await ProductAsync();

        await AssortAsync(channelId, productId);

        var visitId = await VisitAsync(outletId);

        await SubmitAsync(visitId, productId, net: 20.00m, tax: 3.80m);

        var order = await Admin().GetFromJsonAsync<OrderReadback>($"/api/orders/by-visit/{visitId}");

        Assert.NotNull(order);
        Assert.Equal("NotRepriced", order.Agreement);
        Assert.Null(order.ServerTotal);
        Assert.Null(order.ServerTaxTotal);

        // The device's tax survives the server having no tax of its own to compare it against.
        Assert.Equal(3.80m, order.TaxTotal);
    }

    [Fact]
    public async Task Says_the_same_to_a_reader_listing_a_shop_s_orders()
    {
        /*
         * The list arm shares `Respond`, so this asserts a mapping rather than a second rule — and it
         * is the arm an exception queue would actually be built on. A reader chasing disagreements
         * wants every order that has one, not one order they already knew the visit for.
         */
        var (visitId, productId) = await ShopWithOnePricedProductAsync("10.00");

        await SubmitAsync(visitId, productId, net: 17.50m, tax: 3.33m);

        var outletId = (await Admin().GetFromJsonAsync<OrderReadback>(
            $"/api/orders/by-visit/{visitId}"))!.OutletId;

        var orders = await Admin().GetFromJsonAsync<IReadOnlyList<OrderReadback>>(
            $"/api/orders/by-outlet/{outletId}");

        Assert.Equal("Differs", Assert.Single(orders!).Agreement);
    }

    private async Task<(Guid VisitId, Guid ProductId)> ShopWithOnePricedProductAsync(string amount)
    {
        var channelId = await ChannelAsync();
        var outletId = await OutletAsync(channelId);
        var productId = await ProductAsync();

        await AssortAsync(channelId, productId);
        await PriceListAsync(
            productId,
            amount,
            DateOnly.FromDateTime(Captured.UtcDateTime).AddDays(-30),
            null,
            outletId);

        return (await VisitAsync(outletId), productId);
    }

    private async Task<Guid> ChannelAsync()
    {
        var response = await Admin().PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    private async Task<Guid> OutletAsync(Guid channelId)
    {
        var response = await Admin().PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, Location: Shop));

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    private async Task<Guid> ProductAsync()
    {
        var response = await Rep().PostAsJsonAsync(
            "/api/products", new { sku = Unique("SKU"), name = "Cola 500ml" });

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!.Id;
    }

    /// <summary>Makes the product orderable at every outlet in the channel (<c>BR-ORD-1</c>).</summary>
    private async Task AssortAsync(Guid channelId, Guid productId)
    {
        var response = await Rep().PutAsJsonAsync(
            $"/api/products/assortments/channels/{channelId}",
            new SetAssortmentRequest([new AssortmentLineRequest(productId)]));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private async Task PriceListAsync(
        Guid productId, string amount, DateOnly from, DateOnly? to, Guid outletId)
    {
        var writer = Rep();

        var created = await writer.PostAsJsonAsync(
            Lists, new CreatePriceListRequest(Unique("List"), "RON", from, to));

        Assert.True(
            created.StatusCode == HttpStatusCode.Created,
            $"{created.StatusCode}: {await created.Content.ReadAsStringAsync()}");

        var listId = (await created.Content.ReadFromJsonAsync<PriceListResponse>())!.Id;

        await writer.PutAsJsonAsync(
            $"{Lists}/{listId}/prices",
            new SetPricesRequest([new PriceLineRequest(productId, amount)]));

        await writer.PutAsJsonAsync(
            $"{Lists}/{listId}/assignments", new SetAssignmentsRequest([], [outletId]));
    }

    private async Task<Guid> VisitAsync(Guid outletId)
    {
        var response = await Admin().PostAsJsonAsync(
            "/api/visits/check-in", new CheckInRequest(outletId, Shop.Latitude, Shop.Longitude));

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit.Id;
    }

    /// <summary>Two of the product, at ten each, however the caller says the device totalled it.</summary>
    private async Task SubmitAsync(
        Guid visitId,
        Guid productId,
        decimal net,
        decimal tax,
        PricingSnapshot? against = null)
    {
        var captured = new CapturedOrder(
            Guid.CreateVersion7(),
            visitId,
            "RON",
            net,
            Captured,
            [new CapturedOrderLine(productId, 2m, "unit", null, net / 2m, net, tax)],
            tax,
            against);

        var result = await AsAsync(async services =>
            await services.GetRequiredService<IOrderIngest>()
                .IngestAsync(captured, Guid.CreateVersion7(), SubjectOf(fixture.AdminAccessToken)));

        Assert.Equal(OrderIngestRefusal.None, result.Refusal);
    }

    private Task<Order> StoredAsync(Guid visitId) =>
        AsAsync(async services =>
            await services.GetRequiredService<OrderDbContext>().Orders
                .Include(order => order.Lines)
                .SingleAsync(order => order.VisitId == visitId));

    /// <summary>
    /// Runs <paramref name="work"/> under a tenant context built from the rep's real token — the
    /// approach <see cref="AuditIngestTests"/> explains at length.
    /// </summary>
    private async Task<T> AsAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = fixture.Services.CreateScope();

        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;

        accessor.HttpContext = new DefaultHttpContext { User = PrincipalOf(fixture.AdminAccessToken) };

        try
        {
            return await work(scope.ServiceProvider);
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }

    private static string SubjectOf(string token) => PrincipalOf(token).FindFirst("sub")!.Value;

    /// <summary>
    /// The order as an HTTP reader sees it — the narrow slice this file is about.
    /// </summary>
    /// <remarks>
    /// A record of its own rather than <c>OrderResponse</c>, the way every other readback in these
    /// tests is written. Deserialising into the server's own DTO would make the field names move
    /// together — a rename would rebuild the test alongside the endpoint and stay green while every
    /// real consumer broke. These names are written out once so that they have to be kept.
    /// </remarks>
    private sealed record OrderReadback(
        Guid Id,
        Guid OutletId,
        decimal Total,
        decimal TaxTotal,
        decimal? ServerTotal,
        decimal? ServerTaxTotal,
        string Agreement);

    private static ClaimsPrincipal PrincipalOf(string token)
    {
        var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        var padded = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');

        using var document = JsonDocument.Parse(Convert.FromBase64String(padded));

        return new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("tenant", document.RootElement.GetProperty("tenant").GetString()!),
                new Claim("sub", document.RootElement.GetProperty("sub").GetString()!),
            ],
            "test"));
    }
}
