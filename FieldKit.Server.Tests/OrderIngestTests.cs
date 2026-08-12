using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FieldKit.Infrastructure.Outbox;
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
/// Applying an order a device captured offline (<c>ORD-07</c>, <c>OFF-04</c>) — W11 slice 1.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="OrderRecordTests"/> covers what a stored order must be true of. Asserted here is
/// everything the aggregate cannot see: that the visit exists and is this rep's, that a sealed visit
/// refuses a new order, that a replay is a success, and that the read endpoints answer with what was
/// stored.
/// </para>
/// <para>
/// <b>Why this calls the service rather than an endpoint.</b> There is no HTTP write path, on
/// purpose — an order is taken at a counter with no signal and arrives through <c>/sync/push</c>,
/// wired in W11 slice 5. So these resolve <see cref="IOrderIngest"/> from the running server's own
/// container, which means standing up a tenant context by hand.
/// </para>
/// <para>
/// <b>That harness is copied from <c>AuditIngestTests</c>, and this is the second copy.</b> Both
/// exist because <c>KeycloakTenantContext</c> throws without an authenticated request — deliberately,
/// so a tenant-owned query can never run unscoped — and reaching around it with a stub would test a
/// different tenant context from the one the server uses. A third module written only through
/// <c>/sync/push</c> is the point at which this should be extracted rather than copied again.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class OrderIngestTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    /// <summary>A shop on Calea Dorobanți, and a doorway to stand in.</summary>
    private static readonly Coordinates Shop = new(44.4638, 26.0946);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    private static CapturedOrder Captured(Guid visitId, params CapturedOrderLine[] lines) => new(
        Guid.CreateVersion7(),
        visitId,
        "EUR",
        lines.Sum(line => line.LineTotal),
        DateTimeOffset.Parse("2026-08-11T09:30:00Z"),
        lines.Length == 0 ? [Line()] : lines);

    /// <summary>
    /// A line. <paramref name="productId"/> defaults to a product no shop stocks.
    /// </summary>
    /// <remarks>
    /// That default is load-bearing since W11 slice 4b: an unnamed product is <b>not assorted</b>, so
    /// an order carrying one is stored and immediately rejected (<c>BR-ORD-1</c>). Tests that expect
    /// an order to stand pass a product the fixture stocked; tests that expect a refusal before the
    /// gate — a duplicate line, an unknown visit — do not have to care.
    /// </remarks>
    private static CapturedOrderLine Line(
        Guid? productId = null, decimal quantity = 6m, decimal lineTotal = 27.00m) =>
        new(productId ?? Guid.CreateVersion7(), quantity, "case", 12, 4.50m, lineTotal);

    [Fact]
    public async Task An_order_is_stored_against_the_visits_outlet_and_read_back()
    {
        using var admin = Admin();

        var (visitId, outletId, assorted) = await VisitAsync(admin);
        var captured = Captured(visitId, Line(assorted[0]));

        var result = await IngestAsync(captured);
        Assert.Equal(OrderIngestRefusal.None, result.Refusal);

        var response = await admin.GetAsync($"/api/orders/by-visit/{visitId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var order = (await response.Content.ReadFromJsonAsync<OrderReadback>())!;

        Assert.Equal(outletId, order.OutletId);
        Assert.Equal("EUR", order.CurrencyCode);

        // The name, not the ordinal — the response renders it rather than leaning on a converter.
        Assert.Equal("Submitted", order.Status);
        Assert.Equal(27.00m, order.Total);
        Assert.Equal("case", Assert.Single(order.Lines).UnitOfMeasure);
    }

    [Fact]
    public async Task Another_reps_visit_is_refused_the_same_way_a_missing_one_is()
    {
        // A device sends ids it read out of its own store. Distinguishing "no such visit" from "not
        // yours" would make this a way to discover whose visits exist — the same call Audit makes.
        using var admin = Admin();

        var (visitId, _, assorted) = await VisitAsync(admin);

        var stranger = await IngestAsync(Captured(visitId, Line()), userId: "someone-else");
        var missing = await IngestAsync(Captured(Guid.CreateVersion7(), Line()));

        Assert.Equal(OrderIngestRefusal.UnknownVisit, stranger.Refusal);
        Assert.Equal(OrderIngestRefusal.UnknownVisit, missing.Refusal);
        Assert.Equal(stranger.Message, missing.Message);
    }

    [Fact]
    public async Task A_repeat_of_the_same_order_succeeds_even_after_the_visit_is_sealed()
    {
        /*
         * The window that matters. Order and Sync commit separately, so a mutation can land and lose
         * its ledger entry; the device retries. If the replay check ran *after* the visit lookup, a
         * retry arriving once the rep had checked out would be refused forever — work that is done,
         * with no way back.
         */
        using var admin = Admin();

        var (visitId, _, assorted) = await VisitAsync(admin);
        var captured = Captured(visitId, Line(assorted[0]));

        // The *same* mutation id both times — that is what makes this a replay rather than a second
        // submission, and from W11 slice 3 the difference is what the lock turns on.
        var mutationId = Guid.CreateVersion7();

        Assert.Equal(
            OrderIngestRefusal.None, (await IngestAsync(captured, mutationId: mutationId)).Refusal);

        var checkedOut = await admin.PostAsJsonAsync(
            $"/api/visits/{visitId}/check-out", new CheckOutRequest(VisitOutcome.Productive));
        Assert.Equal(HttpStatusCode.OK, checkedOut.StatusCode);

        Assert.Equal(
            OrderIngestRefusal.None, (await IngestAsync(captured, mutationId: mutationId)).Refusal);
    }

    [Fact]
    public async Task A_second_submission_of_a_submitted_order_is_refused_rather_than_taken_as_a_replay()
    {
        /*
         * `BR-ORD-4`, and the gap W11 slice 3 closed. Until then the replay test was the *order* id,
         * so this — a different push, different lines, same order — was silently accepted as a retry
         * and then ignored. An edit after submit, wearing a retry's clothes, and the rule that
         * forbids it enforced by nothing.
         *
         * `BR-ORD-9`'s rejected order is the documented exception and re-opens for exactly this;
         * nothing can reject one until slice 4.
         */
        using var admin = Admin();

        var (visitId, _, assorted) = await VisitAsync(admin);
        var captured = Captured(visitId, Line(assorted[0]));

        Assert.Equal(OrderIngestRefusal.None, (await IngestAsync(captured)).Refusal);

        var edited = captured with { Lines = [Line(quantity: 99m, lineTotal: 445.50m)] };

        var second = await IngestAsync(edited);

        Assert.Equal(OrderIngestRefusal.AlreadySubmitted, second.Refusal);
        Assert.Contains("cannot be changed", second.Message);

        // …and the stored order is still the one the rep actually sealed.
        var stored = await admin.GetFromJsonAsync<OrderReadback>($"/api/orders/by-visit/{visitId}");

        Assert.Equal(6m, Assert.Single(stored!.Lines).Quantity);
    }

    [Fact]
    public async Task Submitting_an_order_announces_it()
    {
        /*
         * `OrderSubmitted` on the outbox, in the same transaction as the rows (ADR-0006) — so a
         * subscriber cannot learn of an order that failed to store, and a stored order cannot go
         * unlearned-of.
         *
         * Asserted through the outbox table rather than a subscriber, because there is none yet:
         * this is the boundary the reporting read-side consumes in W12, and the event has to exist
         * before it can be subscribed to.
         */
        using var admin = Admin();

        var (visitId, outletId, assorted) = await VisitAsync(admin);
        var captured = Captured(visitId, Line(assorted[0]));

        Assert.Equal(OrderIngestRefusal.None, (await IngestAsync(captured)).Refusal);

        var payloads = await AsAsync(fixture.AdminAccessToken, async services =>
        {
            var db = services.GetRequiredService<OrderDbContext>();

            // Filtered by type in the database and by payload in memory: Content is jsonb and
            // Postgres has no LIKE for it. The shape VisitCheckOutTests uses, for the same reason.
            return await db.Set<OutboxMessage>()
                .Where(message => message.Type.Contains(nameof(OrderSubmitted)))
                .Select(message => message.Content)
                .ToListAsync();
        });

        var mine = Assert.Single(payloads.Where(content =>
            content.Contains(captured.OrderId.ToString(), StringComparison.OrdinalIgnoreCase)));

        using var document = JsonDocument.Parse(mine);
        var root = document.RootElement;

        Assert.Equal(captured.OrderId, root.GetProperty("OrderId").GetGuid());
        Assert.Equal(outletId, root.GetProperty("OutletId").GetGuid());
        Assert.Equal("EUR", root.GetProperty("CurrencyCode").GetString());
        Assert.Equal(27.00m, root.GetProperty("Total").GetDecimal());
        Assert.Equal(1, root.GetProperty("LineCount").GetInt32());

        // The capture time travels, and is not the same as when this server heard about it.
        Assert.Equal(
            captured.CapturedAtUtc, root.GetProperty("CapturedAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task An_order_taken_before_check_out_is_accepted_after_it()
    {
        /*
         * The regression W11 slice 8d exists for, and this test used to assert the opposite.
         *
         * It was called `A_sealed_visit_refuses_a_new_order`, checked the visit out, and expected
         * `UnknownVisit` — while sending a capture time days *before* the check-out. That is not a
         * new order on a filed visit; it is what every order a rep takes at a counter looks like. A
         * pushed `CapturedVisit` is created already sealed and a device only enqueues one at
         * check-out, so an offline order always arrives at a sealed visit.
         *
         * W11 slice 8c held the order until the visit had landed, which was right about the ordering
         * and turned `UnknownVisit`-because-missing into `UnknownVisit`-because-sealed. Nothing
         * caught that: this test asserted the refusal, and the device suite mocks the API so it never
         * meets one. It took driving the audit screen in a browser.
         */
        using var admin = Admin();

        var (visitId, _, assorted) = await VisitAsync(admin);

        var checkedOut = await admin.PostAsJsonAsync(
            $"/api/visits/{visitId}/check-out", new CheckOutRequest(VisitOutcome.Productive));
        Assert.Equal(HttpStatusCode.OK, checkedOut.StatusCode);

        var result = await IngestAsync(Captured(visitId, Line(assorted[0])));

        Assert.Equal(OrderIngestRefusal.None, result.Refusal);
    }

    [Fact]
    public async Task An_order_taken_after_check_out_is_refused()
    {
        // The rule that survives: an order placed *after* the visit was filed would change a record
        // already counted. The moment decides it, not the flag.
        using var admin = Admin();

        var (visitId, _, assorted) = await VisitAsync(admin);

        var checkedOut = await admin.PostAsJsonAsync(
            $"/api/visits/{visitId}/check-out", new CheckOutRequest(VisitOutcome.Productive));
        Assert.Equal(HttpStatusCode.OK, checkedOut.StatusCode);

        // Read back rather than reached for from a clock:  is banned in this
        // project, and taking the moment from the response is the exact boundary anyway.
        var sealedAt = (await checkedOut.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit.CheckedOutAtUtc!.Value;

        var late = Captured(visitId, Line(assorted[0])) with { CapturedAtUtc = sealedAt.AddSeconds(1) };

        var result = await IngestAsync(late);

        Assert.Equal(OrderIngestRefusal.UnknownVisit, result.Refusal);
    }

    [Fact]
    public async Task An_order_the_aggregate_refuses_says_which_rule_it_broke()
    {
        using var admin = Admin();

        var (visitId, _, assorted) = await VisitAsync(admin);
        var duplicate = Line();

        var result = await IngestAsync(
            Captured(visitId) with { Lines = [duplicate, duplicate] });

        Assert.Equal(OrderIngestRefusal.Invalid, result.Refusal);
        Assert.Contains("more than one line", result.Message);
    }

    [Fact]
    public async Task An_outlets_orders_come_back_newest_captured_first()
    {
        /*
         * By when the rep *captured* them, not when this server heard. Two orders pushed in one
         * batch arrive milliseconds apart, so ordering by arrival would put a Thursday order taken
         * in a car park ahead of the Tuesday one it was queued behind.
         *
         * The later-captured order is stored *first* so that a sort by arrival would fail this.
         */
        using var admin = Admin();

        var (firstVisit, outletId, assorted) = await VisitAsync(admin);

        var later = Captured(firstVisit, Line(assorted[0])) with
        {
            CapturedAtUtc = DateTimeOffset.Parse("2026-08-11T16:00:00Z"),
        };

        Assert.Equal(OrderIngestRefusal.None, (await IngestAsync(later)).Refusal);

        var (secondVisit, _, _) = await VisitAsync(admin, outletId, assorted);

        var earlier = Captured(secondVisit, Line(assorted[1])) with
        {
            CapturedAtUtc = DateTimeOffset.Parse("2026-08-11T08:00:00Z"),
        };

        Assert.Equal(OrderIngestRefusal.None, (await IngestAsync(earlier)).Refusal);

        var orders = await admin.GetFromJsonAsync<List<OrderReadback>>(
            $"/api/orders/by-outlet/{outletId}");

        Assert.Equal([later.OrderId, earlier.OrderId], orders!.Select(order => order.Id));
    }

    [Fact]
    public async Task A_visit_with_no_order_is_a_404_rather_than_an_empty_one()
    {
        // "No order was taken" and "an order for nothing" are different facts, and an empty order is
        // one the aggregate refuses to store in the first place.
        using var admin = Admin();

        var (visitId, _, assorted) = await VisitAsync(admin);

        var response = await admin.GetAsync($"/api/orders/by-visit/{visitId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Ingests under a fresh mutation id unless the caller is testing a replay.</summary>
    private Task<OrderIngestResult> IngestAsync(
        CapturedOrder captured, string? userId = null, Guid? mutationId = null) =>
        AsAsync(fixture.AdminAccessToken, services => services
            .GetRequiredService<IOrderIngest>()
            .IngestAsync(
                captured,
                mutationId ?? Guid.CreateVersion7(),
                userId ?? SubjectOf(fixture.AdminAccessToken)));

    /// <summary>
    /// A visit at a shop that is allowed to buy <see cref="Assorted"/>.
    /// </summary>
    /// <remarks>
    /// <b>The assorted products arrived with W11 slice 4b</b>, and their absence is what made these
    /// tests pass before it. Every order here used to name a freshly minted <c>Guid</c>, which is by
    /// definition not in any outlet's assortment — so once <c>BR-ORD-1</c> was enforced, every one of
    /// them came back <c>Rejected</c>. The fixture now stocks the shop, which is what a rep ordering
    /// from their own catalogue actually looks like.
    /// </remarks>
    private sealed record Shopfront(Guid VisitId, Guid OutletId, IReadOnlyList<Guid> Assorted);

    private async Task<Shopfront> VisitAsync(
        HttpClient client, Guid? existingOutlet = null, IReadOnlyList<Guid>? existingAssorted = null)
    {
        var (outletId, assorted) = existingOutlet is { } known
            ? (known, existingAssorted!)
            : await OutletAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/visits/check-in", new CheckInRequest(outletId, Shop.Latitude, Shop.Longitude));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var visit = (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;

        return new Shopfront(visit.Id, outletId, assorted);
    }

    private async Task<(Guid OutletId, IReadOnlyList<Guid> Assorted)> OutletAsync(HttpClient client)
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var created = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, Location: Shop));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var outletId = (await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id;

        // A separate client, because the realm deliberately gives `admin` no `product:*` — writing
        // the catalogue is a different job from administering the tenant.
        using var writer = fixture.CreateAuthenticatedClient();

        var products = new List<Guid>();

        for (var i = 0; i < 3; i++)
        {
            var product = await writer.PostAsJsonAsync(
                "/api/products", new CreateProductRequest(Unique("SKU"), "Veridian Still"));

            Assert.Equal(HttpStatusCode.Created, product.StatusCode);

            products.Add((await product.Content.ReadFromJsonAsync<ProductResponse>())!.Id);
        }

        var assorted = await writer.PutAsJsonAsync(
            $"/api/products/assortments/channels/{channelId}",
            new SetAssortmentRequest([.. products.Select(id => new AssortmentLineRequest(id))]));

        Assert.Equal(HttpStatusCode.OK, assorted.StatusCode);

        return (outletId, products);
    }

    /// <summary>Runs <paramref name="work"/> in a scope whose tenant context matches a real token.</summary>
    private async Task<T> AsAsync<T>(string token, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = fixture.Services.CreateScope();

        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;

        accessor.HttpContext = new DefaultHttpContext { User = PrincipalOf(token) };

        try
        {
            return await work(scope.ServiceProvider);
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }

    /// <summary>The claims inside a JWT, without validating it — the server already did that.</summary>
    private static ClaimsPrincipal PrincipalOf(string token)
    {
        var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        var padded = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');

        using var document = JsonDocument.Parse(Convert.FromBase64String(padded));

        var claims = new List<Claim>
        {
            new("tenant", document.RootElement.GetProperty("tenant").GetString()!),
            new("sub", document.RootElement.GetProperty("sub").GetString()!),
        };

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static string SubjectOf(string token) => PrincipalOf(token).FindFirst("sub")!.Value;

    private sealed record OrderLineReadback(string UnitOfMeasure, decimal Quantity, decimal LineTotal);

    private sealed record OrderReadback(
        Guid Id,
        Guid OutletId,
        string Status,
        string CurrencyCode,
        decimal Total,
        IReadOnlyList<OrderLineReadback> Lines);
}


